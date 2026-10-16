namespace Abyss.Components.Services.Misc;

public class ConfigureService
{
    public const string DefaultMediaRoot = "/storage";
    public const string DefaultDebugMode = "Production";
    public const string DefaultAllowedPorts = "443";
    public const string DefaultUrls = "http://0.0.0.0:80;http://[::]:80";

    public string MediaRoot { get; set; } = DefaultMediaRoot;
    public string DebugMode { get; set; } = DefaultDebugMode;
    public string AllowedPorts { get; set; } = DefaultAllowedPorts;
    public string Urls { get; set; } = DefaultUrls;
    public string Discover { get; set; } = "";
    public string PublicUrl { get; set; } = "";
    public string Version { get; } = "Alpha v0.1";
    public string UserDatabase { get; set; } = "user.db";
    public string RaDatabase { get; set; } = "ra.db";
    public string IndexDatabase { get; set; } = "index.db";

    public ConfigureService() : this(DefaultPath(), createIfMissing: true)
    {
    }

    public ConfigureService(string path, bool createIfMissing)
    {
        Load(path, createIfMissing);
    }

    public static string DefaultPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            throw new InvalidOperationException("User profile directory is not set");
        return Path.Combine(home, ".abyss", "abyss.conf");
    }

    public static string DefaultBody { get; } =
        "media_root = /storage\n" +
        "debug_mode = Production\n" +
        "allowed_ports = 443\n" +
        "urls = http://0.0.0.0:80;http://[::]:80\n" +
        "discover =\n" +
        "public_url =\n";

    /// <summary>
    /// Blank <paramref name="urls"/> uses <see cref="DefaultUrls"/>. A wildcard of one
    /// family is paired with the other family on the same scheme and port.
    /// </summary>
    public static string ExpandListenUrls(string? urls)
    {
        var source = string.IsNullOrWhiteSpace(urls) ? DefaultUrls : urls.Trim();
        var parts = source.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var part in parts)
        {
            if (seen.Add(part))
                result.Add(part);
        }

        foreach (var part in parts)
        {
            if (!Uri.TryCreate(part, UriKind.Absolute, out var uri))
                continue;
            string? twinHost = uri.IdnHost switch
            {
                "0.0.0.0" => "[::]",
                "::" => "0.0.0.0",
                _ => null
            };
            if (twinHost == null)
                continue;
            var twin = $"{uri.Scheme}://{twinHost}:{uri.Port}";
            if (seen.Add(twin))
                result.Add(twin);
        }

        return string.Join(';', result);
    }

    private void Load(string path, bool createIfMissing)
    {
        if (!File.Exists(path))
        {
            if (!createIfMissing)
                return;
            try
            {
                WriteDefault(path);
            }
            catch (IOException) when (File.Exists(path))
            {
            }
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            switch (key)
            {
                case "media_root":
                    MediaRoot = value;
                    break;
                case "debug_mode":
                    DebugMode = value;
                    break;
                case "allowed_ports":
                    AllowedPorts = value;
                    break;
                case "urls":
                    Urls = value;
                    break;
                case "discover":
                    Discover = value;
                    break;
                case "public_url":
                    PublicUrl = value;
                    break;
            }
        }
    }

    private static void WriteDefault(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    dir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(DefaultBody);
        }
    }
}
