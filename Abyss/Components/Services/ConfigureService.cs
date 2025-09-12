namespace Abyss.Components.Services;

public class ConfigureService
{
    public string MediaRoot { get; set; } = Environment.GetEnvironmentVariable("MEDIA_ROOT") ?? "/opt";
    public string DebugMode { get; set; } = Environment.GetEnvironmentVariable("DEBUG_MODE") ?? "Production";
    public string AllowedPorts { get; set; } = Environment.GetEnvironmentVariable("ALLOWED_PORTS") ?? "443"; // Split with ' '
    public string Version { get; } = "Alpha v0.1";
    public string UserDatabase { get; set; } = "user.db";
    public string RaDatabase { get; set; } = "ra.db";
}