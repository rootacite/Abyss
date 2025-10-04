using CommandLine;

namespace abyssctl.App.Modules;

[Verb("ver", HelpText = "Get server version")]
public class VersionOptions
{
    public static int Run(VersionOptions opts)
    {
        Console.WriteLine("Version");
        return 0;
    }
}