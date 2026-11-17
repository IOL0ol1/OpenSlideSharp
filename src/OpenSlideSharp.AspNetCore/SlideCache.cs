using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenSlideSharp.AspNetCore.Internal;

namespace OpenSlideSharp.AspNetCore;

/// <summary>
/// Keeps up to <see cref="OpenSlideServerOptions.SlideCacheSize"/> slides open, least recently used first out,
/// and hands them out as <see cref="SlideLease"/>s. <c>AddOpenSlide</c> registers one as a singleton, shared by the
/// Deep Zoom and OME-Zarr endpoints.
/// </summary>
/// <remarks>
/// <para>
/// Slides are keyed by <see cref="Path.GetFullPath(string)"/>, ignoring case on Windows. When several requests
/// ask for a slide that is not open yet, it is opened once and they all wait for it. A slide that cannot be opened
/// is not remembered, so the next request tries again.
/// </para>
/// <para>
/// A slide leaves the cache when it is the least recently used one and room is needed, or through
/// <see cref="Evict"/>. It is closed once it has left the cache and every lease on it has been disposed, so
/// requests still using it are not disturbed. All members are thread-safe.
/// </para>
/// </remarks>
public sealed class SlideCache : IDisposable
{
    private static readonly StringComparer KeyComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly OpenSlideServerOptions _options;
    private readonly ILogger<SlideCache> _logger;
    private readonly int _capacity;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, LinkedListNode<CacheItem>> _items = new(KeyComparer);
    private readonly LinkedList<CacheItem> _recency = new(); // most recently used first
    private bool _disposed;
    private int _openCount;

    /// <summary>Creates a slide cache.</summary>
    /// <param name="options">The server options; the cache uses them as they are when it is created.</param>
    /// <param name="logger">Receives a debug message for each slide that cannot be opened, and color warnings.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="logger"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><see cref="OpenSlideServerOptions.SlideCacheSize"/> is less than 1.</exception>
    /// <exception cref="OptionsValidationException">The options are not valid.</exception>
    public SlideCache(IOptions<OpenSlideServerOptions> options, ILogger<SlideCache> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _logger = logger;
        _capacity = _options.SlideCacheSize;
        if (_capacity < 1)
        {
            throw new ArgumentException("SlideCacheSize must be at least 1.", nameof(options));
        }
    }

    /// <summary>Gets the number of slides in the cache, including ones still being opened.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>Gets how many times <see cref="OpenSlide.Open"/> has been called. For tests.</summary>
    internal int OpenCount => Volatile.Read(ref _openCount);

    /// <summary>Leases a slide, opening it if it is not in the cache.</summary>
    /// <param name="filePath">Path of the slide file.</param>
    /// <returns>
    /// A lease on the open slide, which the caller must dispose; or <see langword="null"/> if the file does not
    /// exist, is not a slide OpenSlide recognizes, or cannot be opened (a debug message says why).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The cache has been disposed.</exception>
    /// <exception cref="DllNotFoundException">The native OpenSlide library could not be loaded.</exception>
    public SlideLease? TryAcquire(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        if (!TryGetKey(filePath, out string? key, out Exception? error))
        {
            Log.CannotOpenSlide(_logger, filePath, error);
            return null;
        }

        CacheItem item;
        List<CacheItem>? evicted = null;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_items.TryGetValue(key, out LinkedListNode<CacheItem>? node))
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
                item = node.Value;
            }
            else
            {
                item = new CacheItem(this, key);
                _items.Add(key, _recency.AddFirst(item));
                while (_items.Count > _capacity)
                {
                    CacheItem oldest = _recency.Last!.Value;
                    _recency.RemoveLast();
                    _items.Remove(oldest.Key);
                    (evicted ??= []).Add(oldest);
                }
            }

            // While the item is cached the cache holds a reference, so the count is above zero here.
            item.AddRef();
        }

        // Closing slides takes native calls; do it outside the lock.
        if (evicted is not null)
        {
            foreach (CacheItem oldest in evicted)
            {
                oldest.Release();
            }
        }

        SlideEntry? entry;
        try
        {
            entry = item.GetEntry();
        }
        catch
        {
            // Lazy<T> would rethrow this to every later request; drop the item so the next one tries again.
            Remove(item);
            item.Release();
            throw;
        }

        if (entry is null)
        {
            Remove(item);
            item.Release();
            return null;
        }

        return new SlideLease(item, entry);
    }

    /// <summary>
    /// Removes a slide from the cache, for example after it entered the OpenSlide error state, so the next
    /// <see cref="TryAcquire"/> opens it again. Leases already held keep working with the old instance, which is
    /// closed when the last of them is disposed. Does nothing if the slide is not in the cache.
    /// </summary>
    /// <param name="filePath">Path of the slide file.</param>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> is <see langword="null"/>.</exception>
    public void Evict(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        if (!TryGetKey(filePath, out string? key, out _))
        {
            return;
        }

        CacheItem? removed = null;
        lock (_lock)
        {
            if (_items.Remove(key, out LinkedListNode<CacheItem>? node))
            {
                _recency.Remove(node);
                removed = node.Value;
            }
        }

        removed?.Release();
    }

    /// <summary>
    /// Empties the cache. Slides without a lease are closed now; leased ones when their last lease is disposed.
    /// </summary>
    public void Dispose()
    {
        List<CacheItem> items;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            items = [.. _recency];
            _items.Clear();
            _recency.Clear();
        }

        foreach (CacheItem item in items)
        {
            item.Release();
        }
    }

    private static bool TryGetKey(string filePath, [NotNullWhen(true)] out string? key, [NotNullWhen(false)] out Exception? error)
    {
        try
        {
            key = Path.GetFullPath(filePath);
            error = null;
            return true;
        }
        catch (Exception e) when (e is ArgumentException or IOException)
        {
            key = null;
            error = e;
            return false;
        }
    }

    // Removes the item if it is still the cached one for its key, and releases the cache's reference to it.
    private void Remove(CacheItem item)
    {
        lock (_lock)
        {
            if (!_items.TryGetValue(item.Key, out LinkedListNode<CacheItem>? node) || node.Value != item)
            {
                return;
            }

            _items.Remove(item.Key);
            _recency.Remove(node);
        }

        item.Release();
    }

    private SlideEntry? Open(string filePath)
    {
        Interlocked.Increment(ref _openCount);
        OpenSlide slide;
        try
        {
            slide = OpenSlide.Open(filePath);
        }
        catch (Exception e) when (e is OpenSlideException or ArgumentException)
        {
            // OpenSlideUnsupportedFormatException covers missing files; ArgumentException a NUL in the path.
            Log.CannotOpenSlide(_logger, filePath, e);
            return null;
        }

        return new SlideEntry(filePath, slide, _options, _logger);
    }

    /// <summary>
    /// A cache slot: opens its slide once, and closes it when the reference count reaches zero. The cache holds
    /// one reference while the item is cached, and each lease (or acquisition in progress) holds one more.
    /// </summary>
    internal sealed class CacheItem
    {
        private readonly Lazy<SlideEntry?> _entry;
        private int _references = 1;

        public CacheItem(SlideCache cache, string key)
        {
            Key = key;
            _entry = new Lazy<SlideEntry?>(() => cache.Open(key), LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public string Key { get; }

        public SlideEntry? GetEntry() => _entry.Value;

        public void AddRef() => Interlocked.Increment(ref _references);

        public void Release()
        {
            int references = Interlocked.Decrement(ref _references);
            Debug.Assert(references >= 0, "A cache item was released more often than referenced.");

            // Every acquisition holds a reference while it waits for the Lazy, so at zero it has finished.
            if (references == 0 && _entry.IsValueCreated)
            {
                _entry.Value?.Dispose();
            }
        }
    }
}
