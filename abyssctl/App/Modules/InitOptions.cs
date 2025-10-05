using abyssctl.App.Attributes;
using abyssctl.App.Interfaces;
using CommandLine;

namespace abyssctl.App.Modules;


[Module(103)]
[Verb("init", HelpText = "Initialize abyss server")]
public class InitOptions: IOptions
{
    public async Task<int> Run()
    {
        var r = await App.CtlWriteRead<InitOptions>([]);
        Console.WriteLine($"Response Code: {r.Head}");
        Console.WriteLine($"Params: {string.Join(",", r.Params)}");
        
        return 0;
    }
}