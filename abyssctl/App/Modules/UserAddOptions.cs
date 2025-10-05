using abyssctl.App.Attributes;
using abyssctl.App.Interfaces;
using CommandLine;

namespace abyssctl.App.Modules;


[Module(104)]
[Verb("useradd", HelpText = "Add user")]
public class UserAddOptions: IOptions
{
    [Option('u', "username", Required = true, HelpText = "Username for new user.")]
    public string Username { get; set; } = "";
    
    [Option('p', "privilege", Required = true, HelpText = "User privilege.")]
    public int Privilege { get; set; }
    public async Task<int> Run()
    {
        var r = await App.CtlWriteRead<UserAddOptions>([Username, Privilege.ToString()]);
        
        Console.WriteLine($"Response Code: {r.Head}");
        Console.WriteLine($"Params: {string.Join(",", r.Params)}");
        
        return 0;
    }
}