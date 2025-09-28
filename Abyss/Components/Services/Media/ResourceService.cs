// ResourceService.cs

using Abyss.Components.Services.Misc;
using Abyss.Components.Services.Security;
using Abyss.Components.Static;
using Abyss.Model.Media;
using Abyss.Model.Security;
using Microsoft.AspNetCore.Mvc;

namespace Abyss.Components.Services.Media;

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
    private readonly ResourceDatabaseService _db;

    public ResourceService(ILogger<ResourceService> logger, ConfigureService config, UserService user, ResourceDatabaseService db)
    {
        _logger = logger;
        _config = config;
        _user = user;
        _db = db;
    }

    // Create UID only for resources, without considering advanced hash security such as adding salt
    private async Task<Dictionary<string, bool>> ValidAny(string[] paths, string token, OperationType type, string ip)
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
                    var uidDir = ResourceDatabaseService.Uid(subPath);
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
                var uidRes = ResourceDatabaseService.Uid(resourcePath);
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

        // Batch query DB for all UIDs (via DatabaseService)
        var uidsNeeded = uidToOps.Keys.ToList();
        var rasList = new List<ResourceAttribute>();
        if (uidsNeeded.Count > 0)
        {
            rasList = await _db.GetResourceAttributesByUidsAsync(uidsNeeded);
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
                var uidDir = ResourceDatabaseService.Uid(subPath);
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
            var uidRes = ResourceDatabaseService.Uid(resourcePath);
            if (!uidToOps.TryGetValue(uidRes, out var resOps))
            {
                resOps = new HashSet<OperationType>();
                uidToOps[uidRes] = resOps;
                uidToExampleRelPath[uidRes] = resourcePath;
            }

            resOps.Add(type);
        }

        // 4. batch query DB for all UIDs using DatabaseService
        var uidsNeeded = uidToOps.Keys.ToList();
        var rasList = new List<ResourceAttribute>();
        if (uidsNeeded.Count > 0)
        {
            rasList = await _db.GetResourceAttributesByUidsAsync(uidsNeeded);
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
    
    private async Task<bool> Valid(string path, string token, OperationType type, string ip)
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
            var uidDir = ResourceDatabaseService.Uid(subPath);
            var raDir = await _db.GetResourceAttributeByUidAsync(uidDir);
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

        var uid = ResourceDatabaseService.Uid(path);
        ResourceAttribute? ra = await _db.GetResourceAttributeByUidAsync(uid);
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

        if (!ResourceDatabaseService.PermissionRegex.IsMatch(ra.Permission)) return false;

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
                        _logger.LogDebug(
                            $"Query: access denied or not managed for '{entry}' (user token: {token}) - item skipped.");
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

    public async Task<PhysicalFileResult?> Get(string path, string token, string ip, string contentType)
    {
        var b = await Valid(path, token, OperationType.Read, ip);
        if (b) return new PhysicalFileResult(path, contentType)
            {
                EnableRangeProcessing = true
            };

        return null;
    }

    public async Task<string?> GetString(string path, string token, string ip)
    {
        var b = await Valid(path, token, OperationType.Read, ip);
        if (b)
        {
            return await File.ReadAllTextAsync(path);
        }
        return null;
    }

    public async Task<Dictionary<string, string?>> GetAllString(string[] paths, string token, string ip)
    {
        Dictionary<string, string?> result = new();
        var validMap = await ValidAny(paths, token, OperationType.Read, ip);
        foreach (var entry in validMap)
        {
            if (entry.Value)
            {
                result[entry.Key] = await File.ReadAllTextAsync(entry.Key);
            }
        }

        return result;
    }

    public async Task<bool> UpdateString(string path, string token, string ip, string content)
    {
        var b = await Valid(path, token, OperationType.Write, ip);
        if (b)
        {
            await File.WriteAllTextAsync(path, content);
            return true;
        }

        return false;
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
                var uid = ResourceDatabaseService.Uid(currentPath);
                var existing = await _db.GetResourceAttributeByUidAsync(uid);

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
                await _db.InsertResourceAttributesAsync(newResources);
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
            var uid = ResourceDatabaseService.Uid(relPath);

            var resource = await _db.GetResourceAttributeByUidAsync(uid);
            if (resource == null)
            {
                _logger.LogError($"Exclude failed: Resource '{relPath}' not found in database.");
                return false;
            }

            var deleted = await _db.DeleteByUidAsync(uid);
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

        if (!ResourceDatabaseService.PermissionRegex.IsMatch(permission))
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
            var uid = ResourceDatabaseService.Uid(relPath);

            var existing = await _db.GetResourceAttributeByUidAsync(uid);
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

            var inserted = await _db.InsertResourceAttributeAsync(newResource);
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
            var uid = ResourceDatabaseService.Uid(relPath);

            return await _db.ExistsUidAsync(uid);
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
        if (!ResourceDatabaseService.PermissionRegex.IsMatch(permission))
        {
            _logger.LogError($"Invalid permission format: {permission}");
            return false;
        }

        // Normalize path to full path
        path = Path.GetFullPath(path);

        // Collect targets and permission checks
        List<string> targets = new List<string>();
        try
        {
            if (recursive && Directory.Exists(path))
            {
                _logger.LogInformation($"Recursive directory '{path}'.");
                targets.Add(path);
                foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                {
                    targets.Add(entry);
                }

                if (!await ValidAll(targets.ToArray(), token, OperationType.Security, ip))
                {
                    _logger.LogWarning($"Permission denied for recursive chmod on '{path}'");
                    return false;
                }

                _logger.LogInformation($"Successfully validated chmod on '{path}'.");
            }
            else
            {
                if (!await Valid(path, token, OperationType.Security, ip))
                {
                    _logger.LogWarning($"Permission denied for chmod on '{path}'");
                    return false;
                }

                targets.Add(path);
            }

            // Build distinct UIDs
            var relUids = targets
                .Select(t => Path.GetRelativePath(_config.MediaRoot, t))
                .Select(rel => ResourceDatabaseService.Uid(rel))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (relUids.Count == 0)
            {
                _logger.LogWarning($"No targets resolved for chmod on '{path}'");
                return false;
            }

            // Use DatabaseService to perform chunked updates
            var updatedCount = await _db.UpdatePermissionsByUidsAsync(relUids, permission);

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

            // Build distinct UIDs
            var relUids = targets
                .Select(t => Path.GetRelativePath(_config.MediaRoot, t))
                .Select(rel => ResourceDatabaseService.Uid(rel))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (relUids.Count == 0)
            {
                _logger.LogWarning($"No targets resolved for chown on '{path}'");
                return false;
            }

            // Use DatabaseService to perform chunked owner updates
            var updatedCount = await _db.UpdateOwnerByUidsAsync(relUids, owner);

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
            var uid = ResourceDatabaseService.Uid(rel);

            var ra = await _db.GetResourceAttributeByUidAsync(uid);

            return ra;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"GetAttribute failed for path '{path}'");
            return null;
        }
    }
}