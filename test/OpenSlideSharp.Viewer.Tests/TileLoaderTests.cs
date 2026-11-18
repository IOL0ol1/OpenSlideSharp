using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenSlideSharp.Viewer.Core;
using Xunit;

namespace OpenSlideSharp.Viewer.Tests;

public class TileLoaderTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Loader_LoadsAndNotifies()
    {
        var reader = new FakeReader();
        using var cache = new TileCache(1 << 20);
        var loaded = new ConcurrentQueue<(TileKey Key, bool Cached, int Thread)>();
        using var loader = new TileLoader(reader.Read, cache, 1, key =>
            loaded.Enqueue((key, cache.TryGet(key, out _), Environment.CurrentManagedThreadId)));
        TileKey[] keys = [new(0, 0, 0), new(0, 1, 0), new(1, 0, 0)];

        loader.Request(keys);
        Wait.Until(() => loaded.Count == 3 && loader.Pending == 0, "the tiles are loaded");

        Assert.Equal(1, loader.Generation);
        Assert.Equal(keys, reader.Reads); // in the order given
        Assert.Equal(keys, loaded.Select(l => l.Key));
        Assert.All(loaded, l => Assert.True(l.Cached, "loaded fired before the tile was cached"));
        Assert.All(loaded, l => Assert.NotEqual(Environment.CurrentManagedThreadId, l.Thread));
        foreach (TileKey key in keys)
        {
            Assert.True(cache.TryGet(key, out TileImage? tile));
            Assert.Contains(tile, reader.Returned);
        }
    }

    [Fact]
    public void Loader_NewGeneration_DropsQueuedRequests()
    {
        var reader = new FakeReader(blocked: true);
        using var cache = new TileCache(1 << 20);
        var loaded = new ConcurrentQueue<TileKey>();
        var loader = new TileLoader(reader.Read, cache, 1, loaded.Enqueue);
        var first = new TileKey(0, 0, 0);

        loader.Request([first]);
        Assert.True(reader.Entered.Wait(Timeout, Token), "the first read did not start");
        loader.Request(Enumerable.Range(1, 100).Select(i => new TileKey(0, i, 0)));
        Assert.Equal(101, loader.Pending);
        TileKey[] latest = [new(1, 0, 0), new(1, 1, 0)];
        loader.Request(latest);
        Assert.Equal(3, loader.Generation);
        Assert.Equal(3, loader.Pending);

        reader.Release();
        Wait.Until(() => loaded.Count == 3, "the reads finish");
        loader.Dispose(); // no read starts after this

        Assert.Equal([first, latest[0], latest[1]], reader.Reads);
        // The read that had started still completes into the cache.
        Assert.True(cache.TryGet(first, out _));
    }

    [Fact]
    public void Loader_DoesNotDuplicateInFlight()
    {
        var reader = new FakeReader(blocked: true);
        using var cache = new TileCache(1 << 20);
        var loaded = new ConcurrentQueue<TileKey>();
        using var loader = new TileLoader(reader.Read, cache, 2, loaded.Enqueue); // an idle worker could take a duplicate
        var key = new TileKey(0, 0, 0);

        loader.Request([key, key]);
        Assert.True(reader.Entered.Wait(Timeout, Token), "the read did not start");
        loader.Request([key]);
        loader.Request([key, key]);
        Assert.Equal(1, loader.Pending); // only the read in flight

        reader.Release();
        Wait.Until(() => loaded.Count == 1 && loader.Pending == 0, "the read finishes");

        // Once cached, it is not read again either.
        loader.Request([key]);
        Assert.Equal(0, loader.Pending);
        Assert.Equal([key], reader.Reads);
        Assert.Equal([key], loaded);
    }

    [Fact]
    public async Task Loader_DisposeWhileLoading_CompletesQuietly()
    {
        var reader = new FakeReader(blocked: true);
        using var cache = new TileCache(1 << 20);
        int loaded = 0, failed = 0;
        var loader = new TileLoader(reader.Read, cache, 2, _ => Interlocked.Increment(ref loaded));
        loader.Failed += _ => Interlocked.Increment(ref failed);
        TileKey[] keys = [new(0, 0, 0), new(0, 1, 0), new(0, 2, 0)];
        loader.Request(keys);
        Assert.True(reader.Entered.Wait(Timeout, Token) && reader.Entered.Wait(Timeout, Token), "the reads did not start");

        Task dispose = Task.Run(loader.Dispose, Token);
        await Task.WhenAny(dispose, Task.Delay(200, Token));
        Assert.False(dispose.IsCompleted, "Dispose returned while reads were still running");
        reader.Release();
        await dispose.WaitAsync(Timeout, Token); // throws if it does not return, or if it threw

        loader.Request([new TileKey(1, 0, 0)]); // ignored
        loader.Dispose();
        Assert.Equal(0, Volatile.Read(ref loaded));
        Assert.Equal(0, Volatile.Read(ref failed));
        Assert.Equal(2, reader.Reads.Count); // the queued third tile never started
        Assert.Equal(0, loader.Pending);
        // What the reads returned after Dispose is dropped, not cached.
        Assert.Equal(0, cache.Count);
        Assert.All(reader.Returned, tile => Assert.True(tile.IsDisposed));
    }

    [Fact]
    public void Loader_DisposeTimesOut_NoCallbacksAfterward()
    {
        var reader = new FakeReader(blocked: true);
        using var cache = new TileCache(1 << 20);
        int loaded = 0, failed = 0;
        var loader = new TileLoader(reader.Read, cache, 1, _ => Interlocked.Increment(ref loaded))
        {
            DisposeTimeout = TimeSpan.FromMilliseconds(100),
        };
        loader.Failed += _ => Interlocked.Increment(ref failed);
        loader.Request([new TileKey(0, 0, 0)]);
        Assert.True(reader.Entered.Wait(Timeout, Token), "the read did not start");

        loader.Dispose(); // gives up waiting on the blocked read

        reader.Release();
        Wait.Until(() => loader.Pending == 0, "the abandoned read finishes");
        Assert.Equal(0, Volatile.Read(ref loaded));
        Assert.Equal(0, Volatile.Read(ref failed));
        Assert.Equal(0, cache.Count);
        Assert.All(reader.Returned, tile => Assert.True(tile.IsDisposed));
    }

    [Fact]
    public void Loader_FailureStopsFurtherRequests()
    {
        var error = new OpenSlideException("boom");
        var reader = new FakeReader(blocked: true) { Fail = _ => error };
        using var cache = new TileCache(1 << 20);
        var loaded = new ConcurrentQueue<TileKey>();
        var failures = new ConcurrentQueue<Exception>();
        using var loader = new TileLoader(reader.Read, cache, 2, loaded.Enqueue);
        loader.Failed += failures.Enqueue;
        TileKey a = new(0, 0, 0), b = new(0, 1, 0), queued = new(0, 2, 0);

        loader.Request([a, b, queued]);
        Assert.True(reader.Entered.Wait(Timeout, Token) && reader.Entered.Wait(Timeout, Token), "the reads did not start");
        reader.Release(); // both reads fail
        Wait.Until(() => !failures.IsEmpty && loader.Pending == 0, "the failures are handled");

        Assert.Same(error, Assert.Single(failures)); // reported once
        long generation = loader.Generation;
        loader.Request([new TileKey(1, 0, 0)]);
        Assert.Equal(0, loader.Pending);
        Assert.Equal(generation, loader.Generation);
        Assert.Equal(new[] { a, b }.ToHashSet(), reader.Reads.ToHashSet()); // the queued tile was dropped
        Assert.Empty(loaded);
        Assert.Single(failures);
    }

    /// <summary>A read function whose calls can be held at a gate, counted and made to fail.</summary>
    private sealed class FakeReader
    {
        private readonly ManualResetEventSlim _gate;

        public FakeReader(bool blocked = false) => _gate = new ManualResetEventSlim(!blocked);

        public ConcurrentQueue<TileKey> Reads { get; } = new();

        public ConcurrentQueue<TileImage> Returned { get; } = new();

        /// <summary>Released once each time a read starts.</summary>
        public SemaphoreSlim Entered { get; } = new(0);

        public Func<TileKey, Exception?>? Fail { get; init; }

        public void Release() => _gate.Set();

        public TileImage Read(TileKey key)
        {
            Reads.Enqueue(key);
            Entered.Release();
            if (!_gate.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("The test never released the read.");
            }

            if (Fail?.Invoke(key) is { } error)
            {
                throw error;
            }

            var tile = new TileImage(4, 4, new byte[4 * 4 * 4]);
            Returned.Enqueue(tile);
            return tile;
        }
    }
}
