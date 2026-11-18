using System;
using System.Threading;

namespace OpenSlideSharp.Viewer.Core;

/// <summary>
/// The pixels of one tile as OpenSlide returns them: premultiplied BGRA, rows top to bottom with no padding. It is
/// platform independent; the UI layer turns it into a bitmap the first time it draws it.
/// </summary>
/// <remarks>
/// <see cref="Dispose"/> marks the tile as dropped from the <see cref="TileCache"/>, so whatever the UI built from it
/// can be released. <see cref="Pixels"/> stays readable afterwards: the cache may evict a tile on a loader thread
/// while the UI thread is still drawing it.
/// </remarks>
internal sealed class TileImage : IDisposable
{
    private int _disposed;

    public TileImage(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != (long)width * height * 4)
        {
            throw new ArgumentException($"A {width} x {height} tile needs {(long)width * height * 4} bytes, not {pixels.Length}.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    /// <summary>Gets the size the tile counts for in the cache budget: <c>width * height * 4</c>.</summary>
    public long SizeBytes => Pixels.LongLength;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
