using System;
using System.Collections.Generic;

namespace OpenSlideSharp.Viewer.Core;

/// <summary>
/// A thread-safe least-recently-used cache of tiles, limited by their total size (<c>width * height * 4</c> bytes
/// each). Tiles it drops, by eviction, replacement, <see cref="Clear"/> or <see cref="Dispose"/>, are disposed.
/// </summary>
internal sealed class TileCache : IDisposable
{
    /// <summary>The default budget: 256 MiB.</summary>
    public const long DefaultBudgetBytes = 256L * 1024 * 1024;

    private readonly long _budget;
    private readonly object _lock = new();
    private readonly Dictionary<TileKey, LinkedListNode<Entry>> _entries = new();
    private readonly LinkedList<Entry> _recency = new(); // most recently used first
    private long _size;
    private bool _disposed;

    public TileCache(long budgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        _budget = budgetBytes;
    }

    public long SizeBytes
    {
        get
        {
            lock (_lock)
            {
                return _size;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Looks up a tile and, if it is there, marks it as the most recently used.</summary>
    public bool TryGet(TileKey key, out TileImage? tile)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
                tile = node.Value.Tile;
                return true;
            }
        }

        tile = null;
        return false;
    }

    /// <summary>Looks up a tile without changing its place in the eviction order.</summary>
    public bool Contains(TileKey key)
    {
        lock (_lock)
        {
            return _entries.ContainsKey(key);
        }
    }

    /// <summary>
    /// Adds a tile as the most recently used one, replacing any tile with the same key, then evicts the least
    /// recently used tiles until the cache is within its budget. A tile larger than the whole budget is not kept.
    /// After <see cref="Dispose"/> the tile is disposed straight away.
    /// </summary>
    public void Add(TileKey key, TileImage tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        var dropped = new List<TileImage>();
        lock (_lock)
        {
            if (_disposed)
            {
                dropped.Add(tile);
            }
            else
            {
                if (_entries.TryGetValue(key, out LinkedListNode<Entry>? existing))
                {
                    if (ReferenceEquals(existing.Value.Tile, tile))
                    {
                        _recency.Remove(existing);
                        _recency.AddFirst(existing);
                        return;
                    }

                    Remove(existing);
                    dropped.Add(existing.Value.Tile);
                }

                _entries.Add(key, _recency.AddFirst(new Entry(key, tile)));
                _size += tile.SizeBytes;
                while (_size > _budget && _recency.Last is { } last)
                {
                    Remove(last);
                    dropped.Add(last.Value.Tile);
                }
            }
        }

        foreach (TileImage image in dropped)
        {
            image.Dispose();
        }
    }

    /// <summary>
    /// Removes and disposes the tile cached under <paramref name="key"/> if it is <paramref name="tile"/>; a tile that
    /// has replaced it stays.
    /// </summary>
    /// <returns>Whether the tile was removed.</returns>
    public bool Remove(TileKey key, TileImage tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out LinkedListNode<Entry>? node) || !ReferenceEquals(node.Value.Tile, tile))
            {
                return false;
            }

            Remove(node);
        }

        tile.Dispose();
        return true;
    }

    /// <summary>Removes and disposes every tile.</summary>
    public void Clear()
    {
        List<TileImage> dropped;
        lock (_lock)
        {
            dropped = new List<TileImage>(_entries.Count);
            foreach (Entry entry in _recency)
            {
                dropped.Add(entry.Tile);
            }

            _entries.Clear();
            _recency.Clear();
            _size = 0;
        }

        foreach (TileImage image in dropped)
        {
            image.Dispose();
        }
    }

    /// <summary>Clears the cache; tiles added later are disposed instead of kept.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }

        Clear();
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        _recency.Remove(node);
        _entries.Remove(node.Value.Key);
        _size -= node.Value.Tile.SizeBytes;
    }

    private readonly record struct Entry(TileKey Key, TileImage Tile);
}
