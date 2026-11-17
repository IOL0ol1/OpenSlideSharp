using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OpenSlideSharp.SkiaSharp.Internal;
using SkiaSharp;

namespace OpenSlideSharp.SkiaSharp;

/// <summary>Creates <see cref="IColorTransform"/> instances that convert ICC profiles to sRGB with SkiaSharp.</summary>
public static class SkiaColorTransform
{
    // One decision per slide: the core hands every tile the same backing array of the profile.
    private static readonly ConditionalWeakTable<byte[], StrongBox<IColorTransform?>> Cache = new();

    /// <summary>Creates a transform from an ICC profile to sRGB.</summary>
    /// <param name="iccProfile">The ICC profile bytes.</param>
    /// <returns>
    /// A thread-safe transform, or <see langword="null"/> if <paramref name="iccProfile"/> is empty or Skia cannot parse it.
    /// </returns>
    public static IColorTransform? Create(ReadOnlyMemory<byte> iccProfile)
    {
        if (iccProfile.IsEmpty)
        {
            return null;
        }

        return CreateCore(iccProfile.ToArray());
    }

    /// <summary>Like <see cref="Create"/>, but remembers the result per backing array of the profile.</summary>
    internal static IColorTransform? GetCached(ReadOnlyMemory<byte> iccProfile)
    {
        if (iccProfile.IsEmpty)
        {
            return null;
        }

        if (MemoryMarshal.TryGetArray(iccProfile, out ArraySegment<byte> segment)
            && segment.Array is { } array && segment.Offset == 0 && segment.Count == array.Length)
        {
            return Cache.GetValue(array, a => new StrongBox<IColorTransform?>(CreateCore(a))).Value;
        }

        return CreateCore(iccProfile.ToArray());
    }

    private static IColorTransform? CreateCore(byte[] profile)
    {
        // Never call SKColorSpace.CreateIcc(byte[]) directly: it throws on data that is not a profile.
        using SKColorSpaceIccProfile? parsed = SKColorSpaceIccProfile.Create(profile);
        if (parsed is null)
        {
            return null;
        }

        SKColorSpace? colorSpace = SKColorSpace.CreateIcc(parsed);
        if (colorSpace is not null)
        {
            return new PixmapTransform(colorSpace);
        }

        return new CodecTransform(profile);
    }

    // Level 1: Skia understands the profile as a color space (matrix/TRC).
    private sealed class PixmapTransform : IColorTransform
    {
        private readonly SKColorSpace _source;

        public PixmapTransform(SKColorSpace source) => _source = source;

        public unsafe void Apply(Span<byte> pixels, int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                return;
            }

            int stride = width * 4;
            int length = checked(stride * height);
            if (pixels.Length < length)
            {
                throw new ArgumentException("Pixel buffer is too small.", nameof(pixels));
            }

            byte[] converted = new byte[length];
            fixed (byte* src = pixels)
            fixed (byte* dst = converted)
            {
                var srcInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque, _source);
                using var srgb = SKColorSpace.CreateSrgb();
                var dstInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque, srgb);
                using var pixmap = new SKPixmap(srcInfo, (IntPtr)src, stride);
                if (!pixmap.ReadPixels(dstInfo, (IntPtr)dst, stride))
                {
                    throw new InvalidOperationException("Skia could not convert the pixels to sRGB.");
                }
            }

            converted.AsSpan().CopyTo(pixels);
        }
    }

    // Level 2: the profile is not a color space for Skia (e.g. LUT-only), but the PNG codec can still apply it.
    private sealed class CodecTransform : IColorTransform
    {
        private readonly byte[] _profile;

        public CodecTransform(byte[] profile) => _profile = profile;

        public unsafe void Apply(Span<byte> pixels, int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                return;
            }

            int stride = width * 4;
            int length = checked(stride * height);
            if (pixels.Length < length)
            {
                throw new ArgumentException("Pixel buffer is too small.", nameof(pixels));
            }

            byte[] png = PngChunks.BuildRgbPngWithProfile(pixels.Slice(0, length).ToArray(), width, height, _profile);
            using SKData data = SKData.CreateCopy(png);
            using SKCodec? codec = SKCodec.Create(data);
            if (codec is null)
            {
                throw new InvalidOperationException("Skia could not read the intermediate PNG.");
            }

            byte[] converted = new byte[length];
            using var srgb = SKColorSpace.CreateSrgb();
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque, srgb);
            SKCodecResult result;
            fixed (byte* dst = converted)
            {
                result = codec.GetPixels(info, (IntPtr)dst, stride, SKCodecOptions.Default);
            }

            if (result != SKCodecResult.Success)
            {
                throw new InvalidOperationException("Skia could not convert the pixels to sRGB: " + result + ".");
            }

            converted.AsSpan().CopyTo(pixels);
        }
    }
}
