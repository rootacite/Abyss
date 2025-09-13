using System.Net;
using System.Net.Sockets;
using System.Text;
using Abyss.Components.Tools;

namespace Abyss.Components.Services;

public class AbyssService(ILogger<AbyssService> logger, ConfigureService config) : IHostedService, IDisposable
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
        return;
    }

    private async Task ClientHandlerAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var stream = await client.GetAbyssStreamAsync(ct: cancellationToken);

        try
        {
            var request = HttpHelper.Parse(await HttpReader.ReadHttpMessageAsync(stream, cancellationToken));
            var port = 80;
            var sp = request.RequestUri?.ToString().Split(':') ?? [];
            if (sp.Length == 2)
            {
                port = int.Parse(sp[1]);
            }
            if (request.Method == "CONNECT")
            {
                TcpClient upClient = new TcpClient();
                await upClient.ConnectAsync("127.0.0.1", port, cancellationToken);

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
                var response = HttpHelper.BuildHttpResponse(
                    200,
                    "Connection established",
                    new Dictionary<string, string>
                    {
                        ["Proxy-Agent"] = "Abyss/0.1",
                        ["Connection"] = "keep-alive"
                    });
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response), cancellationToken);
                // Connection established
                
                logger.LogInformation($"Tunnel for {client.Client.RemoteEndPoint} and upstream {upClient.Client.RemoteEndPoint} created");
                await UpStreamTunnelAsync(stream, upstream, cancellationToken);
                logger.LogInformation($"Tunnel for {client.Client.RemoteEndPoint} and upstream {upClient.Client.RemoteEndPoint} will be release");
                
                upstream.Close();
                upClient.Close();
                upClient.Dispose();
            }
            else
            {
                string htmlContent = """
                                     <html>
                                         <head>
                                             <title>405 Method Not Allowed</title>
                                         </head>
                                         <body>
                                             <h1>Method Not Allowed</h1>
                                             <p>The requested HTTP method is not supported by this proxy server.</p>
                                         </body>
                                     </html>
                                     """;
                byte[] responseBytes = Encoding.UTF8.GetBytes(htmlContent);
                
                var response = HttpHelper.BuildHttpResponse(
                    405,
                    "Method Not Allowed",
                    new Dictionary<string, string>
                    {
                        ["Allow"] = "CONNECT",
                        ["Content-Type"] = "text/html; charset=utf-8",
                        ["Content-Length"] = responseBytes.Length.ToString() 
                    }, htmlContent);
                
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response), cancellationToken);
                throw new Exception("Method Not Allowed");
            }
        }
        catch (Exception e)
        {
            logger.LogError(e.Message);
        }
        finally
        {
            stream.Close();
            client.Close();
            client.Dispose();
        }
    }

    private async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener.Start();
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
        logger.LogInformation("TCP listener stopped");
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