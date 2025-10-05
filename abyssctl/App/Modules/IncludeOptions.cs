using abyssctl.App.Attributes;
using abyssctl.App.Interfaces;
using CommandLine;

namespace abyssctl.App.Modules;

[Module(105)]
[Verb("include", HelpText = "include resources to system")]
public class IncludeOptions: IOptions
{
    [Value(0, MetaName = "path", Required = true, HelpText = "Relative path to resources.")]
    public string Path { get; set; } = "";
    
    [Value(1, MetaName = "owner", Required = true, HelpText = "Owner id.")]
    public int Id { get; set; }
    
    [Option('r', "recursive", Default = false, HelpText = "Recursive include resources.")]
    public bool Recursive { get; set; }
    
    public async Task<int> Run()
    {
        var r = await App.CtlWriteRead<IncludeOptions>([Path, Id.ToString(), Recursive.ToString()]);
        
        Console.WriteLine($"Response Code: {r.Head}");
        Console.WriteLine($"Params: {string.Join(",", r.Params)}");
        
        return 0;
    }
}