using abyssctl.App.Attributes;
using abyssctl.App.Interfaces;
using CommandLine;

namespace abyssctl.App.Modules;

[Module(101)]
[Verb("ver", HelpText = "Get server version")]
public class VersionOptions: IOptions
{
    public async Task<int> Run()
    {
        Console.WriteLine("Version");
        return 0;
    }
}