using abyssctl.App.Attributes;
using abyssctl.App.Interfaces;
using CommandLine;

namespace abyssctl.App.Modules;


[Module(104)]
[Verb("useradd", HelpText = "Add user")]
public class UserAddOptions: IOptions
{
    [Value(0, MetaName = "username", Required = true, HelpText = "Username for new user.")]
    public string Username { get; set; } = "";
    
    [Value(1, MetaName = "privilege", Required = true, HelpText = "User privilege.")]
    public int Privilege { get; set; }
    
    public async Task<int> Run()
    {
        var r = await App.CtlWriteRead<UserAddOptions>([Username, Privilege.ToString()]);
        
        Console.WriteLine($"Response Code: {r.Head}");
        Console.WriteLine($"Params: {string.Join(",", r.Params)}");
        
        return 0;
    }
}