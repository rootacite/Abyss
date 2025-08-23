// Program.cs
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.Json.Serialization.Metadata;
using NSec.Cryptography;

public class ChallengeRequestBody
{
    public string Response { get; set; } = "";
}

public class CreateRequestBody
{
    public string Response { get; set; } = "";
    public string Name { get; set; } = "";
    public string Parent { get; set; } = "";
    public int Privilege { get; set; }
    public string PublicKey { get; set; } = "";
}

public static class Ed25519Utils
{
    public static (string privateBase64, string publicBase64) GenerateKeyPairBase64()
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        var creationParameters = new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        };
        using var key = Key.Create(algorithm, creationParameters);
        var priv = key.Export(KeyBlobFormat.RawPrivateKey);
        var pub = key.Export(KeyBlobFormat.RawPublicKey);
        return (Convert.ToBase64String(priv), Convert.ToBase64String(pub));
    }

    public static string SignBase64PrivateKey(string privateKeyBase64, byte[] dataToSign)
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        var privateBytes = Convert.FromBase64String(privateKeyBase64);
        using var key = Key.Import(algorithm, privateBytes, KeyBlobFormat.RawPrivateKey);
        var sig = algorithm.Sign(key, dataToSign);
        return Convert.ToBase64String(sig);
    }
}

public static class Program
{
    static async Task<int> Main(string[] args)
    {
        if (args == null || args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var cmd = args[0].ToLowerInvariant();
        try
        {
            switch (cmd)
            {
                case "open":
                    return await CmdOpen(args);
                case "destroy":
                    return await CmdDestroy(args);
                case "valid":
                    return await CmdValid(args);
                case "create":
                    return await CmdCreate(args);
                default:
                    Console.Error.WriteLine("Unknown command.");
                    PrintUsage();
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 3;
        }
    }

    static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  AbyssCli open <baseUrl> <user> <privateKeyBase64>");
        Console.WriteLine("  AbyssCli destroy <baseUrl> <token>");
        Console.WriteLine("  AbyssCli valid <baseUrl> <token>");
        Console.WriteLine("  AbyssCli create <baseUrl> <user> <privateKeyBase64> <newUsername> <privilege>");
    }

    static HttpClient CreateHttpClient(string baseUrl)
    {
        var client = new HttpClient();
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        return client;
    }

    static async Task<int> CmdOpen(string[] args)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine("open requires 3 arguments: <baseUrl> <user> <privateKeyBase64>");
            return 1;
        }

        var baseUrl = args[1];
        var user = args[2];
        var privateKeyBase64 = args[3];

        using var client = CreateHttpClient(baseUrl);

        // 1. GET challenge
        var challenge = await GetChallenge(client, user);
        if (challenge == null)
        {
            Console.Error.WriteLine("Failed to get challenge.");
            return 1;
        }

        // 2. Sign challenge (challenge is base64 string)
        byte[] challengeBytes;
        try
        {
            challengeBytes = Convert.FromBase64String(challenge);
        }
        catch
        {
            Console.Error.WriteLine("Challenge is not valid base64.");
            return 1;
        }

        string signatureBase64;
        try
        {
            signatureBase64 = Ed25519Utils.SignBase64PrivateKey(privateKeyBase64, challengeBytes);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Signing failed: {ex.Message}");
            return 1;
        }

        // 3. POST response to get token
        var token = await PostResponseForToken(client, user, signatureBase64);
        if (token == null)
        {
            Console.Error.WriteLine("Authentication failed or server returned no token.");
            return 1;
        }

        Console.WriteLine(token);
        return 0;
    }

    static async Task<int> CmdDestroy(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("destroy requires 2 arguments: <baseUrl> <token>");
            return 1;
        }

        var baseUrl = args[1];
        var token = args[2];

        using var client = CreateHttpClient(baseUrl);

        var resp = await client.PostAsync($"api/user/destroy?token={token}", null);
        if (!resp.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"Destroy failed: {resp.StatusCode}");
            var txt = await TryReadResponseText(resp);
            if (!string.IsNullOrEmpty(txt)) Console.Error.WriteLine(txt);
            return 1;
        }

        var body = await resp.Content.ReadAsStringAsync();
        Console.WriteLine("Success");
        return 0;
    }

    static async Task<int> CmdValid(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("valid requires 2 arguments: <baseUrl> <token>");
            return 1;
        }

        var baseUrl = args[1];
        var token = args[2];

        using var client = CreateHttpClient(baseUrl);

        var resp = await client.PostAsync($"api/user/validate?token={token}", null);
        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine("Invalid");
            return 1;
        }

        var content = await resp.Content.ReadAsStringAsync();
        // server likely returns JSON string (e.g. "username"), try to parse JSON string
        try
        {
            var username = JsonSerializer.Deserialize<string>(content, jsonOptions);
            if (username == null)
            {
                Console.WriteLine("Invalid");
                return 1;
            }
            Console.WriteLine(username);
            return 0;
        }
        catch
        {
            // fallback
            Console.WriteLine(content.Trim('"'));
            return 0;
        }
    }

    static async Task<int> CmdCreate(string[] args)
    {
        if (args.Length != 6)
        {
            Console.Error.WriteLine("create requires 5 arguments: <baseUrl> <user> <privateKeyBase64> <newUsername> <privilege>");
            return 1;
        }

        var baseUrl = args[1];
        var user = args[2];
        var privateKeyBase64 = args[3];
        var newUsername = args[4];
        if (!int.TryParse(args[5], out var privilege))
        {
            Console.Error.WriteLine("Privilege must be an integer.");
            return 1;
        }

        using var client = CreateHttpClient(baseUrl);

        // 1. Get challenge for creator user
        var challenge = await GetChallenge(client, user);
        if (challenge == null)
        {
            Console.Error.WriteLine("Failed to get challenge for creator.");
            return 1;
        }

        byte[] challengeBytes;
        try
        {
            challengeBytes = Convert.FromBase64String(challenge);
        }
        catch
        {
            Console.Error.WriteLine("Challenge is not valid base64.");
            return 1;
        }

        string signatureBase64;
        try
        {
            signatureBase64 = Ed25519Utils.SignBase64PrivateKey(privateKeyBase64, challengeBytes);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Signing failed: {ex.Message}");
            return 1;
        }

        // 2. Generate key pair for new user
        var (newPrivBase64, newPubBase64) = Ed25519Utils.GenerateKeyPairBase64();

        // 3. Build create payload
        var payload = new CreateRequestBody
        {
            Response = signatureBase64,
            Name = newUsername,
            Parent = user,
            Privilege = privilege,
            PublicKey = newPubBase64
        };

        var json = JsonSerializer.Serialize(payload, jsonOptions);
        var request = new HttpRequestMessage(new HttpMethod("PATCH"), $"api/user/{Uri.EscapeDataString(user)}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var resp = await client.SendAsync(request);
        var respText = await TryReadResponseText(resp);
        if (!resp.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"Create failed: {resp.StatusCode}");
            if (!string.IsNullOrEmpty(respText)) Console.Error.WriteLine(respText);
            return 1;
        }

        Console.WriteLine("Success");
        Console.WriteLine("NewUserPrivateKeyBase64:");
        Console.WriteLine(newPrivBase64);
        Console.WriteLine("NewUserPublicKeyBase64:");
        Console.WriteLine(newPubBase64);
        return 0;
    }

    static async Task<string?> GetChallenge(HttpClient client, string user)
    {
        var resp = await client.GetAsync($"api/user/{Uri.EscapeDataString(user)}");
        if (!resp.IsSuccessStatusCode) return null;
        var content = await resp.Content.ReadAsStringAsync();

        // server probably returns JSON string; try to deserialize to string
        try
        {
            var s = JsonSerializer.Deserialize<string>(content, jsonOptions);
            if (s != null) return s;
        }
        catch { /* ignore */ }

        // fallback: trim quotes
        return content.Trim('"');
    }

    static async Task<string?> PostResponseForToken(HttpClient client, string user, string signatureBase64)
    {
        var body = new ChallengeRequestBody { Response = signatureBase64 };
        var json = JsonSerializer.Serialize(body, jsonOptions);
        var resp = await client.PostAsync($"api/user/{Uri.EscapeDataString(user)}",
            new StringContent(json, Encoding.UTF8, "application/json"));
        if (!resp.IsSuccessStatusCode) return null;
        var content = await resp.Content.ReadAsStringAsync();
        try
        {
            var token = JsonSerializer.Deserialize<string>(content, jsonOptions);
            if (!string.IsNullOrEmpty(token)) return token;
        }
        catch { /* ignore */ }
        return content.Trim('"');
    }

    static async Task<string> TryReadResponseText(HttpResponseMessage resp)
    {
        try
        {
            return await resp.Content.ReadAsStringAsync();
        }
        catch
        {
            return "";
        }
    }
    
    static readonly JsonSerializerOptions jsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
}
