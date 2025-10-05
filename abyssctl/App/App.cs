
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using abyssctl.App.Attributes;
using abyssctl.App.Interfaces;
using abyssctl.Model;
using abyssctl.Static;
using CommandLine;
using Newtonsoft.Json;

namespace abyssctl.App;

public class App
{
    private static readonly string SocketPath = Path.Combine(Path.GetTempPath(), "abyss-ctl.sock");
    
    public static async Task<Ctl> CtlWriteRead<T>(string[] param)
    {
        var attr = typeof(T).GetCustomAttribute<ModuleAttribute>()!;
        
        var endPoint = new UnixDomainSocketEndPoint(SocketPath);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(endPoint);
            await socket.WriteBase64Async(Ctl.MakeBase64(attr.Head, param));
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
            return Parser.Default.ParseArguments(args, ModuleAttribute.Modules)
                .MapResult(
                    (object obj) =>
                    {
                        var s = (obj as IOptions)?.Run().GetAwaiter().GetResult();
                        return s!.Value;
                    },
                    _ => 1);
        });
    }
}