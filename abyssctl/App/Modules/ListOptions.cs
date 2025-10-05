using abyssctl.App.Attributes;
using abyssctl.App.Interfaces;
using CommandLine;

namespace abyssctl.App.Modules;

[Module(107)]
[Verb("list", HelpText = "List items")]
public class ListOptions: IOptions
{
    [Value(0, MetaName = "path", Required = true, HelpText = "Relative path to resources.")]
    public string Path { get; set; } = "";
    
    public async Task<int> Run()
    {
        var r = await App.CtlWriteRead<ListOptions>([Path]);

        if (r.Head != 200)
        {
            Console.WriteLine($"Response Code: {r.Head}");
            Console.WriteLine($"Params: {string.Join(",", r.Params)}");
        }
        else
        {
            Console.WriteLine(r.Params[0]);
        }

        return 0;
    }
}