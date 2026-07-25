using System.Net;
using System.Net.Sockets;
using System.Text;
using Abyss.Components.Services.Misc;
using Abyss.Components.Tools;

namespace Abyss.Components.Services.Security;

public class AbyssService(ILogger<AbyssService> logger, ConfigureService config, UserService user) : IHostedService, IDisposable
{
    private Task? _executingTask;
    private CancellationTokenSource? _cts;
    private readonly TcpListener _listener = new TcpListener(IPAddress.Any, 4096);
    public readonly int[] AllowedPorts = config.AllowedPorts.Split(' ').Select(int.Parse).ToArray();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _executingTask = ExecuteAsync(_cts.Token);
        return _executingTask.IsCompleted ? _executingTask : Task.CompletedTask;
    }

    private async Task UpStreamTunnelAsync(AbyssStream client, NetworkStream upstream, CancellationToken token)
    {
        var tunnelUp = Task.Run(async () =>
        {
            byte[] buffer = new byte[4096];
            while (!token.IsCancellationRequested)
            {
                int bytesRead = await client.ReadAsync(buffer, 0, buffer.Length, token);
                if (bytesRead == 0) 
                    break;
                await upstream.WriteAsync(buffer, 0, bytesRead, token);
            }
        });
        
        var tunnelDown = Task.Run(async () =>
        {
            byte[] buffer = new byte[4096];
            while (!token.IsCancellationRequested)
            {
                int bytesRead = await upstream.ReadAsync(buffer, 0, buffer.Length, token);
                if (bytesRead == 0) 
                    break;
                
                await client.WriteAsync(buffer, 0, bytesRead, token);
            }
        });

        await Task.WhenAny(tunnelUp, tunnelDown);
    }

private async Task ClientHandlerAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = await client.GetAbyssStreamAsync(ct: cancellationToken, us: user);

            // 1. Keep the raw string to forward later for non-CONNECT methods
            string rawRequest = await HttpReader.ReadHttpMessageAsync(stream, cancellationToken);
            if (string.IsNullOrWhiteSpace(rawRequest)) return;

            var request = HttpHelper.Parse(rawRequest);
            string targetHost;
            int targetPort;
            string requestUri = request.RequestUri?.ToString() ?? "";

            // 2. Correctly parse Host and Port based on HTTP Proxy standards
            if (request.Method == "CONNECT")
            {
                // CONNECT uses the Authority form (e.g., example.com:443)
                var parts = requestUri.Split(':');
                targetHost = parts[0];
                targetPort = parts.Length > 1 ? int.Parse(parts[1]) : 443; // 443 is default for HTTPS
            }
            else
            {
                // GET/POST usually use the Absolute form in proxy requests (e.g., http://example.com/path)
                if (Uri.TryCreate(requestUri, UriKind.Absolute, out Uri? uri))
                {
                    targetHost = uri.Host;
                    targetPort = uri.Port > 0 ? uri.Port : 80;
                }
                else
                {
                    // Fallback: Extract from Host header if RequestUri is just a path (e.g., /path)
                    var hostMatch = System.Text.RegularExpressions.Regex.Match(rawRequest, @"(?im)^Host:\s*([^\r\n]+)");
                    if (hostMatch.Success)
                    {
                        var parts = hostMatch.Groups[1].Value.Trim().Split(':');
                        targetHost = parts[0];
                        targetPort = parts.Length > 1 ? int.Parse(parts[1]) : 80;
                    }
                    else
                    {
                        throw new Exception("Unable to determine target host from request.");
                    }
                }
            }

            // 3. Connect to the upstream server
            using TcpClient upClient = new TcpClient();
            await upClient.ConnectAsync(targetHost, targetPort, cancellationToken);

            if (!upClient.Connected)
            {
                var err1 = HttpHelper.BuildHttpResponse(
                    504,
                    "Gateway Timeout",
                    new Dictionary<string, string>
                    {
                        ["Proxy-Agent"] = "Abyss/0.1",
                        ["Content-Length"] = "0"
                    });
                await stream.WriteAsync(Encoding.UTF8.GetBytes(err1), cancellationToken);
                throw new Exception("Gateway Timeout");
            }

            var upstream = upClient.GetStream();

            // 4. Handle proxy transmission methodology
            if (request.Method == "CONNECT")
            {
                // For HTTPS (CONNECT), we must tell the client the tunnel is established
                var response = HttpHelper.BuildHttpResponse(
                    200,
                    "Connection established",
                    new Dictionary<string, string>
                    {
                        ["Proxy-Agent"] = "Abyss/0.1",
                        ["Connection"] = "keep-alive"
                    });
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response), cancellationToken);
            }
            else
            {
                // For HTTP (GET/POST), we must forward the initial HTTP request we already consumed
                byte[] initialRequestBytes = Encoding.UTF8.GetBytes(rawRequest);
                await upstream.WriteAsync(initialRequestBytes, 0, initialRequestBytes.Length, cancellationToken);
            }

            // 5. Pipe the streams for subsequent data (HTTP Keep-Alive or HTTPS encrypted frames)
            logger.LogInformation($"Tunnel for {client.Client.RemoteEndPoint} and upstream {targetHost}:{targetPort} created");
            await UpStreamTunnelAsync(stream, upstream, cancellationToken);
            logger.LogInformation($"Tunnel for {client.Client.RemoteEndPoint} and upstream {targetHost}:{targetPort} will be release");

            // Note: Streams are closed safely when exiting 'using' block or disposed below
            upstream.Close();
        }
        catch (Exception e)
        {
            logger.LogError($"[Proxy Error] {e.Message}");
        }
        finally
        {
            client.Close();
            client.Dispose();
        }
    }

    private async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener.Start();
        logger.LogInformation("Abyss listening on: {}", _listener.LocalEndpoint);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var c = await _listener.AcceptTcpClientAsync(stoppingToken);
                _ = Task.Run(() => ClientHandlerAsync(c, stoppingToken), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error occurred in background service");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }

        _listener.Stop();
        logger.LogInformation("Abyss listener stopped");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_executingTask == null)
            return;

        try
        {
            _cts?.CancelAsync();
        }
        finally
        {
            await Task.WhenAny(_executingTask,
                Task.Delay(Timeout.Infinite, cancellationToken));
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }
}