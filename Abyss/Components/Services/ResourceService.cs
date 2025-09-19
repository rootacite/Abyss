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

    public async Task<Dictionary<string, bool>> ValidAny(string[] paths, string token, OperationType type, string ip)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        if (paths.Length == 0)
            return result; // empty input -> empty result

        // Normalize media root
        var mediaRootFull = Path.GetFullPath(_config.MediaRoot);

        // Prepare normalized full paths and early-check outside-media-root
        var fullPaths = new List<string>(paths.Length);
        foreach (var p in paths)
        {
            try
            {
                var full = Path.GetFullPath(p);
                // record normalized path as key
                if (!full.StartsWith(mediaRootFull, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError($"Path outside media root or null: {p}");
                    result[full] = false;
                }
                else
                {
                    fullPaths.Add(full);
                    // initialize to false; will set true when all checks pass
                    result[full] = false;
                }
            }
            catch (Exception ex)
            {
                // malformed path -> mark false and continue
                _logger.LogError(ex, $"Invalid path encountered in ValidAny: {p}");
                try
                {
                    result[Path.GetFullPath(p)] = false;
                }
                catch
                {
                    /* ignore */
                }
            }
        }

        if (fullPaths.Count == 0)
            return result;

        // Validate token and user once
        int uuid = _user.Validate(token, ip);
        if (uuid == -1)
        {
            _logger.LogError($"Invalid token: {token}");
            // all previously-initialized keys remain false
            return result;
        }

        User? user = await _user.QueryUser(uuid);
        if (user == null || user.Uuid != uuid)
        {
            _logger.LogError($"Verification failed: {token}");
            return result;
        }

        // Build mapping: for each input path -> list of required (uid, op)
        // Also build uid -> set of ops needed overall for batching
        var pathToReqs = new Dictionary<string, List<(string uid, OperationType op)>>(StringComparer.OrdinalIgnoreCase);
        var uidToOps = new Dictionary<string, HashSet<OperationType>>(StringComparer.OrdinalIgnoreCase);
        var uidToExampleRelPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var full in fullPaths)
        {
            try
            {
                // rel path relative to media root for Uid calculation
                var rel = Path.GetRelativePath(_config.MediaRoot, full);

                var parts = rel
                    .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                        StringSplitOptions.RemoveEmptyEntries)
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToArray();

                var reqs = new List<(string uid, OperationType op)>();

                // parents: each prefix requires Read
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    var subPath = Path.Combine(parts.Take(i + 1).ToArray());
                    var uidDir = Uid(subPath);
                    reqs.Add((uidDir, OperationType.Read));

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
                reqs.Add((uidRes, type));

                if (!uidToOps.TryGetValue(uidRes, out var resOps))
                {
                    resOps = new HashSet<OperationType>();
                    uidToOps[uidRes] = resOps;
                    uidToExampleRelPath[uidRes] = resourcePath;
                }

                resOps.Add(type);

                pathToReqs[full] = reqs;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error building requirements for path '{full}' in ValidAny.");
                // leave result[full] as false
            }
        }

        // Batch query DB for all UIDs (chunked)
        var uidsNeeded = uidToOps.Keys.ToList();
        var rasList = new List<ResourceAttribute>();

        const int sqliteMaxVariableNumber = 900;
        if (uidsNeeded.Count > 0)
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

        var raDict = rasList.ToDictionary(r => r.Uid, StringComparer.OrdinalIgnoreCase);

        // Check each uid+op once and cache results
        var permCache = new Dictionary<(string uid, OperationType op), bool>();
        foreach (var kv in uidToOps)
        {
            var uid = kv.Key;
            var ops = kv.Value;

            if (!raDict.TryGetValue(uid, out var ra))
            {
                // missing resource attribute -> all ops for this uid are false
                foreach (var op in ops)
                {
                    permCache[(uid, op)] = false;
                    var examplePath = uidToExampleRelPath.GetValueOrDefault(uid, uid);
                    _logger.LogDebug($"ValidAny: missing ResourceAttribute for Uid={uid}, example='{examplePath}'");
                }

                continue;
            }

            foreach (var op in ops)
            {
                var key = (uid, op);
                if (!permCache.TryGetValue(key, out var ok))
                {
                    ok = await CheckPermission(user, ra, op);
                    permCache[key] = ok;
                }
            }
        }

        // Compose results per original path
        foreach (var kv in pathToReqs)
        {
            var full = kv.Key;
            var reqs = kv.Value;

            bool allOk = true;
            foreach (var (uid, op) in reqs)
            {
                if (!permCache.TryGetValue((uid, op), out var ok) || !ok)
                {
                    allOk = false;
                    break;
                }
            }

            result[full] = allOk;
        }

        return result;
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

        try
        {
            var entries = Directory.EnumerateFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly).ToArray();

            if (entries.Length == 0)
                return Array.Empty<string>();

            var validMap = await ValidAny(entries, token, OperationType.Read, ip);

            var allowed = new List<string>(entries.Length);

            foreach (var entry in entries)
            {
                try
                {
                    var full = Path.GetFullPath(entry);
                    if (validMap.TryGetValue(full, out var ok) && ok)
                    {
                        allowed.Add(Path.GetRelativePath(path, entry));
                    }
                    else
                    {
                        _logger.LogDebug($"Query: access denied or not managed for '{entry}' (user token: {token}) - item skipped.");
                    }
                }
                catch (Exception exEntry)
                {
                    _logger.LogError(exEntry, $"Error processing entry '{entry}' in Query.");
                }
            }

            return allowed.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error while listing directory '{path}' in Query.");
            return null;
        }
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

    // public async Task<bool> Put(string path, string token, string ip)
    // {
    //     throw new NotImplementedException();
    // }

    // public async Task<bool> Delete(string path, string token, string ip)
    // {
    //     throw new NotImplementedException();
    // }

    public async Task<bool> Exclude(string path, string token, string ip)
    {
        var requester = _user.Validate(token, ip);
        if (requester != 1)
        {
            _logger.LogWarning(
                $"Permission denied: Non-root user '{requester}' attempted to exclude resource '{path}'.");
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

    public async Task<bool> Chmod(string path, string token, string permission, string ip, bool recursive = false)
    {
        // Validate permission format first
        if (!PermissionRegex.IsMatch(permission))
        {
            _logger.LogError($"Invalid permission format: {permission}");
            return false;
        }

        // Normalize path to full path (Valid / ValidAll expect absolute path starting with media root)
        path = Path.GetFullPath(path);

        // If recursive and path is directory, collect all descendants (iterative)
        List<string> targets = new List<string>();
        try
        {
            if (recursive && Directory.Exists(path))
            {
                // include root directory itself
                targets.Add(path);
                // Enumerate all files and directories under path iteratively
                foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                {
                    targets.Add(entry);
                }

                // Permission check for all targets
                if (!await ValidAll(targets.ToArray(), token, OperationType.Security, ip))
                {
                    _logger.LogWarning($"Permission denied for recursive chmod on '{path}'");
                    return false;
                }
            }
            else
            {
                // Non-recursive or target is a file: validate single path
                if (!await Valid(path, token, OperationType.Security, ip))
                {
                    _logger.LogWarning($"Permission denied for chmod on '{path}'");
                    return false;
                }

                targets.Add(path);
            }

            // Convert targets to relative paths and UIDs
            var relUids = targets.Select(t => Path.GetRelativePath(_config.MediaRoot, t))
                .Select(rel => Uid(rel))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (relUids.Count == 0)
            {
                _logger.LogWarning($"No targets resolved for chmod on '{path}'");
                return false;
            }

            // Batch query existing ResourceAttribute rows for these UIDs (chunk to respect SQLite param limit)
            var rows = new List<ResourceAttribute>();
            const int sqliteMaxVariableNumber = 900;
            for (int i = 0; i < relUids.Count; i += sqliteMaxVariableNumber)
            {
                var chunk = relUids.Skip(i).Take(sqliteMaxVariableNumber).ToList();
                var placeholders = string.Join(",", chunk.Select(_ => "?"));
                var queryArgs = chunk.Cast<object>().ToArray();
                var sql = $"SELECT * FROM ResourceAttributes WHERE Uid IN ({placeholders})";
                var chunkResult = await _database.QueryAsync<ResourceAttribute>(sql, queryArgs);
                rows.AddRange(chunkResult);
            }

            var rowDict = rows.ToDictionary(r => r.Uid, StringComparer.OrdinalIgnoreCase);

            int updatedCount = 0;
            foreach (var uid in relUids)
            {
                if (rowDict.TryGetValue(uid, out var ra))
                {
                    ra.Permission = permission;
                    var res = await _database.UpdateAsync(ra);
                    if (res > 0) updatedCount++;
                    else _logger.LogError($"Failed to update permission row (UID={uid}) for chmod on '{path}'");
                }
                else
                {
                    // Resource not managed by DB — skip but log
                    _logger.LogWarning($"Chmod skipped: resource not found in DB (Uid={uid}) for target '{path}'");
                }
            }

            if (updatedCount > 0)
            {
                _logger.LogInformation(
                    $"Chmod: updated permissions for {updatedCount} resource(s) (root='{path}', recursive={recursive})");
                return true;
            }
            else
            {
                _logger.LogWarning($"Chmod: no resources updated for '{path}' (recursive={recursive})");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error changing permissions for: {path}");
            return false;
        }
    }

    public async Task<bool> Chown(string path, string token, int owner, string ip, bool recursive = false)
    {
        // Validate new owner exists
        var newOwner = await _user.QueryUser(owner);
        if (newOwner == null)
        {
            _logger.LogError($"New owner '{owner}' does not exist");
            return false;
        }

        // Normalize
        path = Path.GetFullPath(path);

        // Permission checks and target collection
        List<string> targets = new List<string>();
        try
        {
            if (recursive && Directory.Exists(path))
            {
                targets.Add(path);
                foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                {
                    targets.Add(entry);
                }

                if (!await ValidAll(targets.ToArray(), token, OperationType.Security, ip))
                {
                    _logger.LogWarning($"Permission denied for recursive chown on '{path}'");
                    return false;
                }
            }
            else
            {
                if (!await Valid(path, token, OperationType.Security, ip))
                {
                    _logger.LogWarning($"Permission denied for chown on '{path}'");
                    return false;
                }

                targets.Add(path);
            }

            // Build UID list
            var relUids = targets.Select(t => Path.GetRelativePath(_config.MediaRoot, t))
                .Select(rel => Uid(rel))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (relUids.Count == 0)
            {
                _logger.LogWarning($"No targets resolved for chown on '{path}'");
                return false;
            }

            // Batch query DB
            var rows = new List<ResourceAttribute>();
            const int sqliteMaxVariableNumber = 900;
            for (int i = 0; i < relUids.Count; i += sqliteMaxVariableNumber)
            {
                var chunk = relUids.Skip(i).Take(sqliteMaxVariableNumber).ToList();
                var placeholders = string.Join(",", chunk.Select(_ => "?"));
                var queryArgs = chunk.Cast<object>().ToArray();
                var sql = $"SELECT * FROM ResourceAttributes WHERE Uid IN ({placeholders})";
                var chunkResult = await _database.QueryAsync<ResourceAttribute>(sql, queryArgs);
                rows.AddRange(chunkResult);
            }

            var rowDict = rows.ToDictionary(r => r.Uid, StringComparer.OrdinalIgnoreCase);

            int updatedCount = 0;
            foreach (var uid in relUids)
            {
                if (rowDict.TryGetValue(uid, out var ra))
                {
                    ra.Owner = owner;
                    var res = await _database.UpdateAsync(ra);
                    if (res > 0) updatedCount++;
                    else _logger.LogError($"Failed to update owner row (UID={uid}) for chown on '{path}'");
                }
                else
                {
                    _logger.LogWarning($"Chown skipped: resource not found in DB (Uid={uid}) for target '{path}'");
                }
            }

            if (updatedCount > 0)
            {
                _logger.LogInformation(
                    $"Chown: changed owner for {updatedCount} resource(s) (root='{path}', recursive={recursive})");
                return true;
            }
            else
            {
                _logger.LogWarning($"Chown: no resources updated for '{path}' (recursive={recursive})");
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
    
    public async Task<ResourceAttribute?> GetAttribute(string path)
    {
        try
        {
            // normalize to full path
            var full = Path.GetFullPath(path);

            // ensure it's under media root
            var mediaRootFull = Path.GetFullPath(_config.MediaRoot);
            if (!full.StartsWith(mediaRootFull, StringComparison.OrdinalIgnoreCase))
                return null;

            var rel = Path.GetRelativePath(_config.MediaRoot, full);
            var uid = Uid(rel);

            var ra = await _database.Table<ResourceAttribute>()
                .Where(r => r.Uid == uid)
                .FirstOrDefaultAsync();

            return ra;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"GetAttribute failed for path '{path}'");
            return null;
        }
    }
}