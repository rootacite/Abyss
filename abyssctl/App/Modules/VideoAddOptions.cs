using abyssctl.App.Attributes;
using abyssctl.App.Interfaces;
using CommandLine;

namespace abyssctl.App.Modules;

[Module(108)]
[Verb("videoadd", HelpText = "Add video to server")]
public class VideoAddOptions: IOptions
{
    [Value(0, MetaName = "path", Required = true, HelpText = "Absolute path to resources.")]
    public string Path { get; set; } = "";
    
    public async Task<int> Run()
    {
        throw new NotImplementedException();
    }
}