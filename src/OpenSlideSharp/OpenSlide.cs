using System;
using System.Collections.Generic;
using System.Threading;
using OpenSlideSharp.Native;

namespace OpenSlideSharp;

/// <summary>An open whole-slide image, backed by an OpenSlide <c>openslide_t</c> handle.</summary>
/// <remarks>
/// <para>
/// A slide may be used from several threads at once. Disposing it while other threads are still using it is safe:
/// native calls already in progress finish first, and calls that start after <see cref="Dispose"/> throw
/// <see cref="ObjectDisposedException"/>.
/// </para>
/// <para>
/// OpenSlide errors are sticky: after the slide reports an error, every member except <see cref="FileName"/>,
/// <see cref="ToString"/> and <see cref="Dispose"/> throws <see cref="OpenSlideException"/>.
/// </para>
/// </remarks>
public sealed class OpenSlide : IDisposable
{
    private readonly OpenSlideHandle _handle;
    private readonly SlideSize _dimensions;
    private readonly IReadOnlyList<SlideSize> _levelDimensions;
    private readonly IReadOnlyList<double> _levelDownsamples;
    private readonly PropertyMap _properties;
    private readonly AssociatedImageMap _associatedImages;
    private int _disposed;
    private byte[]? _colorProfile; // null = not read yet; NoProfile = read, slide has none

    private static readonly byte[] NoProfile = new byte[0];

    private unsafe OpenSlide(string fileName, OpenSlideHandle handle)
    {
        FileName = fileName;
        _handle = handle;
        _properties = new PropertyMap(this);
        _associatedImages = new AssociatedImageMap(this);

        // Level metadata never changes, so read it once. The public accessors still run the
        // disposed / sticky-error check on every access, like openslide-python's per-access queries.
        int count = Invoke(static osr => openslide.get_level_count(osr));
        var dimensions = new SlideSize[count];
        var downsamples = new double[count];
        using (HandleLease lease = AcquireHandle())
        {
            long width, height;
            openslide.get_level0_dimensions(lease.Handle, &width, &height);
            _dimensions = new SlideSize(width, height);
            for (int level = 0; level < count; level++)
            {
                openslide.get_level_dimensions(lease.Handle, level, &width, &height);
                dimensions[level] = new SlideSize(width, height);
                downsamples[level] = openslide.get_level_downsample(lease.Handle, level);
            }
        }

        _levelDimensions = Array.AsReadOnly(dimensions);
        _levelDownsamples = Array.AsReadOnly(downsamples);
    }

    /// <summary>Gets the path the slide was opened with, exactly as passed to <see cref="Open"/>.</summary>
    public string FileName { get; }

    /// <summary>Gets the number of levels in the slide.</summary>
    /// <exception cref="ObjectDisposedException">The slide has been disposed.</exception>
    /// <exception cref="OpenSlideException">The slide is in the error state.</exception>
    public int LevelCount
    {
        get
        {
            ThrowIfUnusable();
            return _levelDimensions.Count;
        }
    }

    /// <summary>Gets the size of level 0, the highest-resolution level.</summary>
    /// <exception cref="ObjectDisposedException">The slide has been disposed.</exception>
    /// <exception cref="OpenSlideException">The slide is in the error state.</exception>
    public SlideSize Dimensions
    {
        get
        {
            ThrowIfUnusable();
            return _dimensions;
        }
    }

    /// <summary>Gets the size of each level, indexed by level number.</summary>
    /// <exception cref="ObjectDisposedException">The slide has been disposed.</exception>
    /// <exception cref="OpenSlideException">The slide is in the error state.</exception>
    public IReadOnlyList<SlideSize> LevelDimensions
    {
        get
        {
            ThrowIfUnusable();
            return _levelDimensions;
        }
    }

    /// <summary>Gets the downsample factor of each level relative to level 0, indexed by level number.</summary>
    /// <exception cref="ObjectDisposedException">The slide has been disposed.</exception>
    /// <exception cref="OpenSlideException">The slide is in the error state.</exception>
    public IReadOnlyList<double> LevelDownsamples
    {
        get
        {
            ThrowIfUnusable();
            return _levelDownsamples;
        }
    }

    /// <summary>
    /// Gets the slide's properties as a live, read-only dictionary: each lookup or enumeration queries OpenSlide.
    /// A missing key makes the indexer throw <see cref="KeyNotFoundException"/> and <c>TryGetValue</c> return
    /// <see langword="false"/>. A key containing a NUL character (<c>'\0'</c>) cannot be passed to OpenSlide, so
    /// looking it up throws <see cref="ArgumentException"/>. Any access throws <see cref="ObjectDisposedException"/>
    /// after the slide is disposed, or <see cref="OpenSlideException"/> if it is in the error state. See
    /// <see cref="OpenSlideProperties"/> for the standard keys.
    /// </summary>
    public IReadOnlyDictionary<string, string> Properties => _properties;

    /// <summary>
    /// Returns the format (vendor) name OpenSlide detects for a file, without opening it
    /// (<c>openslide_detect_vendor</c>).
    /// </summary>
    /// <param name="filename">Path of the file to examine.</param>
    /// <returns>The format name, for example <c>"aperio"</c>, or <see langword="null"/> if the file is missing or not a recognized slide.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="filename"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="filename"/> contains a NUL character (<c>'\0'</c>).</exception>
    /// <exception cref="DllNotFoundException">The native OpenSlide library could not be loaded.</exception>
    public static unsafe string? DetectFormat(string filename)
    {
        if (filename is null)
        {
            throw new ArgumentNullException(nameof(filename));
        }

        byte[] path = Utf8.ToNullTerminated(filename, nameof(filename));
        return NativeLibraryLoader.ThrowHelpfulIfMissing(() =>
        {
            fixed (byte* p = path)
            {
                return Utf8.FromNative(openslide.detect_vendor(p));
            }
        });
    }

    /// <summary>Opens a whole-slide image (<c>openslide_open</c>).</summary>
    /// <param name="filename">Path of the slide file. Any Unicode path is supported; it is passed to OpenSlide as UTF-8.</param>
    /// <returns>The open slide. Dispose it to close the file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="filename"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="filename"/> contains a NUL character (<c>'\0'</c>).</exception>
    /// <exception cref="OpenSlideUnsupportedFormatException">The file is missing or not a recognized slide format.</exception>
    /// <exception cref="OpenSlideException">The format was recognized but OpenSlide could not open the file.</exception>
    /// <exception cref="DllNotFoundException">The native OpenSlide library could not be loaded.</exception>
    public static unsafe OpenSlide Open(string filename)
    {
        if (filename is null)
        {
            throw new ArgumentNullException(nameof(filename));
        }

        byte[] path = Utf8.ToNullTerminated(filename, nameof(filename));
        IntPtr osr = NativeLibraryLoader.ThrowHelpfulIfMissing(() =>
        {
            fixed (byte* p = path)
            {
                return openslide.open(p);
            }
        });

        if (osr == IntPtr.Zero)
        {
            throw new OpenSlideUnsupportedFormatException("Unsupported or missing image file: " + filename);
        }

        var handle = new OpenSlideHandle(osr);
        try
        {
            // A non-NULL handle in the error state means "recognized but broken". ThrowIfError copies the
            // error text before the catch below closes the handle, and nothing escapes without closing it.
            OpenSlideHandle.ThrowIfError(osr);
            return new OpenSlide(filename, handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Returns the index of the level best suited to displaying the slide at <paramref name="downsample"/>
    /// (<c>openslide_get_best_level_for_downsample</c>).
    /// </summary>
    /// <param name="downsample">The desired downsample factor relative to level 0.</param>
    /// <returns>The level index.</returns>
    /// <exception cref="ObjectDisposedException">The slide has been disposed.</exception>
    /// <exception cref="OpenSlideException">The slide is in the error state.</exception>
    public int GetBestLevelForDownsample(double downsample)
    {
        using HandleLease lease = AcquireHandle();
        return openslide.get_best_level_for_downsample(lease.Handle, downsample);
    }

    /// <summary>
    /// Gets the slide's associated images (thumbnail, label, macro and so on) as a live, read-only dictionary keyed
    /// by name. The set of names is read once and cached, since it cannot change after the slide is opened; every
    /// lookup or enumeration still throws <see cref="ObjectDisposedException"/> after the slide is disposed, or
    /// <see cref="OpenSlideException"/> if it is in the error state. A missing key makes the indexer throw
    /// <see cref="KeyNotFoundException"/>.
    /// </summary>
    public IReadOnlyDictionary<string, AssociatedImage> AssociatedImages => _associatedImages;

    /// <summary>
    /// Gets the slide embedded ICC color profile (<c>openslide_read_icc_profile</c>), or an empty value
    /// if it has none. The profile is read once and cached, so every access returns the same array instance:
    /// callers must not modify it.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The slide has been disposed.</exception>
    /// <exception cref="OpenSlideException">The slide is in the error state.</exception>
    public unsafe ReadOnlyMemory<byte> ColorProfile
    {
        get
        {
            ThrowIfUnusable();
            byte[]? cached = Volatile.Read(ref _colorProfile);
            if (cached is null)
            {
                byte[] profile;
                using (HandleLease lease = AcquireHandle())
                {
                    long size = openslide.get_icc_profile_size(lease.Handle);
                    if (size <= 0)
                    {
                        // A negative size means error; the lease post-call check throws it.
                        profile = NoProfile;
                    }
                    else
                    {
                        profile = new byte[size];
                        fixed (byte* p = profile)
                        {
                            openslide.read_icc_profile(lease.Handle, (IntPtr)p);
                        }
                    }
                }

                // Concurrent first readers may both read; all of them end up returning the winner instance.
                cached = Interlocked.CompareExchange(ref _colorProfile, profile, null) ?? profile;
            }

            return ReferenceEquals(cached, NoProfile) ? default : new ReadOnlyMemory<byte>(cached);
        }
    }

    /// <summary>
    /// Reads a region of the slide into a new <see cref="PixelImage"/> in <see cref="PixelFormat.Bgra32Premultiplied"/>
    /// (<c>openslide_read_region</c>). Areas outside the slide are transparent.
    /// </summary>
    /// <param name="x">X of the top-left corner, in level 0 coordinates.</param>
    /// <param name="y">Y of the top-left corner, in level 0 coordinates.</param>
    /// <param name="level">The level to read from. A level that does not exist yields a transparent image, like openslide-python.</param>
    /// <param name="width">Width in pixels at <paramref name="level"/>.</param>
    /// <param name="height">Height in pixels at <paramref name="level"/>.</param>
    /// <returns>The image; its <see cref="PixelImage.IccProfile"/> is <see cref="ColorProfile"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is negative (a deliberate divergence from openslide-python, which passes it to OpenSlide), or the pixel data would not fit in a .NET array.</exception>
    /// <exception cref="ObjectDisposedException">The slide has been disposed.</exception>
    /// <exception cref="OpenSlideException">The slide is in the error state, or reading failed.</exception>
    public unsafe PixelImage ReadRegion(long x, long y, int level, int width, int height)
    {
        long length = CheckedPixelBytes(width, height, "region");
        var data = new byte[length];
        fixed (byte* p = data)
        {
            ReadRegionCore(x, y, level, width, height, p);
        }

        return new PixelImage(width, height, PixelFormat.Bgra32Premultiplied, data, ColorProfile);
    }

    /// <summary>
    /// Reads a region of the slide into <paramref name="destination"/> as premultiplied BGRA bytes
    /// (<c>openslide_read_region</c>). Areas outside the slide are transparent.
    /// </summary>
    /// <param name="x">X of the top-left corner, in level 0 coordinates.</param>
    /// <param name="y">Y of the top-left corner, in level 0 coordinates.</param>
    /// <param name="level">The level to read from. A level that does not exist yields transparent pixels, like openslide-python.</param>
    /// <param name="width">Width in pixels at <paramref name="level"/>.</param>
    /// <param name="height">Height in pixels at <paramref name="level"/>.</param>
    /// <param name="destination">Receives <c>width * height * 4</c> bytes, rows top to bottom with no padding.</param>
    /// <remarks>
    /// The alignment of <paramref name="destination"/> is not guaranteed, although OpenSlide accesses it as
    /// <c>uint32</c>: a <c>byte[]</c>, <c>stackalloc</c> memory or a pooled array all work.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <c>width * height * 4</c> bytes.</exception>
    /// <exception cref="ObjectDisposedException">The slide has been disposed.</exception>
    /// <exception cref="OpenSlideException">The slide is in the error state, or reading failed.</exception>
    public unsafe void ReadRegion(long x, long y, int level, int width, int height, Span<byte> destination)
    {
        ValidateSize(width, height);
        long length = (long)width * height * 4;
        if (destination.Length < length)
        {
            throw new ArgumentException(
                $"Destination holds {destination.Length} bytes but a {width} x {height} region needs {length}.",
                nameof(destination));
        }

        fixed (byte* p = destination)
        {
            ReadRegionCore(x, y, level, width, height, p);
        }
    }

    /// <summary>
    /// Returns an opaque thumbnail of the slide that fits in <paramref name="maxWidth"/> x
    /// <paramref name="maxHeight"/>, keeping its aspect ratio, like openslide-python's <c>get_thumbnail</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reads the whole level best suited to the requested size (<see cref="GetBestLevelForDownsample"/>),
    /// composites it over the slide background color (<see cref="OpenSlideProperties.BackgroundColor"/>, white if
    /// missing or malformed), and scales it down with Pillow's Lanczos filter to the size Pillow's
    /// <c>Image.thumbnail</c> would choose. A level that already fits is returned at its own size, never enlarged.
    /// </para>
    /// <para>
    /// The level is scaled in one Lanczos pass, without the box-reduce step Pillow adds when a side shrinks by a
    /// factor of 4 or more, so in that case pixels can differ slightly from openslide-python; sizes always match.
    /// </para>
    /// </remarks>
    /// <param name="maxWidth">Maximum width in pixels, at least 1.</param>
    /// <param name="maxHeight">Maximum height in pixels, at least 1.</param>
    /// <returns>
    /// A <see cref="PixelFormat.Bgra32Opaque"/> image whose <see cref="PixelImage.IccProfile"/> is <see cref="ColorProfile"/>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxWidth"/> or <paramref name="maxHeight"/> is less than 1, or the level to read would not fit
    /// in a .NET array.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The slide has been disposed.</exception>
    /// <exception cref="OpenSlideException">The slide is in the error state, or reading failed.</exception>
    public PixelImage GetThumbnail(int maxWidth, int maxHeight)
    {
        if (maxWidth < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxWidth), maxWidth, "The maximum width must be at least 1.");
        }

        if (maxHeight < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxHeight), maxHeight, "The maximum height must be at least 1.");
        }

        SlideSize dimensions = Dimensions;
        double downsample = Math.Max((double)dimensions.Width / maxWidth, (double)dimensions.Height / maxHeight);
        int level = GetBestLevelForDownsample(downsample);
        SlideSize levelSize = _levelDimensions[level];

        // An empty level, or one that already fits, is not scaled. Otherwise the size is at most the maximum.
        (long Width, long Height) size =
            ThumbnailSize.Compute(levelSize.Width, levelSize.Height, maxWidth, maxHeight)
            ?? (levelSize.Width, levelSize.Height);
        if (size.Width > int.MaxValue / 4 || size.Height > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxWidth), $"A {size.Width} x {size.Height} thumbnail is too large for a PixelImage.");
        }

        int width = (int)size.Width;
        int height = (int)size.Height;
        var data = new byte[CheckedPixelBytes(width, height, "thumbnail")];
        var source = new SlideRenderSource(this);
        Compositor.TryParseBackground(
            source.GetProperty(OpenSlideProperties.BackgroundColor), out byte red, out byte green, out byte blue);
        RegionRenderer.RenderRegion(
            source,
            new SourceRegion(0, 0, level, levelSize.Width, levelSize.Height),
            (red, green, blue),
            colorTransform: null,
            width,
            height,
            data);
        return new PixelImage(width, height, PixelFormat.Bgra32Opaque, data, ColorProfile);
    }

    /// <summary>
    /// Makes the slide use <paramref name="cache"/> for decoded tiles (<c>openslide_set_cache</c>). OpenSlide takes
    /// its own reference to the cache, so the cache may be disposed afterwards and the slide keeps working.
    /// </summary>
    /// <param name="cache">The cache to use, replacing the slide default one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cache"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The slide or the cache has been disposed.</exception>
    /// <exception cref="OpenSlideException">The slide is in the error state.</exception>
    public void SetCache(OpenSlideCache cache)
    {
        if (cache is null)
        {
            throw new ArgumentNullException(nameof(cache));
        }

        cache.Use(c => Invoke(osr => openslide.set_cache(osr, c)));
    }

    /// <summary>
    /// Returns <c>width * height * 4</c> after rejecting negative sizes and sizes that do not fit in a .NET array.
    /// Computed in <see langword="long"/>, so large associated image dimensions cannot overflow.
    /// </summary>
    internal static long CheckedPixelBytes(long width, long height, string what)
    {
        if (width < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width must not be negative.");
        }

        if (height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "Height must not be negative.");
        }

#if NET
        long maxLength = Array.MaxLength;
#else
        const long maxLength = 0x7FFFFFC7;
#endif
        // Both factors are non-negative; dividing avoids overflowing the product.
        if (width != 0 && height > maxLength / 4 / width)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width), $"A {width} x {height} {what} needs more bytes than a .NET array can hold.");
        }

        return width * height * 4;
    }

    private static void ValidateSize(int width, int height)
    {
        if (width < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width must not be negative.");
        }

        if (height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "Height must not be negative.");
        }
    }

    private unsafe void ReadRegionCore(long x, long y, int level, int width, int height, byte* dest)
    {
        // OpenSlide never writes through dest for an empty region, but pass a valid pointer anyway.
        uint scratch;
        uint* target = dest == null ? &scratch : (uint*)dest;
        using HandleLease lease = AcquireHandle();
        openslide.read_region(lease.Handle, target, x, y, level, width, height);
    }

    /// <summary>Returns <c>OpenSlide(</c><see cref="FileName"/><c>)</c>.</summary>
    /// <returns>A string that identifies the slide.</returns>
    public override string ToString() => $"OpenSlide({FileName})";

    /// <summary>
    /// Closes the slide. Safe to call more than once, and never throws, even if the slide is in the error state.
    /// If other threads are inside a native call on this slide, the handle is closed when the last of them finishes.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _handle.Dispose();
        }
    }

    /// <summary>
    /// Runs <paramref name="call"/> with the native handle, under the same rules as <see cref="AcquireHandle"/>. Any <c>byte*</c> that OpenSlide returns points into memory the slide owns, so copy it into managed data inside the call and never return the pointer.
    /// </summary>
    internal T Invoke<T>(Func<IntPtr, T> call)
    {
        using HandleLease lease = AcquireHandle();
        return call(lease.Handle);
    }

    /// <summary>
    /// Runs <paramref name="call"/> with the native handle, under the same rules as <see cref="AcquireHandle"/>.
    /// </summary>
    internal void Invoke(Action<IntPtr> call)
    {
        using HandleLease lease = AcquireHandle();
        call(lease.Handle);
    }

    /// <summary>
    /// Leases the native handle for direct calls (for example with pointers or spans, which a lambda cannot capture).
    /// Acquiring takes a <see cref="System.Runtime.InteropServices.SafeHandle"/> reference and checks the sticky
    /// error state; disposing the lease checks the error state again and then releases the reference, even if
    /// that check throws. Any <c>byte*</c> that OpenSlide returns points into memory the slide owns, so copy it into managed data inside the call and never return the pointer. Every native call on an open slide goes through here, directly or via
    /// <see cref="Invoke{T}(Func{IntPtr, T})"/>.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The slide has been disposed.</exception>
    /// <exception cref="OpenSlideException">The slide is in the error state.</exception>
    internal HandleLease AcquireHandle()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(OpenSlide));
        }

        try
        {
            return _handle.Acquire();
        }
        catch (ObjectDisposedException)
        {
            // Dispose ran between the check above and SafeHandle.DangerousAddRef.
            throw new ObjectDisposedException(nameof(OpenSlide));
        }
    }

    internal void ThrowIfUnusable()
    {
        using (AcquireHandle())
        {
        }
    }
}
