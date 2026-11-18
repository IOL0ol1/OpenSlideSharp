using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using OpenSlideSharp.Viewer.Core;
using Xunit;

namespace OpenSlideSharp.Viewer.Tests;

public class TileCacheTests
{
    private const long TileBytes = 256 * 256 * 4; // 256 KiB

    private static TileImage Tile(int size = 256) => new(size, size, new byte[size * size * 4]);

    private static TileKey Key(int i) => new(0, i, 0);

    [Fact]
    public void Cache_StaysWithinBudget()
    {
        using var cache = new TileCache(1024 * 1024);
        var tiles = new TileImage[10];

        for (int i = 0; i < tiles.Length; i++)
        {
            tiles[i] = Tile();
            cache.Add(Key(i), tiles[i]);
            Assert.True(cache.SizeBytes <= 1024 * 1024, $"{cache.SizeBytes} bytes after adding tile {i}");
        }

        Assert.Equal(4, cache.Count);
        Assert.Equal(4 * TileBytes, cache.SizeBytes);
        for (int i = 0; i < 6; i++)
        {
            Assert.True(tiles[i].IsDisposed, $"tile {i} was evicted but not disposed");
            Assert.False(cache.TryGet(Key(i), out _));
        }

        for (int i = 6; i < 10; i++)
        {
            Assert.False(tiles[i].IsDisposed);
            Assert.True(cache.TryGet(Key(i), out TileImage? tile));
            Assert.Same(tiles[i], tile);
        }

        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.SizeBytes);
        Assert.All(tiles, tile => Assert.True(tile.IsDisposed));
    }

    [Fact]
    public void Cache_LruOrder()
    {
        using var cache = new TileCache(3 * TileBytes);
        TileImage a = Tile(), b = Tile(), c = Tile(), d = Tile();
        cache.Add(Key(0), a);
        cache.Add(Key(1), b);
        cache.Add(Key(2), c);

        Assert.True(cache.TryGet(Key(0), out _)); // a is now the most recently used
        cache.Add(Key(3), d);

        Assert.True(b.IsDisposed);
        Assert.False(a.IsDisposed || c.IsDisposed || d.IsDisposed);
        Assert.False(cache.TryGet(Key(1), out _));
        Assert.Equal(3, cache.Count);

        // c is now the least recently used.
        TileImage e = Tile();
        cache.Add(Key(4), e);
        Assert.True(c.IsDisposed);
        Assert.False(a.IsDisposed || d.IsDisposed || e.IsDisposed);
    }

    [Fact]
    public void Cache_AddSameKey_ReplacesAndDisposesOld()
    {
        using var cache = new TileCache(3 * TileBytes);
        TileImage first = Tile(), second = Tile();
        cache.Add(Key(0), first);

        cache.Add(Key(0), second);
        cache.Add(Key(0), second); // the same tile again is a no-op

        Assert.True(first.IsDisposed);
        Assert.False(second.IsDisposed);
        Assert.Equal(1, cache.Count);
        Assert.Equal(TileBytes, cache.SizeBytes);
        Assert.True(cache.TryGet(Key(0), out TileImage? tile));
        Assert.Same(second, tile);
    }

    [Fact]
    public void Cache_TileLargerThanBudget_IsNotKept()
    {
        using var cache = new TileCache(TileBytes - 1);
        TileImage tile = Tile();

        cache.Add(Key(0), tile);

        Assert.True(tile.IsDisposed);
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.SizeBytes);
    }

    [Fact]
    public void Cache_Dispose_DisposesTilesAndIgnoresLateAdds()
    {
        var cache = new TileCache(3 * TileBytes);
        TileImage kept = Tile(), late = Tile();
        cache.Add(Key(0), kept);

        cache.Dispose();
        cache.Add(Key(1), late); // a loader thread may still finish a read

        Assert.True(kept.IsDisposed);
        Assert.True(late.IsDisposed);
        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet(Key(1), out _));
    }

    [Fact]
    public void Cache_ConcurrentAccess()
    {
        const int Size = 64;
        const long Bytes = Size * Size * 4;
        const long Budget = 10 * Bytes;
        using var cache = new TileCache(Budget);
        var created = new ConcurrentBag<TileImage>();
        var errors = new ConcurrentQueue<Exception>();
        using var start = new Barrier(8);

        Thread[] threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
        {
            try
            {
                var random = new Random(t);
                start.SignalAndWait();
                for (int i = 0; i < 5000; i++)
                {
                    var key = new TileKey(0, random.Next(40), 0); // shared by all threads, so keys collide
                    if (i % 2 == 0)
                    {
                        TileImage tile = Tile(Size);
                        created.Add(tile);
                        cache.Add(key, tile);
                    }
                    else if (cache.TryGet(key, out TileImage? tile))
                    {
                        Assert.NotNull(tile);
                    }

                    if (cache.SizeBytes > Budget)
                    {
                        throw new InvalidOperationException($"{cache.SizeBytes} bytes in the cache");
                    }
                }
            }
            catch (Exception e)
            {
                errors.Enqueue(e);
            }
        })).ToArray();

        foreach (Thread thread in threads)
        {
            thread.Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        Assert.Empty(errors);
        Assert.True(cache.SizeBytes <= Budget);
        Assert.Equal(cache.Count * Bytes, cache.SizeBytes);
        // Every tile that left the cache was disposed; the ones still in it were not.
        Assert.Equal(cache.Count, created.Count(tile => !tile.IsDisposed));
    }
}
