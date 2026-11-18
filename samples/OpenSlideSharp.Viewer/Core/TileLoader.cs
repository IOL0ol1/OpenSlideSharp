using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace OpenSlideSharp.Viewer.Core;

/// <summary>
/// Reads tiles on background threads into a <see cref="TileCache"/>, newest request first.
/// </summary>
/// <remarks>
/// <para>
/// Each <see cref="Request"/> starts a new generation: it drops the tiles of earlier requests that have not started
/// and queues its own in the order given (the caller puts the centre of the view first). Tiles already cached, in
/// flight or queued are skipped. Reads that have started always finish and go into the cache.
/// </para>
/// <para>
/// A worker reads a tile, adds it to the cache, then calls <c>loaded</c> on its own thread; the UI layer switches
/// to the UI thread itself. The first exception from a read raises <see cref="Failed"/> once, on a worker thread,
/// and the loader then ignores further requests.
/// </para>
/// <para>
/// An exception thrown by <c>loaded</c> or by a <see cref="Failed"/> handler is a bug in the caller, not a read
/// failure: it is traced and swallowed, so it neither ends the process from a worker thread nor stops the loader,
/// and the other <see cref="Failed"/> handlers still run.
/// </para>
/// <para>
/// <see cref="Dispose"/> stops taking requests, drops the queued ones and waits for the reads in flight, for at most
/// <see cref="DisposeTimeout"/>. Once it returns with no read in flight no callback starts, and tiles read after it
/// are discarded. A read still in flight past the timeout (0 waits for none) finishes on its own: its tile is
/// discarded, or added to the cache if it beat <see cref="Dispose"/> by a hair (a disposed cache disposes it), and in
/// that case <c>loaded</c> may still run once; a failure is not reported.
/// </para>
/// </remarks>
internal sealed class TileLoader : IDisposable
{
    private readonly Func<TileKey, TileImage> _read;
    private readonly TileCache _cache;
    private readonly Action<TileKey> _loaded;
    private readonly Thread[] _workers;
    private readonly object _lock = new();
    private readonly Queue<TileKey> _queue = new();
    private readonly HashSet<TileKey> _queued = new();
    private readonly HashSet<TileKey> _inFlight = new();
    private long _generation;
    private bool _failed;
    private bool _disposed;

    public TileLoader(Func<TileKey, TileImage> read, TileCache cache, int workers, Action<TileKey> loaded)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        _read = read;
        _cache = cache;
        _loaded = loaded;
        _workers = new Thread[workers];
        for (int i = 0; i < workers; i++)
        {
            _workers[i] = new Thread(Work) { IsBackground = true, Name = $"Tile loader {i + 1}" };
        }

        foreach (Thread worker in _workers)
        {
            worker.Start();
        }
    }

    /// <summary>Raised once, on a worker thread, when a read first fails.</summary>
    public event Action<Exception>? Failed;

    /// <summary>Gets the number of requests made so far.</summary>
    public long Generation
    {
        get
        {
            lock (_lock)
            {
                return _generation;
            }
        }
    }

    /// <summary>Gets the number of tiles queued or being read.</summary>
    public int Pending
    {
        get
        {
            lock (_lock)
            {
                return _queue.Count + _inFlight.Count;
            }
        }
    }

    /// <summary>Gets whether <see cref="Dispose"/> has been called.</summary>
    public bool IsDisposed
    {
        get
        {
            lock (_lock)
            {
                return _disposed;
            }
        }
    }

    /// <summary>Gets or sets how long <see cref="Dispose"/> waits for the reads in flight.</summary>
    internal TimeSpan DisposeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Replaces the queued tiles with <paramref name="keys"/>, to be read in that order.</summary>
    public void Request(IEnumerable<TileKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        TileKey[] requested = keys.ToArray(); // the caller's enumeration runs outside the lock

        lock (_lock)
        {
            if (_disposed || _failed)
            {
                return;
            }

            _generation++;
            _queue.Clear();
            _queued.Clear();
            foreach (TileKey key in requested)
            {
                if (!_inFlight.Contains(key) && !_queued.Contains(key) && !_cache.Contains(key))
                {
                    _queue.Enqueue(key);
                    _queued.Add(key);
                }
            }

            Monitor.PulseAll(_lock);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _queue.Clear();
            _queued.Clear();
            Monitor.PulseAll(_lock);

            // Called from a callback, the worker's own tile is in flight until Dispose returns.
            int own = Array.IndexOf(_workers, Thread.CurrentThread) >= 0 ? 1 : 0;
            long deadline = Environment.TickCount64 + (long)DisposeTimeout.TotalMilliseconds;
            while (_inFlight.Count > own)
            {
                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    break;
                }

                Monitor.Wait(_lock, TimeSpan.FromMilliseconds(remaining));
            }
        }
    }

    private void Work()
    {
        while (true)
        {
            TileKey key;
            lock (_lock)
            {
                while (_queue.Count == 0 && !_disposed)
                {
                    Monitor.Wait(_lock);
                }

                if (_disposed)
                {
                    return;
                }

                key = _queue.Dequeue();
                _queued.Remove(key);
                _inFlight.Add(key);
            }

            try
            {
                Load(key);
            }
            finally
            {
                lock (_lock)
                {
                    _inFlight.Remove(key);
                    Monitor.PulseAll(_lock); // Dispose may be waiting
                }
            }
        }
    }

    private void Load(TileKey key)
    {
        try
        {
            TileImage tile = _read(key);
            if (IsDisposed)
            {
                tile.Dispose();
                return;
            }

            _cache.Add(key, tile);
        }
        catch (Exception e)
        {
            Fail(e);
            return;
        }

        if (!IsDisposed)
        {
            Notify(_loaded, key, "loaded");
        }
    }

    // Runs a callback on a worker thread: what it throws must not end the process or pass for a read failure.
    private static void Notify<T>(Action<T> callback, T argument, string name)
    {
        try
        {
            callback(argument);
        }
        catch (Exception e)
        {
            Trace.TraceError($"TileLoader: the {name} callback threw: {e}");
        }
    }

    private void Fail(Exception e)
    {
        Action<Exception>? failed;
        lock (_lock)
        {
            if (_disposed || _failed)
            {
                return;
            }

            _failed = true;
            _queue.Clear();
            _queued.Clear();
            failed = Failed;
        }

        if (failed is null)
        {
            return;
        }

        // Each handler on its own, so that one that throws does not keep the others from running.
        foreach (Action<Exception> handler in failed.GetInvocationList())
        {
            Notify(handler, e, nameof(Failed));
        }
    }
}
