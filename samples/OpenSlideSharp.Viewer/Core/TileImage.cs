using System;
using System.Threading;

namespace OpenSlideSharp.Viewer.Core;

/// <summary>
/// The pixels of one tile as OpenSlide returns them: premultiplied BGRA, rows top to bottom with no padding. It is
/// platform independent; the UI layer turns it into a bitmap the first time it draws it.
/// </summary>
/// <remarks>
/// <para>
/// The tile owns its pixel buffer, which may come from a pool, until one of two things happens, whichever is first:
/// </para>
/// <list type="bullet">
/// <item><see cref="ConsumePixels{T}"/> hands the buffer to the UI layer, which copies it into a bitmap; or</item>
/// <item><see cref="Dispose"/> marks the tile as dropped from the <see cref="TileCache"/>.</item>
/// </list>
/// <para>
/// Either way the buffer then goes back through the <c>release</c> callback, exactly once, and the tile no longer
/// has pixels: a drawn tile lives on as its bitmap, which takes the pixels' place in the cache budget. The hand-over
/// is atomic, so the cache may evict a tile on a loader thread while the UI thread is copying it: the UI thread
/// still finishes its copy, and the release comes after it.
/// </para>
/// </remarks>
internal sealed class TileImage : IDisposable
{
    private readonly Action<byte[]>? _release;
    private byte[]? _pixels;
    private int _disposed;

    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="pixels">
    /// The pixels in its first <c>width * height * 4</c> bytes; a pooled array may be longer. The tile owns it from
    /// now on.
    /// </param>
    /// <param name="release">
    /// Called once with <paramref name="pixels"/> when the tile is done with it, for example to return it to its
    /// pool; null leaves it to the garbage collector.
    /// </param>
    public TileImage(int width, int height, byte[] pixels, Action<byte[]>? release = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        ArgumentNullException.ThrowIfNull(pixels);
        long size = (long)width * height * 4;
        if (pixels.Length < size)
        {
            throw new ArgumentException($"A {width} x {height} tile needs {size} bytes, not {pixels.Length}.", nameof(pixels));
        }

        Width = width;
        Height = height;
        SizeBytes = size;
        _pixels = pixels;
        _release = release;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Gets the size the tile counts for in the cache budget: <c>width * height * 4</c>.</summary>
    public long SizeBytes { get; }

    /// <summary>Gets whether the tile still holds its pixels: neither consumed nor disposed.</summary>
    public bool HasPixels => Volatile.Read(ref _pixels) is not null;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Hands the pixel buffer to <paramref name="consume"/>, then releases it. Only the first call gets the pixels,
    /// and only if the tile has not been disposed; <paramref name="consume"/> must not keep the array.
    /// </summary>
    /// <returns>What <paramref name="consume"/> returned, or null if the pixels were already gone.</returns>
    public T? ConsumePixels<T>(Func<byte[], T> consume)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(consume);
        byte[]? pixels = Interlocked.Exchange(ref _pixels, null);
        if (pixels is null)
        {
            return null;
        }

        try
        {
            return consume(pixels);
        }
        finally
        {
            _release?.Invoke(pixels);
        }
    }

    /// <summary>Marks the tile as dropped from the cache and releases its pixels unless they were consumed.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && Interlocked.Exchange(ref _pixels, null) is { } pixels)
        {
            _release?.Invoke(pixels);
        }
    }
}
