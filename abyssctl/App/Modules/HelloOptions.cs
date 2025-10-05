using abyssctl.App.Attributes;
using abyssctl.App.Interfaces;
using abyssctl.Model;
using CommandLine;

namespace abyssctl.App.Modules;

[Module(100)]
[Verb("hello", HelpText = "Say hello to abyss server")]
public class HelloOptions: IOptions
{
    [Option('r', "raw", Default = false, HelpText = "Show raw response.")]
    public bool Raw { get; set; }
    
    public async Task<int> Run()
    {
        var r = await App.CtlWriteRead<HelloOptions>([]);

        if (Raw)
        {
            Console.WriteLine($"Response Code: {r.Head}");
            Console.WriteLine($"Params: {string.Join(",", r.Params)}");
        }
        else
        {
            Console.WriteLine($"Server: {string.Join(",", r.Params)}");
        }
        return 0;
    }
}