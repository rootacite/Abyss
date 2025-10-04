using System.Net.Sockets;
using System.Text;

using Abyss.Components.Static;
using Abyss.Model.Admin;
using Newtonsoft.Json;

using System.Reflection;
using Abyss.Components.Services.Admin.Interfaces;
using Module = Abyss.Components.Services.Admin.Attributes.Module;

namespace Abyss.Components.Services.Admin;

public class CtlService(ILogger<CtlService> logger, IServiceProvider serviceProvider) : IHostedService
{
    private readonly string _socketPath = "ctl.sock";

    private Task? _executingTask;
    private CancellationTokenSource? _cts;
    private Dictionary<int, Type> _handlers = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var t = Module.Modules;
        foreach (var module in t)
        {
            var attr = module.GetCustomAttribute<Module>();
            if (attr != null)
            {
                _handlers[attr.Head] = module;
            }
        }
        
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _executingTask = ExecuteAsync(_cts.Token);
        return _executingTask.IsCompleted ? _executingTask : Task.CompletedTask;
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

    private async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (File.Exists(_socketPath))
        {
            File.Delete(_socketPath);
        }

        var endPoint = new UnixDomainSocketEndPoint(_socketPath);

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(endPoint);
        socket.Listen(5);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var clientSocket = await socket.AcceptAsync(stoppingToken);
                _ = HandleClientAsync(clientSocket, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task HandleClientAsync(Socket clientSocket, CancellationToken stoppingToken)
    {
        async Task _400()
        {
            await clientSocket.WriteBase64Async(Ctl.MakeBase64(400, ["Bad Request"]), stoppingToken);
        }
        
        try
        {
            var s = Encoding.UTF8.GetString(
                Convert.FromBase64String(await clientSocket.ReadBase64Async(stoppingToken)));
            var json = JsonConvert.DeserializeObject<Ctl>(s);

            if (json == null || !_handlers.TryGetValue(json.Head, out var handler))
            {
                await _400();
                return;
            }
            
            var module = (serviceProvider.GetRequiredService(handler) as IModule)!;
            var r = await module.ExecuteAsync(json, stoppingToken);
            await clientSocket.WriteBase64Async(Ctl.MakeBase64(r.Head, r.Params), stoppingToken);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error while handling client connection");
        }
    }
}