using abyssctl.Model;
using CommandLine;

namespace abyssctl.App.Modules;

[Verb("hello", HelpText = "Say hello to abyss server")]
public class HelloOptions
{
    public static int Run(HelloOptions opts)
    {
        var r = App.CtlWriteRead(new Ctl
        {
            Head = 100,
            Params = []
        }).GetAwaiter().GetResult();
        Console.WriteLine($"Response Code: {r.Head}");
        Console.WriteLine($"Params: {string.Join(",", r.Params)}");
        return 0;
    }
}