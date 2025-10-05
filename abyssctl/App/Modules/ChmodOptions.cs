using abyssctl.App.Attributes;
using abyssctl.App.Interfaces;
using CommandLine;

namespace abyssctl.App.Modules;

[Module(106)]
[Verb("chmod", HelpText = "Change resources permissions")]
public class ChmodOptions: IOptions
{
    [Value(0, MetaName = "path", Required = true, HelpText = "Relative path to resources.")]
    public string Path { get; set; } = "";

    [Value(1, MetaName = "permission", Required = true, HelpText = "Permission mask.")]
    public string Permission { get; set; } = "";
    
    [Option('r', "recursive", Default = false, HelpText = "Recursive change resources.")]
    public bool Recursive { get; set; }
    public async Task<int> Run()
    {
        var r = await App.CtlWriteRead<ChmodOptions>([Path, Permission, Recursive.ToString()]);
        
        Console.WriteLine($"Response Code: {r.Head}");
        Console.WriteLine($"Params: {string.Join(",", r.Params)}");
        
        return 0;
    }
}