
using System.Net.Sockets;
using System.Text;
using abyssctl.App.Modules;
using abyssctl.Model;
using abyssctl.Static;
using CommandLine;
using Newtonsoft.Json;

namespace abyssctl.App;

public class App
{
    private static readonly string SocketPath = "ctl.sock";
    
    public static async Task<Ctl> CtlWriteRead(Ctl ctl)
    {
        var endPoint = new UnixDomainSocketEndPoint(SocketPath);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(endPoint);
            await socket.WriteBase64Async(Ctl.MakeBase64(ctl.Head, ctl.Params));
            var s = Encoding.UTF8.GetString(
                Convert.FromBase64String(await socket.ReadBase64Async()));
            return JsonConvert.DeserializeObject<Ctl>(s)!;
        }
        catch (Exception e)
        {
            return new Ctl
            {
                Head = 500,
                Params = [e.Message]
            };
        }
    }
    
    public async Task<int> RunAsync(string[] args)
    {
        return await Task.Run(() =>
        {
            return Parser.Default.ParseArguments<HelloOptions, VersionOptions>(args)
                .MapResult(
                    (HelloOptions opt) => HelloOptions.Run(opt),
                    (VersionOptions opt) => VersionOptions.Run(opt),
                    _ => 1);
        });
    }
}