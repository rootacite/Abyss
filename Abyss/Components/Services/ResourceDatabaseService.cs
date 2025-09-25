using System.IO.Hashing;
using System.Text;
using System.Text.RegularExpressions;
using Abyss.Components.Static;
using Abyss.Model;
using SQLite;

namespace Abyss.Components.Services;

public class ResourceDatabaseService
{
    private readonly ILogger<ResourceDatabaseService> _logger;
    private readonly ConfigureService _config;
    public readonly SQLiteAsyncConnection ResourceDatabase;
    public static readonly Regex PermissionRegex = new("^([r-][w-]),([r-][w-]),([r-][w-])$", RegexOptions.Compiled);

    public ResourceDatabaseService(ConfigureService config, ILogger<ResourceDatabaseService> logger)
    {
        _config = config;
        _logger = logger;
        
        ResourceDatabase = new SQLiteAsyncConnection(config.RaDatabase, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create);
        ResourceDatabase.CreateTableAsync<ResourceAttribute>().Wait();
        
        
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
    
    private async Task<bool> InsertRaRow(string fullPath, int owner, string permission, bool update = false)
    {
        if (!PermissionRegex.IsMatch(permission))
        {
            _logger.LogError($"Invalid permission format: {permission}");
            return false;
        }

        var path = Path.GetRelativePath(_config.MediaRoot, fullPath);

        if (update)
            return await ResourceDatabase.InsertOrReplaceAsync(new ResourceAttribute()
            {
                Uid = Uid(path),
                Owner = owner,
                Permission = permission,
            }) == 1;
        else
        {
            return await ResourceDatabase.InsertAsync(new ResourceAttribute()
            {
                Uid = Uid(path),
                Owner = owner,
                Permission = permission,
            }) == 1;
        }
    }
    
    public static string Uid(string path)
    {
        var b = Encoding.UTF8.GetBytes(path);
        var r = XxHash128.Hash(b, 0x11451419);
        return Convert.ToBase64String(r);
    }
    
    public Task<int> ExecuteAsync(string sql, params object[] args)
        => ResourceDatabase.ExecuteAsync(sql, args);

    public Task<List<T>> QueryAsync<T>(string sql, params object[] args) where T : new()
        => ResourceDatabase.QueryAsync<T>(sql, args);

    public async Task<ResourceAttribute?> GetResourceAttributeByUidAsync(string uid)
    {
        return await ResourceDatabase.Table<ResourceAttribute>().Where(r => r.Uid == uid).FirstOrDefaultAsync();
    }
    
    public async Task<List<ResourceAttribute>> GetResourceAttributesByUidsAsync(IEnumerable<string> uidsEnumerable)
    {
        var uids = uidsEnumerable.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var result = new List<ResourceAttribute>();
        if (uids.Count == 0) return result;

        const int sqliteMaxVariableNumber = 900;
        for (int i = 0; i < uids.Count; i += sqliteMaxVariableNumber)
        {
            var chunk = uids.Skip(i).Take(sqliteMaxVariableNumber).ToList();
            var placeholders = string.Join(",", chunk.Select(_ => "?"));
            var sql = $"SELECT * FROM ResourceAttributes WHERE Uid IN ({placeholders})";
            try
            {
                var chunkResult = await ResourceDatabase.QueryAsync<ResourceAttribute>(sql, chunk.Cast<object>().ToArray());
                if (chunkResult != null && chunkResult.Any())
                    result.AddRange(chunkResult);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error querying ResourceAttributes chunk (size {chunk.Count}).");
            }
        }
        return result;
    }
    
    public async Task<int> InsertResourceAttributeAsync(ResourceAttribute ra)
    {
        if (ra == null) throw new ArgumentNullException(nameof(ra));
        try
        {
            return await ResourceDatabase.InsertAsync(ra);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InsertResourceAttributeAsync failed.");
            return -1;
        }
    }
    
    public async Task<int> InsertResourceAttributesAsync(IEnumerable<ResourceAttribute> ras)
    {
        var list = ras.ToList();
        if (!list.Any()) return 0;

        try
        {
            return await ResourceDatabase.InsertAllAsync(list);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InsertResourceAttributesAsync failed.");
            return -1;
        }
    }
    
    public async Task<int> InsertOrReplaceResourceAttributeAsync(ResourceAttribute ra)
    {
        if (ra == null) throw new ArgumentNullException(nameof(ra));
        try
        {
            return await ResourceDatabase.InsertOrReplaceAsync(ra);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InsertOrReplaceResourceAttributeAsync failed.");
            return -1;
        }
    }
    
    public async Task<int> DeleteByUidAsync(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return 0;
        try
        {
            var sql = "DELETE FROM ResourceAttributes WHERE Uid = ?";
            return await ResourceDatabase.ExecuteAsync(sql, uid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"DeleteByUidAsync failed for uid={uid}");
            return -1;
        }
    }
    
    public async Task<int> UpdatePermissionsByUidsAsync(IEnumerable<string> uids, string permission)
    {
        var list = uids.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!list.Any()) return 0;

        int updatedCount = 0;
        const int sqliteMaxVariableNumber = 900;
        for (int i = 0; i < list.Count; i += sqliteMaxVariableNumber)
        {
            var chunk = list.Skip(i).Take(sqliteMaxVariableNumber).ToList();
            var placeholders = string.Join(",", chunk.Select(_ => "?"));
            var args = new List<object> { permission };
            args.AddRange(chunk);
            var sql = $"UPDATE ResourceAttributes SET Permission = ? WHERE Uid IN ({placeholders})";
            try
            {
                var rows = await ResourceDatabase.ExecuteAsync(sql, args.ToArray());
                updatedCount += rows;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"UpdatePermissionsByUidsAsync chunk failed (size {chunk.Count}).");
            }
        }

        return updatedCount;
    }
    
    public async Task<int> UpdateOwnerByUidsAsync(IEnumerable<string> uids, int owner)
    {
        var list = uids.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!list.Any()) return 0;

        int updatedCount = 0;
        const int sqliteMaxVariableNumber = 900;
        for (int i = 0; i < list.Count; i += sqliteMaxVariableNumber)
        {
            var chunk = list.Skip(i).Take(sqliteMaxVariableNumber).ToList();
            var placeholders = string.Join(",", chunk.Select(_ => "?"));
            var args = new List<object> { owner };
            args.AddRange(chunk);
            var sql = $"UPDATE ResourceAttributes SET Owner = ? WHERE Uid IN ({placeholders})";
            try
            {
                var rows = await ResourceDatabase.ExecuteAsync(sql, args.ToArray());
                updatedCount += rows;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"UpdateOwnerByUidsAsync chunk failed (size {chunk.Count}).");
            }
        }

        return updatedCount;
    }
    
    public async Task<bool> ExistsUidAsync(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return false;
        try
        {
            var ra = await GetResourceAttributeByUidAsync(uid);
            return ra != null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"ExistsUidAsync failed for uid={uid}");
            return false;
        }
    }
}