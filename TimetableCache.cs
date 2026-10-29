internal sealed class TimetableCache
{
    private const int MaxEntries = 30;
    private readonly object _sync = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<CacheEntry> _recent = new();

    public int Count
    {
        get
        {
            lock (_sync)
                return _entries.Count;
        }
    }

    public string? GetOrCreate(string key, Func<string?> extract)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out LinkedListNode<CacheEntry>? node))
            {
                _recent.Remove(node);
                _recent.AddFirst(node);
                return node.Value.Table;
            }
        }

        string? table = extract();
        if (table is null)
            return null;

        lock (_sync)
        {
            if (_entries.TryGetValue(key, out LinkedListNode<CacheEntry>? existing))
            {
                _recent.Remove(existing);
                _recent.AddFirst(existing);
                return existing.Value.Table;
            }

            var node = _recent.AddFirst(new CacheEntry(key, table));
            _entries.Add(key, node);

            if (_entries.Count > MaxEntries)
            {
                LinkedListNode<CacheEntry> last = _recent.Last!;
                _entries.Remove(last.Value.Key);
                _recent.RemoveLast();
            }
        }

        return table;
    }

    internal bool Contains(string key)
    {
        lock (_sync)
            return _entries.ContainsKey(key);
    }

    private readonly record struct CacheEntry(string Key, string Table);
}
