using SQLite;
using Index = Abyss.Model.Index;
namespace Abyss.Components.Services;

public class IndexService: IAsyncDisposable
{
    private readonly SQLiteAsyncConnection _db;
    private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
    private bool _disposed;


    public IndexService(ConfigureService cs)
    {
        if (string.IsNullOrWhiteSpace(cs.IndexDatabase)) throw new ArgumentNullException(nameof(cs.IndexDatabase));
        _db = new SQLiteAsyncConnection(cs.IndexDatabase);

        _db.CreateTableAsync<Index>().Wait();
        EnsureRootExistsAsync().Wait();
    }

    private async Task EnsureRootExistsAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Ensure there is a root node with Id = 1. If it already exists, this does nothing.
            // Using INSERT OR IGNORE so that explicit Id insertion will be ignored if existing.
            await _db.ExecuteAsync("INSERT OR IGNORE INTO \"Index\" (Id, Type, Reference, Children) VALUES (?, ?, ?, ?)",
                1, 0, string.Empty, string.Empty).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Index?> GetByIdAsync(int id)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await _db.FindAsync<Index>(id).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Index> InsertNodeAsChildAsync(int parentId, int type, string reference = "")
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var parent = await _db.FindAsync<Index>(parentId).ConfigureAwait(false);
            if (parent == null) throw new InvalidOperationException($"Parent node {parentId} not found");

            var node = new Index
            {
                Type = type,
                Reference = reference,
                Children = string.Empty
            };

            await _db.InsertAsync(node).ConfigureAwait(false);

            // Update parent's children
            var children = ParseChildren(parent.Children);
            if (!children.Contains(node.Id))
            {
                children.Add(node.Id);
                parent.Children = SerializeChildren(children);
                await _db.UpdateAsync(parent).ConfigureAwait(false);
            }

            return node;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteNodeAsync(int id)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var node = await _db.FindAsync<Index>(id).ConfigureAwait(false);
            if (node == null) return false;

            await _db.DeleteAsync(node).ConfigureAwait(false);

            // Remove references from all parents
            var all = await _db.Table<Index>().ToListAsync().ConfigureAwait(false);
            foreach (var parent in all)
            {
                var children = ParseChildren(parent.Children);
                if (children.Remove(id))
                {
                    parent.Children = SerializeChildren(children);
                    await _db.UpdateAsync(parent).ConfigureAwait(false);
                }
            }

            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateTypeOrReferenceAsync(int id, int? type = null, string? reference = null)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var node = await _db.FindAsync<Index>(id).ConfigureAwait(false) ?? throw new InvalidOperationException($"Node {id} not found");
            var changed = false;
            if (type.HasValue && node.Type != type.Value)
            {
                node.Type = type.Value;
                changed = true;
            }
            if (reference != null && node.Reference != reference)
            {
                node.Reference = reference;
                changed = true;
            }
            if (changed) await _db.UpdateAsync(node).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AddEdgeAsync(int fromId, int toId)
    {
        if (fromId == toId) throw new InvalidOperationException("Self-loop not allowed");

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var from = await _db.FindAsync<Index>(fromId).ConfigureAwait(false) ?? throw new InvalidOperationException($"From node {fromId} not found");
            _ = await _db.FindAsync<Index>(toId).ConfigureAwait(false) ?? throw new InvalidOperationException($"To node {toId} not found");

            var children = ParseChildren(from.Children);
            if (!children.Contains(toId))
            {
                children.Add(toId);
                from.Children = SerializeChildren(children);
                await _db.UpdateAsync(from).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> RemoveEdgeAsync(int fromId, int toId)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var from = await _db.FindAsync<Index>(fromId).ConfigureAwait(false);
            if (from == null) return false;

            var children = ParseChildren(from.Children);
            var removed = children.Remove(toId);
            if (removed)
            {
                from.Children = SerializeChildren(children);
                await _db.UpdateAsync(from).ConfigureAwait(false);
            }
            return removed;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<int>> GetChildrenIdsAsync(int id)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var node = await _db.FindAsync<Index>(id).ConfigureAwait(false);
            if (node == null) return new List<int>();
            return ParseChildren(node.Children);
        }
        finally
        {
            _lock.Release();
        }
    }
    private static List<int> ParseChildren(string children)
    {
        if (string.IsNullOrWhiteSpace(children)) return new List<int>();
        return children.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                       .Select(s => s.Trim())
                       .Where(s => int.TryParse(s, out _))
                       .Select(int.Parse)
                       .ToList();
    }
    private static string SerializeChildren(List<int> children)
    {
        if (children.Count == 0) return string.Empty;
        var seen = new HashSet<int>();
        var ordered = new List<int>();
        foreach (var c in children)
        {
            if (seen.Add(c)) ordered.Add(c);
        }
        return string.Join(",", ordered);
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lock.Dispose();
        // SQLiteAsyncConnection does not expose a Close method; rely on finalizer if any.
        await Task.CompletedTask;
    }
    
    
}
