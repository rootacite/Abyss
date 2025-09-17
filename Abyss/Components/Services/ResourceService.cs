// ResourceService.cs

using System.Text;
using System.Text.RegularExpressions;
using Abyss.Components.Static;
using Abyss.Model;
using SQLite;
using System.IO.Hashing;

namespace Abyss.Components.Services;

public enum OperationType
{
    Read, // Query, Read
    Write, // Write, Delete
    Security // Chown, Chmod
}

public class ResourceService
{
    private readonly ILogger<ResourceService> _logger;
    private readonly ConfigureService _config;
    private readonly UserService _user;
    private readonly SQLiteAsyncConnection _database;

    private static readonly Regex PermissionRegex = new("^([r-][w-]),([r-][w-]),([r-][w-])$", RegexOptions.Compiled);

    public ResourceService(ILogger<ResourceService> logger, ConfigureService config, UserService user)
    {
        _logger = logger;
        _config = config;
        _user = user;

        _database = new SQLiteAsyncConnection(config.RaDatabase, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create);
        _database.CreateTableAsync<ResourceAttribute>().Wait();

        var tasksPath = Helpers.SafePathCombine(_config.MediaRoot, "Tasks");
        if (tasksPath != null)
        {
            InsertRaRow(tasksPath, 1, "rw,r-,r-", true).Wait();
        }

        var livePath = Helpers.SafePathCombine(_config.MediaRoot, "Live");
        if (livePath != null)
        {
            InsertRaRow(livePath, 1, "rw,r-,r-", true).Wait();
        }
    }

    // Create UID only for resources, without considering advanced hash security such as adding salt
    private static string Uid(string path)
    {
        var b = Encoding.UTF8.GetBytes(path);
        var r = XxHash128.Hash(b, 0x11451419);
        return Convert.ToBase64String(r);
    }

    private async Task<bool> ValidAll(string[] paths, string token, OperationType type, string ip)
    {
        if (paths.Length == 0)
        {
            _logger.LogError("ValidAll called with empty path set");
            return false;
        }

        var mediaRootFull = Path.GetFullPath(_config.MediaRoot);

        // 1. basic path checks & normalize to relative
        var relPaths = new List<string>(paths.Length);
        foreach (var p in paths)
        {
            if (!p.StartsWith(mediaRootFull, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError($"Path outside media root or null: {p}");
                return false;
            }

            relPaths.Add(Path.GetRelativePath(_config.MediaRoot, Path.GetFullPath(p)));
        }

        // 2. validate token and user once
        int uuid = _user.Validate(token, ip);
        if (uuid == -1)
        {
            _logger.LogError($"Invalid token: {token}");
            return false;
        }

        User? user = await _user.QueryUser(uuid);
        if (user == null || user.Uuid != uuid)
        {
            _logger.LogError($"Verification failed: {token}");
            return false; 
        }

        // 3. build uid -> required ops map (avoid duplicate Uid calculations)
        var uidToOps = new Dictionary<string, HashSet<OperationType>>(StringComparer.OrdinalIgnoreCase);
        var uidToExampleRelPath =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // for better logging
        foreach (var rel in relPaths)
        {
            var parts = rel
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();

            // parents (each prefix) require Read
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var subPath = Path.Combine(parts.Take(i + 1).ToArray());
                var uidDir = Uid(subPath);
                if (!uidToOps.TryGetValue(uidDir, out var ops))
                {
                    ops = new HashSet<OperationType>();
                    uidToOps[uidDir] = ops;
                    uidToExampleRelPath[uidDir] = subPath;
                }

                ops.Add(OperationType.Read);
            }

            // resource itself requires requested 'type'
            var resourcePath = (parts.Length == 0) ? string.Empty : Path.Combine(parts);
            var uidRes = Uid(resourcePath);
            if (!uidToOps.TryGetValue(uidRes, out var resOps))
            {
                resOps = new HashSet<OperationType>();
                uidToOps[uidRes] = resOps;
                uidToExampleRelPath[uidRes] = resourcePath;
            }

            resOps.Add(type);
        }

        // 4. batch query DB for all UIDs using parameterized IN (...) and chunking to respect SQLite param limits
        var uidsNeeded = uidToOps.Keys.ToList();
        var rasList = new List<ResourceAttribute>();

        const int sqliteMaxVariableNumber = 900; // keep below default 999 for safety
        if (uidsNeeded.Count > 0)
        {
            if (uidsNeeded.Count <= sqliteMaxVariableNumber)
            {
                var placeholders = string.Join(",", uidsNeeded.Select(_ => "?"));
                var queryArgs = uidsNeeded.Cast<object>().ToArray();
                var sql = $"SELECT * FROM ResourceAttributes WHERE Uid IN ({placeholders})";
                var chunkResult = await _database.QueryAsync<ResourceAttribute>(sql, queryArgs);
                rasList.AddRange(chunkResult);
            }
            else
            {
                for (int i = 0; i < uidsNeeded.Count; i += sqliteMaxVariableNumber)
                {
                    var chunk = uidsNeeded.Skip(i).Take(sqliteMaxVariableNumber).ToList();
                    var placeholders = string.Join(",", chunk.Select(_ => "?"));
                    var queryArgs = chunk.Cast<object>().ToArray();
                    var sql = $"SELECT * FROM ResourceAttributes WHERE Uid IN ({placeholders})";
                    var chunkResult = await _database.QueryAsync<ResourceAttribute>(sql, queryArgs);
                    rasList.AddRange(chunkResult);
                }
            }
        }

        var raDict = rasList.ToDictionary(r => r.Uid, StringComparer.OrdinalIgnoreCase);

        // 5. check each uid once per required operation (cache results per uid+op)
        var permCache = new Dictionary<(string uid, OperationType op), bool>(); // avoid repeated CheckPermission

        foreach (var kv in uidToOps)
        {
            var uid = kv.Key;
            if (!raDict.TryGetValue(uid, out var ra))
            {
                var examplePath = uidToExampleRelPath.GetValueOrDefault(uid, uid);
                _logger.LogError(
                    $"Permission check failed (missing resource attribute): User: {uuid}, Resource: {examplePath}, Uid: {uid}");
                return false;
            }

            foreach (var op in kv.Value)
            {
                var key = (uid, op);
                if (!permCache.TryGetValue(key, out var ok))
                {
                    ok = await CheckPermission(user, ra, op);
                    permCache[key] = ok;
                }

                if (!ok)
                {
                    var examplePath = uidToExampleRelPath.TryGetValue(uid, out var p) ? p : uid;
                    _logger.LogError(
                        $"Permission check failed: User: {uuid}, Resource: {examplePath}, Uid: {uid}, Type: {op}");
                    return false;
                }
            }
        }

        return true;
    }


    public async Task<bool> Valid(string path, string token, OperationType type, string ip)
    {
        // Path is abs path here, due to Helpers.SafePathCombine
        if (!path.StartsWith(Path.GetFullPath(_config.MediaRoot), StringComparison.OrdinalIgnoreCase))
            return false;

        path = Path.GetRelativePath(_config.MediaRoot, path);

        int uuid = _user.Validate(token, ip);
        if (uuid == -1)
        {
            // No permission granted for invalid tokens
            _logger.LogError($"Invalid token: {token}");
            return false;
        }

        User? user = await _user.QueryUser(uuid);
        if (user == null || user.Uuid != uuid)
        {
            _logger.LogError($"Verification failed: {token}");
            return false; // Two-factor authentication
        }

        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToArray();
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var subPath = Path.Combine(parts.Take(i + 1).ToArray());
            var uidDir = Uid(subPath);
            var raDir = await _database
                .Table<ResourceAttribute>()
                .Where(r => r.Uid == uidDir)
                .FirstOrDefaultAsync();
            if (raDir == null)
            {
                _logger.LogError($"Permission denied: {uuid} has no read access to parent directory {subPath}");
                return false;
            }

            if (!await CheckPermission(user, raDir, OperationType.Read))
            {
                _logger.LogError($"Permission denied: {uuid} has no read access to parent directory {subPath}");
                return false;
            }
        }

        var uid = Uid(path);
        ResourceAttribute? ra = await _database
            .Table<ResourceAttribute>()
            .Where(r => r.Uid == uid)
            .FirstOrDefaultAsync();
        if (ra == null)
        {
            _logger.LogError($"Permission check failed: User: {uuid}, Resource: {path}, Type: {type.ToString()} ");
            return false;
        }

        var l = await CheckPermission(user, ra, type);
        if (!l)
        {
            _logger.LogError($"Permission check failed: User: {uuid}, Resource: {path}, Type: {type.ToString()} ");
        }

        return l;
    }

    private async Task<bool> CheckPermission(User? user, ResourceAttribute? ra, OperationType type)
    {
        if (user == null || ra == null) return false;

        if (!PermissionRegex.IsMatch(ra.Permission)) return false;

        var perms = ra.Permission.Split(',');
        if (perms.Length != 3) return false;

        var owner = await _user.QueryUser(ra.Owner);
        if (owner == null) return false;

        bool isOwner = ra.Owner == user.Uuid;
        bool isPeer = !isOwner && user.Privilege == owner.Privilege;
        bool isOther = !isOwner && !isPeer;

        string currentPerm;
        if (isOwner) currentPerm = perms[0];
        else if (isPeer) currentPerm = perms[1];
        else if (isOther) currentPerm = perms[2];
        else return false;

        switch (type)
        {
            case OperationType.Read:
                return currentPerm.Contains('r') || (user.Privilege > owner.Privilege);
            case OperationType.Write:
                return currentPerm.Contains('w') || (user.Privilege > owner.Privilege);
            case OperationType.Security:
                return (isOwner && currentPerm.Contains('w')) || user.Uuid == 1;
            default:
                return false;
        }
    }

    public async Task<string[]?> Query(string path, string token, string ip)
    {
        if (!await Valid(path, token, OperationType.Read, ip))
            return null;

        if (Helpers.GetPathType(path) != PathType.Directory)
            return null;

        var files = Directory.EnumerateFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly);
        return files.Select(x => Path.GetRelativePath(path, x)).ToArray();
    }

    public async Task<bool> Get(string path, string token, string ip)
    {
        return await Valid(path, token, OperationType.Read, ip);
    }

    public async Task<bool> GetAll(string[] path, string token, string ip)
    {
        return await ValidAll(path, token, OperationType.Read, ip);
    }

    public async Task<bool> Update(string path, string token, string ip)
    {
        return await Valid(path, token, OperationType.Write, ip);
    }

    public async Task<bool> Initialize(string path, string token, string owner, string ip)
    {
        var u = await _user.QueryUser(owner);
        if (u == null || u.Uuid == -1) return false;
        
        return await Initialize(path, token, u.Uuid, ip);
    }

    public async Task<bool> Initialize(string path, string token, int owner, string ip)
    {
        // TODO: Use a more elegant Debug mode
        if (_config.DebugMode == "Debug")
            goto debug;
        // 1. Authorization: Verify the operation is performed by 'root'
        var requester = _user.Validate(token, ip);
        if (requester != 1)
        {
            _logger.LogWarning(
                $"Permission denied: Non-root user '{requester}' attempted to initialize resources.");
            return false;
        }

        debug:
        // 2. Validation: Ensure the target path and owner are valid
        if (!Directory.Exists(path))
        {
            _logger.LogError($"Initialization failed: Path '{path}' does not exist or is not a directory.");
            return false;
        }

        var ownerUser = await _user.QueryUser(owner);
        if (ownerUser == null)
        {
            _logger.LogError($"Initialization failed: Owner user '{owner}' does not exist.");
            return false;
        }

        try
        {
            // 3. Traversal: Get the root directory and all its descendants (files and subdirectories)
            var allPaths = Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories).Prepend(path);

            // 4. Filtering: Identify which paths are not yet in the database
            var newResources = new List<ResourceAttribute>();
            foreach (var p in allPaths)
            {
                var currentPath = Path.GetRelativePath(_config.MediaRoot, p);
                var uid = Uid(currentPath);
                var existing = await _database.Table<ResourceAttribute>().Where(r => r.Uid == uid)
                    .FirstOrDefaultAsync();

                // If it's not in the database, add it to our list for batch insertion
                if (existing == null)
                {
                    newResources.Add(new ResourceAttribute
                    {
                        Uid = uid,
                        Owner = owner,
                        Permission = "rw,--,--"
                    });
                }
            }

            // 5. Database Insertion: Add all new resources in a single, efficient transaction
            if (newResources.Any())
            {
                await _database.InsertAllAsync(newResources);
                _logger.LogInformation(
                    $"Successfully initialized {newResources.Count} new resources under '{path}' for user '{owner}'.");
            }
            else
            {
                _logger.LogInformation(
                    $"No new resources to initialize under '{path}'. All items already exist in the database.");
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"An error occurred during resource initialization for path '{path}'.");
            return false;
        }
    }

    public async Task<bool> Put(string path, string token, string ip)
    {
        throw new NotImplementedException();
    }

    public async Task<bool> Delete(string path, string token, string ip)
    {
        throw new NotImplementedException();
    }

    public async Task<bool> Exclude(string path, string token, string ip)
    {
        var requester = _user.Validate(token, ip);
        if (requester != 1)
        {
            _logger.LogWarning($"Permission denied: Non-root user '{requester}' attempted to exclude resource '{path}'.");
            return false;
        }

        try
        {
            var relPath = Path.GetRelativePath(_config.MediaRoot, path);
            var uid = Uid(relPath);

            var resource = await _database.Table<ResourceAttribute>().Where(r => r.Uid == uid).FirstOrDefaultAsync();
            if (resource == null)
            {
                _logger.LogError($"Exclude failed: Resource '{relPath}' not found in database.");
                return false;
            }

            var deleted = await _database.DeleteAsync(resource);
            if (deleted > 0)
            {
                _logger.LogInformation($"Successfully excluded resource '{relPath}' from management.");
                return true;
            }
            else
            {
                _logger.LogError($"Failed to exclude resource '{relPath}' from database.");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error excluding resource '{path}'.");
            return false;
        }
    }

    public async Task<bool> Include(string path, string token, string ip, int owner, string permission)
    {
        var requester = _user.Validate(token, ip);
        if (requester != 1)
        {
            _logger.LogWarning(
                $"Permission denied: Non-root user '{requester}' attempted to include resource '{path}'.");
            return false;
        }

        if (!PermissionRegex.IsMatch(permission))
        {
            _logger.LogError($"Invalid permission format: {permission}");
            return false;
        }

        var ownerUser = await _user.QueryUser(owner);
        if (ownerUser == null)
        {
            _logger.LogError($"Include failed: Owner user '{owner}' does not exist.");
            return false;
        }

        try
        {
            var relPath = Path.GetRelativePath(_config.MediaRoot, path);
            var uid = Uid(relPath);

            var existing = await _database.Table<ResourceAttribute>().Where(r => r.Uid == uid).FirstOrDefaultAsync();
            if (existing != null)
            {
                _logger.LogError($"Include failed: Resource '{relPath}' already exists in database.");
                return false;
            }

            var newResource = new ResourceAttribute
            {
                Uid = uid,
                Owner = owner,
                Permission = permission
            };

            var inserted = await _database.InsertAsync(newResource);
            if (inserted > 0)
            {
                _logger.LogInformation(
                    $"Successfully included '{relPath}' into resource management (Owner={owner}, Permission={permission}).");
                return true;
            }
            else
            {
                _logger.LogError($"Failed to include resource '{relPath}' into database.");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error including resource '{path}'.");
            return false;
        }
    }

    public async Task<bool> Exists(string path)
    {
        try
        {
            var relPath = Path.GetRelativePath(_config.MediaRoot, path);
            var uid = Uid(relPath);

            var resource = await _database.Table<ResourceAttribute>().Where(r => r.Uid == uid).FirstOrDefaultAsync();
            return resource != null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error checking existence of resource '{path}'.");
            return false;
        }
    }

    public async Task<bool> Chmod(string path, string token, string permission, string ip)
    {
        if (!await Valid(path, token, OperationType.Security, ip))
            return false;

        // Validate the permission format using the existing regex
        if (!PermissionRegex.IsMatch(permission))
        {
            _logger.LogError($"Invalid permission format: {permission}");
            return false;
        }

        try
        {
            path = Path.GetRelativePath(_config.MediaRoot, path);
            var uid = Uid(path);
            var resource = await _database.Table<ResourceAttribute>().Where(r => r.Uid == uid).FirstOrDefaultAsync();

            if (resource == null)
            {
                _logger.LogError($"Resource not found: {path}");
                return false;
            }

            resource.Permission = permission;
            var rowsAffected = await _database.UpdateAsync(resource);

            if (rowsAffected > 0)
            {
                _logger.LogInformation($"Successfully changed permissions for '{path}' to '{permission}'");
                return true;
            }
            else
            {
                _logger.LogError($"Failed to update permissions for: {path}");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error changing permissions for: {path}");
            return false;
        }
    }
    
    public async Task<bool> Chown(string path, string token, int owner, string ip)
    {
        if (!await Valid(path, token, OperationType.Security, ip))
            return false;

        // Validate that the new owner exists
        var newOwner = await _user.QueryUser(owner);
        if (newOwner == null)
        {
            _logger.LogError($"New owner '{owner}' does not exist");
            return false;
        }

        try
        {
            path = Path.GetRelativePath(_config.MediaRoot, path);
            var uid = Uid(path);
            var resource = await _database.Table<ResourceAttribute>().Where(r => r.Uid == uid).FirstOrDefaultAsync();

            if (resource == null)
            {
                _logger.LogError($"Resource not found: {path}");
                return false;
            }

            resource.Owner = owner;
            var rowsAffected = await _database.UpdateAsync(resource);

            if (rowsAffected > 0)
            {
                _logger.LogInformation($"Successfully changed ownership of '{path}' to '{owner}'");
                return true;
            }
            else
            {
                _logger.LogError($"Failed to change ownership for: {path}");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error changing ownership for: {path}");
            return false;
        }
    }

    private async Task<bool> InsertRaRow(string fullPath, int owner, string permission, bool update = false)
    {
        if (!PermissionRegex.IsMatch(permission))
        {
            _logger.LogError($"Invalid permission format: {permission}");
            return false;
        }

        var path = Path.GetRelativePath(_config.MediaRoot, fullPath);

        if (update)
            return await _database.InsertOrReplaceAsync(new ResourceAttribute()
            {
                Uid = Uid(path),
                Owner = owner,
                Permission = permission,
            }) == 1;
        else
        {
            return await _database.InsertAsync(new ResourceAttribute()
            {
                Uid = Uid(path),
                Owner = owner,
                Permission = permission,
            }) == 1;
        }
    }
}