
// UserService.cs

using System.Security.Cryptography;
using System.Text;
using Abyss.Model;
using Microsoft.Extensions.Caching.Memory;
using NSec.Cryptography;
using SQLite;

namespace Abyss.Components.Services;

public class UserService
{
    private readonly ILogger<UserService> _logger;
    private readonly ConfigureService _config;
    private readonly IMemoryCache _cache;
    private readonly SQLiteAsyncConnection _database;
    
    public UserService(ILogger<UserService> logger, ConfigureService config, IMemoryCache cache)
    {
        _logger = logger;
        _config = config;
        _cache = cache;
        
        _database = new SQLiteAsyncConnection(config.UserDatabase, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create);
        _database.CreateTableAsync<User>().Wait();
        var rootUser = _database.Table<User>().Where(x => x.Name == "root").FirstOrDefaultAsync().Result;
        
        if (rootUser == null)
        {
            var key = GenerateKeyPair();
            string privateKeyBase64 = Convert.ToBase64String(key.Export(KeyBlobFormat.RawPrivateKey));
            string publicKeyBase64 = Convert.ToBase64String(key.Export(KeyBlobFormat.RawPublicKey));

            var s = GenerateRandomAsciiString(8);
            Console.WriteLine($"Enter the following string to create a root user: '{s}'");

            if (Console.ReadLine() != s)
            {
                throw (new Exception("Invalid Input"));
            }
            
            Console.WriteLine($"Created root user. Please keep the key safe.");
            Console.WriteLine("key: '" + privateKeyBase64 + "'");
            _database.InsertAsync(new User()
            {
                Name = "root",
                Parent = "root",
                PublicKey = publicKeyBase64,
                Privilege = 1145141919,
            }).Wait();
            
            Console.ReadKey();
        }
    }
    public async Task<string?> Challenge(string user)
    {
        var u = await _database.Table<User>().Where(x => x.Name == user).FirstOrDefaultAsync();
        
        if (u == null) // Error: User not exists
            return null;
        if (_cache.TryGetValue(u.Name, out var challenge)) // The previous challenge has not yet expired
            _cache.Remove(u.Name);

        var c = Convert.ToBase64String(Encoding.UTF8.GetBytes(GenerateRandomAsciiString(32)));
        _cache.Set(u.Name,c, DateTimeOffset.Now.AddMinutes(1));
        return c;
    }

    // The challenge source and response source are not necessarily required to be the same,
    // but the source that obtains the token must be the same as the source that uses the token in the future
    public async Task<string?> Verify(string user, string response, string ip)
    {
        var u = await _database.Table<User>().Where(x => x.Name == user).FirstOrDefaultAsync();
        if (u == null) // Error: User not exists
        {
            return null;
        }
        if (_cache.TryGetValue(u.Name, out string? challenge))
        {
            bool isVerified = VerifySignature(
                PublicKey.Import(
                    SignatureAlgorithm.Ed25519, 
                    Convert.FromBase64String(u.PublicKey),
                    KeyBlobFormat.RawPublicKey),
                Convert.FromBase64String(challenge ?? ""), 
                Convert.FromBase64String(response));

            if (!isVerified)
            {
                // Verification failed, set the challenge string to random to prevent duplicate verification
                _cache.Set(u.Name, $"failed : {GenerateRandomAsciiString(32)}", DateTimeOffset.Now.AddMinutes(1));
                return null;
            }
            else
            {
                // Remove the challenge string and create a session
                _cache.Remove(u.Name);
                var s = GenerateRandomAsciiString(64);
                _cache.Set(s, $"{u.Name}@{ip}", DateTimeOffset.Now.AddDays(1));
                _logger.LogInformation($"Verified {u.Name}@{ip}");
                return s;
            }
        }

        return null;
    }

    public string? Validate(string token, string ip)
    {
        if (_cache.TryGetValue(token, out string? userAndIp))
        {
            if (ip != userAndIp?.Split('@')[1])
            {
                _logger.LogError($"Token used from another Host: {token}");
                Destroy(token);
                return null;
            }
            _logger.LogInformation($"Validated {userAndIp}");
            return userAndIp?.Split('@')[0];
        }
        _logger.LogWarning($"Validation failed {token}");
        return null;
    }
    
    public void Destroy(string token)
    {
        _cache.Remove(token);
    }

    public async Task<User?> QueryUser(string user)
    {
        var u = await _database.Table<User>().Where(x => x.Name == user).FirstOrDefaultAsync();
        return u;
    }

    public async Task CreateUser(User user)
    {
        await _database.InsertAsync(user);
        _logger.LogInformation($"Created user: {user.Name}, Parent: {user.Parent}, Privilege: {user.Privilege}");
    }
    
    static Key GenerateKeyPair()
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        var creationParameters = new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        };
        return Key.Create(algorithm, creationParameters);
    }
    
    public static string GenerateRandomAsciiString(int length)
    {
        const string asciiChars = "-0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        
        using (var rng = RandomNumberGenerator.Create())
        {
            byte[] randomBytes = new byte[length];
            rng.GetBytes(randomBytes);
            
            char[] result = new char[length];
            for (int i = 0; i < length; i++)
            {
                result[i] = asciiChars[randomBytes[i] % asciiChars.Length];
            }
            return new string(result);
        }
    }
    
    static bool VerifySignature(PublicKey publicKey, byte[] data, byte[] signature)
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        return algorithm.Verify(publicKey, data, signature);
    }
}