using System.Text;
using System.Text.RegularExpressions;
using Abyss.Components.Services.Misc;
using Abyss.Components.Tools;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using Newtonsoft.Json.Linq;

namespace Abyss.Components.Services.Security;

public sealed class AbyssDiscoverService(
    ILogger<AbyssDiscoverService> logger,
    ConfigureService config,
    IHostApplicationLifetime lifetime,
    IServer server) : IHostedService, IDisposable
{
    private static readonly Regex IdPattern = new("^[0-9a-fA-F]{32}$", RegexOptions.Compiled);

    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenRegistration _started;
    private IManagedMqttClient? _client;
    private Task? _run;
    private DiscoverEndpoint? _endpoint;
    private string? _topic;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.Discover))
            return Task.CompletedTask;
        if (!DiscoverChannel.TryParse(config.Discover, out var endpoint))
        {
            logger.LogError("discover url is invalid");
            return Task.CompletedTask;
        }

        _endpoint = endpoint;
        _started = lifetime.ApplicationStarted.Register(() =>
        {
            _run = Task.Run(() => RunAsync(_cts.Token));
        });
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _cts.CancelAsync();
        if (_client != null)
        {
            try
            {
                await _client.StopAsync();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "discover client stop");
            }
        }

        if (_run != null)
        {
            try
            {
                await _run.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public void Dispose()
    {
        _started.Dispose();
        _cts.Cancel();
        _client?.Dispose();
        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken token)
    {
        var endpoint = _endpoint;
        if (endpoint == null)
            return;

        try
        {
            _topic = DiscoverChannel.Topic(endpoint.Magic);
            var factory = new MqttFactory();
            var client = factory.CreateManagedMqttClient();
            _client = client;
            client.ApplicationMessageReceivedAsync += OnMessage;
            client.ConnectingFailedAsync += args =>
            {
                logger.LogWarning(args.Exception, "discover broker {Host}:{Port} unreachable", endpoint.Host, endpoint.Port);
                return Task.CompletedTask;
            };

            var clientOptions = new MqttClientOptionsBuilder()
                .WithTcpServer(endpoint.Host, endpoint.Port)
                .WithClientId("abyss-" + Guid.NewGuid().ToString("N")[..12])
                .WithCleanSession()
                .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V311)
                .Build();
            var managed = new ManagedMqttClientOptionsBuilder()
                .WithClientOptions(clientOptions)
                .WithAutoReconnectDelay(TimeSpan.FromSeconds(5))
                .Build();

            await client.StartAsync(managed);
            await client.SubscribeAsync(_topic, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce);
            logger.LogInformation("discover listening via {Host}:{Port}", endpoint.Host, endpoint.Port);
            await Task.Delay(Timeout.Infinite, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "discover stopped");
        }
    }

    private string? AdvertiseUrl()
    {
        if (!string.IsNullOrWhiteSpace(config.PublicUrl))
            return PublicAddress.EnsureTrailingSlash(config.PublicUrl);

        var feature = server.Features.Get<IServerAddressesFeature>();
        var listening = feature?.Addresses ?? Array.Empty<string>();
        return PublicAddress.AbyssUrl(PublicAddress.LocalAddresses(), listening);
    }

    private Task OnMessage(MqttApplicationMessageReceivedEventArgs args)
    {
        var endpoint = _endpoint;
        var topic = _topic;
        var client = _client;
        if (endpoint == null || topic == null || client == null)
            return Task.CompletedTask;
        if (!string.Equals(args.ApplicationMessage.Topic, topic, StringComparison.Ordinal))
            return Task.CompletedTask;

        try
        {
            var payload = args.ApplicationMessage.PayloadSegment.ToArray();
            var plain = DiscoverChannel.Open(payload, endpoint.Magic);
            if (plain == null)
                return Task.CompletedTask;

            var obj = JObject.Parse(Encoding.UTF8.GetString(plain));
            if (obj.Value<int?>("v") != 1)
                return Task.CompletedTask;
            if (obj.Value<string>("op") != "hello")
                return Task.CompletedTask;
            var id = obj.Value<string>("id");
            if (id == null || !IdPattern.IsMatch(id))
                return Task.CompletedTask;

            id = id.ToLowerInvariant();
            var advertise = AdvertiseUrl();
            if (advertise == null)
            {
                var feature = server.Features.Get<IServerAddressesFeature>();
                var listening = feature?.Addresses ?? Array.Empty<string>();
                logger.LogWarning(
                    "discover has no public address for {Id}; listening on {Addresses}",
                    id,
                    listening.Count == 0 ? "(none)" : string.Join(", ", listening));
                return Task.CompletedTask;
            }

            logger.LogInformation("discover offering {Url} to {Id}", advertise, id);
            var offer = DiscoverChannel.Seal(
                Encoding.UTF8.GetBytes(DiscoverChannel.OfferJson(id, advertise)),
                endpoint.Magic);
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic + "/r/" + id)
                .WithPayload(offer)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            return client.EnqueueAsync(message);
        }
        catch (Exception)
        {
            return Task.CompletedTask;
        }
    }
}
