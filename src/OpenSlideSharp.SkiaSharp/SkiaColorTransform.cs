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
    /// <remarks>
    /// <para>
    /// Only RGB profiles are converted: the data must hold a full 128-byte header whose data color space is
    /// <c>'RGB '</c>. Skia would accept some others, but apply them wrongly (a gray profile) or not at all (CMYK).
    /// </para>
    /// <para>
    /// Tier 1: a profile that Skia turns into a color space (matrix/TRC) is applied with <c>SKPixmap.ReadPixels</c>.
    /// </para>
    /// <para>
    /// Tier 2: a profile that Skia parses but cannot turn into a color space (a lookup-table profile) is applied
    /// by encoding the pixels as a PNG that carries the profile and decoding it to sRGB with
    /// <see cref="SKCodec"/>. Profiles whose A2B table (A2B0, or A2B1 if there is no A2B0) is a lut16
    /// (<c>'mft2'</c>) are refused: the codec reads their PCS values in the ICC v4 encodings, but lut16 keeps the
    /// v2 ones. Compared with LittleCMS, white comes out as 188 instead of 255 with an XYZ PCS, and colors are up
    /// to 7 levels off with a Lab PCS.
    /// </para>
    /// <para>Tier 3: anything else is not converted, and this method returns <see langword="null"/>.</para>
    /// </remarks>
    /// <param name="iccProfile">The ICC profile bytes.</param>
    /// <returns>
    /// A thread-safe transform, or <see langword="null"/> if <paramref name="iccProfile"/> is empty, is not an RGB
    /// profile, cannot be parsed by Skia, or is a lut16 profile that tier 2 refuses.
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
        // The pixels are RGB. Skia also accepts other profiles: it applies a gray one to RGB pixels as a diagonal
        // matrix (tier 1), and its PNG codec ignores a CMYK one (tier 2), so neither would convert correctly.
        if (!IccProfileInfo.HasRgbDataColorSpace(profile))
        {
            return null;
        }

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

        // skcms, behind the PNG codec, reads lut16 PCS values in the ICC v4 encodings, but lut16 keeps the v2 ones
        // (XYZ 0x8000 = 1.0; Lab L 0xFF00 = 100, a/b 0x8000 = 0): XYZ comes out at half the linear light, Lab up to
        // 7 levels off LittleCMS. Better not to convert than to convert wrongly.
        if (IccProfileInfo.HasLut16A2B(profile))
        {
            return null;
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
    internal sealed class CodecTransform : IColorTransform
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
