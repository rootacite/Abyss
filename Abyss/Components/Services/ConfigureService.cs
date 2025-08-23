namespace Abyss.Components.Services;

public class ConfigureService
{
    public string MediaRoot { get; set; } = Environment.GetEnvironmentVariable("MEDIA_ROOT") ?? "/opt";
    public string Version { get; } = "Alpha v0.1";
    public string UserDatabase { get; set; } = "user.db";
    public string RaDatabase { get; set; } = "ra.db";
}