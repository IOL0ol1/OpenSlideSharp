using System;
using OpenSlideSharp.Native;

namespace OpenSlideSharp;

/// <summary>
/// A tile cache that can be shared by several <see cref="OpenSlide"/> instances
/// (<c>openslide_cache_create</c>) through <see cref="OpenSlide.SetCache"/>.
/// </summary>
/// <remarks>
/// OpenSlide reference-counts caches: a slide that was given the cache keeps it alive, so disposing this object
/// while slides still use the cache is safe.
/// </remarks>
public sealed class OpenSlideCache : IDisposable
{
    private readonly CacheHandle _handle;

    /// <summary>Creates a cache.</summary>
    /// <param name="capacityBytes">Capacity in bytes; 0 effectively disables caching.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacityBytes"/> is negative.</exception>
    /// <exception cref="DllNotFoundException">The native OpenSlide library could not be loaded.</exception>
    public OpenSlideCache(long capacityBytes)
    {
        if (capacityBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityBytes), capacityBytes, "Capacity must not be negative.");
        }

        // On 32-bit processes a capacity above 4 GiB cannot be represented; clamp it.
        ulong capacity = UIntPtr.Size == 4 ? Math.Min((ulong)capacityBytes, uint.MaxValue) : (ulong)capacityBytes;
        IntPtr cache = NativeLibraryLoader.ThrowHelpfulIfMissing(
            () => openslide.cache_create(new UIntPtr(capacity)));
        if (cache == IntPtr.Zero)
        {
            throw new OpenSlideException("OpenSlide could not create the cache.");
        }

        _handle = new CacheHandle(cache);
    }

    /// <summary>Releases this object's reference to the cache. Safe to call more than once.</summary>
    public void Dispose() => _handle.Dispose();

    /// <summary>Runs <paramref name="call"/> with the native handle held alive. The pointer must not escape the callback.</summary>
    /// <exception cref="ObjectDisposedException">The cache has been disposed.</exception>
    internal void Use(Action<IntPtr> call)
    {
        bool added = false;
        try
        {
            _handle.DangerousAddRef(ref added);
        }
        catch (ObjectDisposedException)
        {
            throw new ObjectDisposedException(nameof(OpenSlideCache));
        }

        try
        {
            call(_handle.DangerousGetHandle());
        }
        finally
        {
            _handle.DangerousRelease();
        }
    }
}
