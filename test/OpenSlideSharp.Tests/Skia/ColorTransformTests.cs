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

    [Fact]
    public void Create_LutProfile_UsesCodecPathAndSwapsChannels()
    {
        var transform = SkiaColorTransform.Create(Icc("swap-rb-lut.icc"));
        Assert.NotNull(transform);

        byte[] px = FourPixels();
        transform!.Apply(px, 4, 1);

        // BGRA order: red input must have a strong blue and almost no red or green.
        Assert.True(px[0] >= 150 && px[2] <= 16 && px[1] <= 16, $"red -> B={px[0]} G={px[1]} R={px[2]}");
        Assert.True(px[10] >= 150 && px[9] <= 16 && px[8] <= 16, $"blue -> B={px[8]} G={px[9]} R={px[10]}");
        Assert.True(px[5] >= 150, $"green -> G={px[5]}");
        Assert.All(new[] { px[3], px[7], px[11], px[15] }, a => Assert.Equal(255, a));
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
