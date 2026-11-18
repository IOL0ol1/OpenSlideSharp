using System;
using System.Threading;

namespace OpenSlideSharp.AspNetCore;

/// <summary>
/// Keeps a cached slide open while a request uses it. Get one from <see cref="SlideCache.TryAcquire"/> and dispose
/// it when done; a lease that is never disposed keeps its slide open for the life of the process.
/// </summary>
public sealed class SlideLease : IDisposable
{
    private readonly SlideEntry _entry;
    private SlideCache.CacheItem? _item;

    internal SlideLease(SlideCache.CacheItem item, SlideEntry entry)
    {
        _item = item;
        _entry = entry;
    }

    /// <summary>Gets the leased slide and its generators.</summary>
    /// <exception cref="ObjectDisposedException">The lease has been disposed.</exception>
    public SlideEntry Entry
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _item) is null, this);
            return _entry;
        }
    }

    /// <summary>The cache slot this lease holds, or <see langword="null"/> once the lease has been disposed.</summary>
    internal SlideCache.CacheItem? Item => Volatile.Read(ref _item);

    /// <summary>
    /// Returns the lease, which closes the slide if it has left the cache and this was its last lease. The lease is
    /// returned only once; disposing it again does nothing.
    /// </summary>
    public void Dispose() => Interlocked.Exchange(ref _item, null)?.Release();
}
