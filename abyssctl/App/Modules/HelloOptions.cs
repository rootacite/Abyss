using abyssctl.App.Interfaces;
using abyssctl.Model;
using CommandLine;

namespace abyssctl.App.Modules;

[Verb("hello", HelpText = "Say hello to abyss server")]
public class HelloOptions: IOptions
{
    public async Task<int> Run()
    {
        var r = await App.CtlWriteRead(new Ctl
        {
            Head = 100,
            Params = []
        });
        
        Console.WriteLine($"Response Code: {r.Head}");
        Console.WriteLine($"Params: {string.Join(",", r.Params)}");
        return 0;
    }
}