
// UserService.cs

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Abyss.Components.Services.Misc;
using Abyss.Model.Security;
using Microsoft.Extensions.Caching.Memory;
using NSec.Cryptography;
using SQLite;
using Task = System.Threading.Tasks.Task;

namespace Abyss.Components.Services.Security;

public class UserService
{
    private readonly ILogger<UserService> _logger;
    private readonly IMemoryCache _cache;
    private readonly SQLiteAsyncConnection _database;
    public UserService(ILogger<UserService> logger, ConfigureService config, IMemoryCache cache)
    {
        _logger = logger;
        _cache = cache;
        
        _database = new SQLiteAsyncConnection(config.UserDatabase, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create);
        _database.CreateTableAsync<User>().Wait();
        var rootUser = _database.Table<User>().Where(x => x.Uuid == 1).FirstOrDefaultAsync().Result;
        
        if (config.DebugMode == "Debug")
            _cache.Set("abyss", $"1@127.0.0.1", DateTimeOffset.Now.AddHours(1));
            // Test token, can only be used locally. Will be destroyed in one hour.
        
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
                Uuid = 1,
                Username = "root",
                ParentId = 1,
                PublicKey = publicKeyBase64,
                Privilege = 1145141919,
            }).Wait();
            
            Console.ReadKey();
        }
    }

    public async Task<string?> OpenUserAsync(string user, string token, string? bindIp, string ip)
    {
        var caller = Validate(token, ip);
        if (caller != 1)
        {
            return null;
        }

        var target = await QueryUser(user);
        if (target == null)
        {
            return null;
        }

        var ipToBind = string.IsNullOrWhiteSpace(bindIp) ? ip : bindIp;

        var t = CreateToken(target.Uuid, ipToBind, TimeSpan.FromHours(1));

        _logger.LogInformation("Root created 1h token for {User}, bound to {BindIp}, request from {ReqIp}", user,
            ipToBind, ip);
        return t;
    }
    
    public async Task<bool> CreateUserAsync(string user, UserCreating creating, string ip)
    {
        // Valid token
        var r = await Verify(user, creating.Response, ip);
        if (r == null)
            return false;

        // User exists ?
        var cu = await QueryUser(creating.Name);
        if (cu != null)
            return false;

        // Valid username string
        if (!IsAlphanumeric(creating.Name))
            return false;

        // Valid parent && Privilege
        var ou = await QueryUser(Validate(r, ip));
        if (creating.Privilege > ou?.Privilege || ou == null)
            return false;

        await CreateUser(new User
        {
            Username = creating.Name,
            ParentId = ou.Uuid,
            Privilege = creating.Privilege,
            PublicKey = creating.PublicKey,
        });

        Destroy(r);
        return true;
    }
    public async Task<string?> Challenge(string user)
    {
        var u = await _database.Table<User>().Where(x => x.Username == user).FirstOrDefaultAsync();
        
        if (u == null) // Error: User not exists
            return null;
        
        if (_cache.TryGetValue(u.Uuid, out _)) // The previous challenge has not yet expired
            _cache.Remove(u.Uuid);

        var c = Convert.ToBase64String(Encoding.UTF8.GetBytes(GenerateRandomAsciiString(32)));
        _cache.Set(u.Uuid, c, DateTimeOffset.Now.AddMinutes(1));
        return c;
    }

    // The challenge source and response source are not necessarily required to be the same,
    // but the source that obtains the token must be the same as the source that uses the token in the future
    public async Task<string?> Verify(string user, string response, string ip)
    {
        var u = await _database.Table<User>().Where(x => x.Username == user).FirstOrDefaultAsync();
        if (u == null) // Error: User not exists
        {
            return null;
        }
        
        if (_cache.TryGetValue(u.Uuid, out string? challenge))
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
                _cache.Set(u.Uuid, $"failed : {GenerateRandomAsciiString(32)}", DateTimeOffset.Now.AddMinutes(1));
                return null;
            }
            else
            {
                // Remove the challenge string and create a session
                _cache.Remove(u.Uuid);
                var s = GenerateRandomAsciiString(64);
                _cache.Set(s, $"{u.Uuid}@{ip}", DateTimeOffset.Now.AddDays(1));
                _logger.LogInformation($"Verified {u.Uuid}@{ip}, Name: {u.Username}");
                return s;
            }
        }

        return null;
    }

    // Id >= 1 : Success, Uid
    // Id == -1: Failed
    public int Validate(string token, string ip)
    {
        if (_cache.TryGetValue(token, out string? userAndIp))
        {
            if (ip != userAndIp?.Split('@')[1] && ip != "127.0.0.1" && token != "abyss")
            {
                _logger.LogError($"Token used from another Host: {token}");
                Destroy(token);
                return -1;
            }
            // _logger.LogInformation($"Validated {userAndIp}");
            return Convert.ToInt32(userAndIp?.Split('@')[0]);
        }
        _logger.LogWarning($"Validation failed {token}");
        return -1;
    }
    
    public void Destroy(string token)
    {
        _cache.Remove(token);
    }

    public async Task<User?> QueryUser(int uid)
    {
        if (uid == -1) 
            return null;
        var u = await _database.Table<User>().Where(x => x.Uuid == uid).FirstOrDefaultAsync();
        return u;
    }
    
    public async Task<User?> QueryUser(string username)
    {
        var u = await _database.Table<User>().Where(x => x.Username == username).FirstOrDefaultAsync();
        return u;
    }

    public async Task CreateUser(User user)
    {
        await _database.InsertAsync(user);
        _logger.LogInformation($"Created user: {user.Username}, Uid: {user.Uuid}, Parent: {user.ParentId}, Privilege: {user.Privilege}");
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
    
    public static bool VerifySignature(PublicKey publicKey, byte[] data, byte[] signature)
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        return algorithm.Verify(publicKey, data, signature);
    }

    public async Task<bool> VerifyAny(byte[] data, byte[] signature)
    {
        var users = await _database.Table<User>().ToListAsync();
        foreach (var u in users)
        {
            try
            {
                var pubKeyBytes = Convert.FromBase64String(u.PublicKey);
                var pubKey = PublicKey.Import(
                    SignatureAlgorithm.Ed25519,
                    pubKeyBytes,
                    KeyBlobFormat.RawPublicKey);

                if (VerifySignature(pubKey, data, signature))
                {
                    _logger.LogInformation($"Signature verified using user {u.Username}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to import public key for {u.Username}");
            }
        }
        return false;
    }
    
    public string CreateToken(int uid, string ip, TimeSpan lifetime)
    {
        var token = GenerateRandomAsciiString(64);
        _cache.Set(token, $"{uid}@{ip}", DateTimeOffset.Now.Add(lifetime));
        _logger.LogInformation($"Created token for {uid}@{ip}, valid {lifetime.TotalMinutes} minutes");
        return token;
    }
    
    
    public static bool IsAlphanumeric(string input)
    {
        if (string.IsNullOrEmpty(input))
            return false;
        return Regex.IsMatch(input, @"^[a-zA-Z0-9]+$");
    }
}