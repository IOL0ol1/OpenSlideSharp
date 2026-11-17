using System;
using System.Threading;
using OpenSlideSharp.Native;

namespace OpenSlideSharp;

/// <summary>
/// An image stored in a slide next to the main pyramid, such as its thumbnail, label or macro image. Get one from
/// <see cref="OpenSlide.AssociatedImages"/>. It belongs to its slide, so every member throws
/// <see cref="ObjectDisposedException"/> after the slide is disposed and <see cref="OpenSlideException"/> if the
/// slide is in the error state, except <see cref="Name"/>.
/// </summary>
public sealed class AssociatedImage
{
    private static readonly byte[] NoProfile = new byte[0];

    private readonly SlideSize _dimensions;
    private byte[]? _colorProfile; // null = not read yet; NoProfile = read, image has none

    internal AssociatedImage(OpenSlide slide, string name, SlideSize dimensions)
    {
        Slide = slide;
        Name = name;
        _dimensions = dimensions;
    }

    /// <summary>Gets the name OpenSlide uses for this image, for example <c>"thumbnail"</c>.</summary>
    public string Name { get; }

    /// <summary>Gets the size of the image in pixels.</summary>
    /// <exception cref="ObjectDisposedException">The slide has been disposed.</exception>
    /// <exception cref="OpenSlideException">The slide is in the error state.</exception>
    public SlideSize Dimensions
    {
        get
        {
            Slide.ThrowIfUnusable();
            return _dimensions;
        }
    }

    internal OpenSlide Slide { get; }

    /// <summary>
    /// Gets the image embedded ICC color profile (<c>openslide_read_associated_image_icc_profile</c>), or
    /// <see langword="null"/> if it has none. The profile is read once and cached, so every access returns the same
    /// array instance: callers must not modify it.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The slide has been disposed.</exception>
    /// <exception cref="OpenSlideException">The slide is in the error state.</exception>
    public unsafe byte[]? ColorProfile
    {
        get
        {
            Slide.ThrowIfUnusable();
            byte[]? cached = Volatile.Read(ref _colorProfile);
            if (cached is null)
            {
                byte[] name = Utf8.ToNullTerminated(Name, nameof(Name));
                byte[] profile;
                using (HandleLease lease = Slide.AcquireHandle())
                {
                    long size;
                    fixed (byte* n = name)
                    {
                        size = openslide.get_associated_image_icc_profile_size(lease.Handle, n);
                    }

                    if (size <= 0)
                    {
                        // A negative size means error; the lease post-call check throws it.
                        profile = NoProfile;
                    }
                    else
                    {
                        profile = new byte[size];
                        fixed (byte* n = name)
                        fixed (byte* p = profile)
                        {
                            openslide.read_associated_image_icc_profile(lease.Handle, n, (IntPtr)p);
                        }
                    }
                }

                // Concurrent first readers may both read; all of them end up returning the winner instance.
                cached = Interlocked.CompareExchange(ref _colorProfile, profile, null) ?? profile;
            }

            return ReferenceEquals(cached, NoProfile) ? null : cached;
        }
    }

    /// <summary>
    /// Reads the image into a new <see cref="PixelImage"/> in <see cref="PixelFormat.Bgra32Premultiplied"/>
    /// (<c>openslide_read_associated_image</c>).
    /// </summary>
    /// <returns>The image; its <see cref="PixelImage.IccProfile"/> is <see cref="ColorProfile"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The pixel data would not fit in a .NET array.</exception>
    /// <exception cref="ObjectDisposedException">The slide has been disposed.</exception>
    /// <exception cref="OpenSlideException">The slide is in the error state, or reading failed.</exception>
    public unsafe PixelImage Read()
    {
        Slide.ThrowIfUnusable();
        long width = _dimensions.Width;
        long height = _dimensions.Height;
        long length = OpenSlide.CheckedPixelBytes(width, height, "associated image");
        if (width > int.MaxValue || height > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width), $"A {width} x {height} associated image is too large for a PixelImage.");
        }

        var data = new byte[length];
        byte[] name = Utf8.ToNullTerminated(Name, nameof(Name));
        // OpenSlide never writes through dest for an empty image, but pass a valid pointer anyway.
        uint scratch;
        fixed (byte* n = name)
        fixed (byte* p = data)
        {
            uint* target = length == 0 ? &scratch : (uint*)p;
            using HandleLease lease = Slide.AcquireHandle();
            openslide.read_associated_image(lease.Handle, n, target);
        }

        return new PixelImage((int)width, (int)height, PixelFormat.Bgra32Premultiplied, data, ColorProfile);
    }
}
