using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using OpenSlideSharp.SkiaSharp;
using Xunit;

namespace OpenSlideSharp.Tests.Skia;

public class ColorTransformTests
{
    private static ReadOnlyMemory<byte> Icc(string name) => File.ReadAllBytes(TestFiles.Get("icc/" + name));

    // Red, green, blue, gray 128 as opaque BGRA.
    private static byte[] FourPixels() => new byte[]
    {
        0, 0, 255, 255,
        0, 255, 0, 255,
        255, 0, 0, 255,
        128, 128, 128, 255,
    };

    [Fact]
    public void Create_MatrixProfile_SwapsRedAndBlue()
    {
        var transform = SkiaColorTransform.Create(Icc("swap-rb-matrix.icc"));
        Assert.NotNull(transform);

        byte[] px = FourPixels();
        transform!.Apply(px, 4, 1);

        Assert.Equal(new byte[] { 255, 0, 0, 255 }, px[0..4]);   // red -> (R0,G0,B255)
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, px[4..8]);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, px[8..12]);  // blue -> (R255,G0,B0)
        Assert.Equal(new byte[] { 129, 129, 129, 255 }, px[12..16]);
    }

    // Red, green, blue, gray 128 and white as opaque BGRA.
    private static byte[] FivePixels() => new byte[]
    {
        0, 0, 255, 255,
        0, 255, 0, 255,
        255, 0, 0, 255,
        128, 128, 128, 255,
        255, 255, 255, 255,
    };

    // What LittleCMS 2.19 makes of FivePixels (as R, G, B) through the lut16 fixtures. Produced with Pillow 13.0.0:
    // ImageCms.applyTransform(image, ImageCms.buildTransform(ImageCmsProfile(profile), createProfile('sRGB'),
    // 'RGB', 'RGB', renderingIntent=0)). Both are the exact red/blue swap.
    private static readonly (byte R, byte G, byte B)[] LittleCmsLut16Xyz =
        { (0, 0, 255), (0, 255, 0), (255, 0, 0), (128, 128, 128), (255, 255, 255) };

    private static readonly (byte R, byte G, byte B)[] LittleCmsLut16Lab =
        { (0, 0, 255), (0, 255, 0), (255, 0, 0), (128, 128, 128), (255, 255, 255) };

    private static int LargestDifference(byte[] bgra, (byte R, byte G, byte B)[] expected)
    {
        int largest = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            largest = Math.Max(largest, Math.Abs(bgra[i * 4 + 2] - expected[i].R));
            largest = Math.Max(largest, Math.Abs(bgra[i * 4 + 1] - expected[i].G));
            largest = Math.Max(largest, Math.Abs(bgra[i * 4] - expected[i].B));
        }

        return largest;
    }

    [Theory]
    [InlineData("swap-rb-lut.icc")]
    [InlineData("swap-rb-lut-lab.icc")]
    public void Create_Lut16Profile_ReturnsNull(string name)
    {
        // Skia's PNG codec misreads lut16 PCS values (see SkiaCodec_Lut16Profiles_DisagreeWithLittleCms), so these
        // profiles are not converted at all.
        Assert.Null(SkiaColorTransform.Create(Icc(name)));
    }

    [Theory]
    [InlineData("swap-rb-lut.icc")]
    [InlineData("swap-rb-lut-lab.icc")]
    public void SkiaCodec_Lut16Profiles_DisagreeWithLittleCms(string name)
    {
        // The reason for the test above, measured with SkiaSharp 3.119.4. Through the PNG codec,
        // swap-rb-lut.icc gives (0,0,188) (0,188,0) (188,0,0) (92,92,92) (188,188,188): skcms reads the lut16
        // XYZ values with 0xFFFF as 1.0 instead of 0x8000, halving linear light. swap-rb-lut-lab.icc gives
        // (0,4,255) (0,254,7) (253,6,2) (126,127,128) (252,254,255), up to 7 levels off: skcms reads the ICC v4
        // Lab encoding, but lut16 keeps the v2 one. Tier 2 would only be acceptable within 3 levels. If this test
        // fails after a SkiaSharp update, the codec may have been fixed and lut16 profiles could be converted again.
        var expected = name == "swap-rb-lut.icc" ? LittleCmsLut16Xyz : LittleCmsLut16Lab;
        byte[] px = FivePixels();
        new SkiaColorTransform.CodecTransform(Icc(name).ToArray()).Apply(px, 5, 1);

        Assert.True(LargestDifference(px, expected) > 3, $"{name}: within 3 levels of LittleCMS");
    }

    private static byte[] WithDataColorSpace(string name, string signature)
    {
        byte[] profile = Icc(name).ToArray();
        Encoding.ASCII.GetBytes(signature).CopyTo(profile, 16);
        return profile;
    }

    [Theory]
    [InlineData("GRAY")]
    [InlineData("CMYK")]
    public void Create_NonRgbDataColorSpace_ReturnsNull(string signature)
    {
        // Skia accepts both: it would apply a gray profile to RGB pixels as a matrix, and its PNG codec ignores a
        // CMYK profile, so the pixels would be neither converted nor tagged.
        Assert.Null(SkiaColorTransform.Create(WithDataColorSpace("swap-rb-matrix.icc", signature)));
        Assert.Null(SkiaColorTransform.Create(WithDataColorSpace("swap-rb-lut-lab.icc", signature)));
    }

    [Fact]
    public void Create_ShortProfile_ReturnsNull()
    {
        Assert.Null(SkiaColorTransform.Create(Icc("swap-rb-matrix.icc").Slice(0, 127)));
    }

    [Fact]
    public void Create_SrgbProfile_ReturnsNoOp()
    {
        var transform = SkiaColorTransform.Create(Icc("srgb-matrix.icc"));
        Assert.Same(SkiaColorTransform.NoOpTransform.Instance, transform);

        var pixels = new byte[16 * 16 * 4];
        new Random(3).NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        byte[] before = (byte[])pixels.Clone();
        transform!.Apply(pixels, 16, 16);
        Assert.Equal(before, pixels);
    }

    [Fact]
    public void Create_GarbageOrEmpty_ReturnsNull()
    {
        var garbage = new byte[300];
        new Random(1).NextBytes(garbage);

        Assert.Null(SkiaColorTransform.Create(garbage));
        Assert.Null(SkiaColorTransform.Create(ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void Apply_ConcurrentCalls_AreConsistent()
    {
        var transform = SkiaColorTransform.Create(Icc("swap-rb-matrix.icc"))!;
        const int size = 64;

        byte[] Source(int seed)
        {
            var b = new byte[size * size * 4];
            new Random(seed).NextBytes(b);
            for (int i = 3; i < b.Length; i += 4) b[i] = 255;
            return b;
        }

        var expected = new byte[8][];
        for (int t = 0; t < 8; t++)
        {
            expected[t] = Source(t);
            transform.Apply(expected[t], size, size);
        }

        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, t =>
        {
            for (int i = 0; i < 200; i++)
            {
                var px = Source(t);
                transform.Apply(px, size, size);
                Assert.True(expected[t].AsSpan().SequenceEqual(px), $"thread {t} iteration {i} differs");
            }
        });
    }
}
