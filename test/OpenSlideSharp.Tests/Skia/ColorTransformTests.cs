using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using OpenSlideSharp.SkiaSharp;
using OpenSlideSharp.SkiaSharp.Internal;
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

    // What LittleCMS 2.19 makes of FivePixels (as R, G, B) through the lookup-table fixtures. Produced with Pillow
    // 13.0.0: ImageCms.applyTransform(image, ImageCms.buildTransform(ImageCmsProfile(profile), createProfile('sRGB'),
    // 'RGB', 'RGB', renderingIntent=0)). All are the red/blue swap; through the mAB profiles gray 128 gives 127.
    private static readonly Dictionary<string, (byte R, byte G, byte B)[]> LittleCms = new()
    {
        ["swap-rb-lut.icc"] = new (byte, byte, byte)[] { (0, 0, 255), (0, 255, 0), (255, 0, 0), (128, 128, 128), (255, 255, 255) },
        ["swap-rb-lut-lab.icc"] = new (byte, byte, byte)[] { (0, 0, 255), (0, 255, 0), (255, 0, 0), (128, 128, 128), (255, 255, 255) },
        ["swap-rb-mab.icc"] = new (byte, byte, byte)[] { (0, 0, 255), (0, 255, 0), (255, 0, 0), (127, 127, 127), (255, 255, 255) },
        ["swap-rb-mab-lab.icc"] = new (byte, byte, byte)[] { (0, 0, 255), (0, 255, 0), (255, 0, 0), (127, 127, 127), (255, 255, 255) },
    };

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

    [Fact]
    public void Create_LutAToBLabProfile_UsesTier2AndMatchesLittleCms()
    {
        // The one kind of lookup-table profile tier 2 accepts: mAB with a Lab PCS.
        var transform = SkiaColorTransform.Create(Icc("swap-rb-mab-lab.icc"));
        Assert.IsType<SkiaColorTransform.CodecTransform>(transform);

        byte[] px = FivePixels();
        transform!.Apply(px, 5, 1);

        // Measured with SkiaSharp 3.119.4: (0,0,255) (0,255,0) (255,0,0) (128,128,128) (255,255,255).
        Assert.True(LargestDifference(px, LittleCms["swap-rb-mab-lab.icc"]) <= 3, "more than 3 levels off LittleCMS");
        Assert.All(new[] { px[3], px[7], px[11], px[15], px[19] }, a => Assert.Equal(255, a));
    }

    [Theory]
    [InlineData("swap-rb-lut.icc")]
    [InlineData("swap-rb-lut-lab.icc")]
    [InlineData("swap-rb-mab.icc")]
    public void Create_LutProfileOutsideTheTier2AllowList_ReturnsNull(string name)
    {
        // Skia's PNG codec converts these wrongly (see SkiaCodec_RefusedLutProfiles_DisagreeWithLittleCms), so they
        // are not converted at all.
        Assert.Null(SkiaColorTransform.Create(Icc(name)));
    }

    private static int ReadInt32(byte[] data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

    // The offset of a tag's 12-byte entry in the tag table.
    private static int TagEntry(byte[] profile, string signature)
    {
        for (int i = 0; i < ReadInt32(profile, 128); i++)
        {
            int entry = 132 + (i * 12);
            if (Encoding.ASCII.GetString(profile, entry, 4) == signature)
                return entry;
        }

        throw new InvalidOperationException(signature + " not found");
    }

    [Fact]
    public void Create_LutAToBLabProfileWithoutA2B0_ReturnsNull()
    {
        // Rename the A2B0 tag to A2B1. skcms would still use it, but tier 2 only accepts an A2B0 tag.
        byte[] profile = Icc("swap-rb-mab-lab.icc").ToArray();
        profile[TagEntry(profile, "A2B0") + 3] = (byte)'1';

        Assert.Null(SkiaColorTransform.Create(profile));
    }

    [Fact]
    public void LutAToBLabCheck_IsDefensive()
    {
        byte[] good = Icc("swap-rb-mab-lab.icc").ToArray();
        Assert.True(IccProfileInfo.IsLutAToBWithLabPcs(good));

        byte[] Patched(int offset, params byte[] bytes)
        {
            byte[] copy = (byte[])good.Clone();
            bytes.CopyTo(copy, offset);
            return copy;
        }

        int entry = TagEntry(good, "A2B0");
        int tagOffset = ReadInt32(good, entry + 4);

        Assert.False(IccProfileInfo.IsLutAToBWithLabPcs(Patched(20, (byte)'X', (byte)'Y', (byte)'Z', (byte)' ')));
        Assert.False(IccProfileInfo.IsLutAToBWithLabPcs(Patched(entry, (byte)'A', (byte)'2', (byte)'B', (byte)'1')));
        Assert.False(IccProfileInfo.IsLutAToBWithLabPcs(Patched(tagOffset, (byte)'m', (byte)'f', (byte)'t', (byte)'1')));
        Assert.False(IccProfileInfo.IsLutAToBWithLabPcs(Patched(128, 0xFF, 0xFF, 0xFF, 0xFF)));  // tag count
        Assert.False(IccProfileInfo.IsLutAToBWithLabPcs(Patched(entry + 4, 0x7F, 0xFF, 0xFF, 0xFF))); // offset
        Assert.False(IccProfileInfo.IsLutAToBWithLabPcs(Patched(entry + 8, 0xFF, 0xFF, 0xFF, 0xFF))); // size
        Assert.False(IccProfileInfo.IsLutAToBWithLabPcs(Patched(entry + 8, 0, 0, 0, 3)));              // no type
        Assert.False(IccProfileInfo.IsLutAToBWithLabPcs(good.AsSpan(0, 131).ToArray()));
        Assert.False(IccProfileInfo.IsLutAToBWithLabPcs(Array.Empty<byte>()));
    }

    [Theory]
    [InlineData("swap-rb-lut.icc")]
    [InlineData("swap-rb-lut-lab.icc")]
    [InlineData("swap-rb-mab.icc")]
    public void SkiaCodec_RefusedLutProfiles_DisagreeWithLittleCms(string name)
    {
        // The reason for the allow-list, measured with SkiaSharp 3.119.4. Through the PNG codec, swap-rb-lut.icc and
        // swap-rb-mab.icc both give (0,0,188) (0,188,0) (188,0,0) (92,92,92) (188,188,188): skcms reads XYZ PCS
        // values with 0xFFFF as 1.0 instead of 0x8000, halving linear light. swap-rb-lut-lab.icc gives (0,4,255)
        // (0,254,7) (253,6,2) (126,127,128) (252,254,255), up to 7 levels off: skcms reads Lab in the ICC v4
        // encoding, but lut16 keeps the v2 one. Tier 2 would only be acceptable within 3 levels. If this test fails
        // after a SkiaSharp update, the codec may have been fixed and the allow-list could grow.
        byte[] px = FivePixels();
        new SkiaColorTransform.CodecTransform(Icc(name).ToArray()).Apply(px, 5, 1);

        Assert.True(LargestDifference(px, LittleCms[name]) > 3, $"{name}: within 3 levels of LittleCMS");
    }

    [Fact]
    public void CodecTransform_MatrixProfile_MatchesTier1()
    {
        // The PNG round trip of tier 2 applies a profile the same way as tier 1 does.
        var size = 32;
        var pixels = new byte[size * size * 4];
        new Random(5).NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        byte[] tier1 = (byte[])pixels.Clone();
        byte[] tier2 = (byte[])pixels.Clone();

        SkiaColorTransform.Create(Icc("swap-rb-matrix.icc"))!.Apply(tier1, size, size);
        new SkiaColorTransform.CodecTransform(Icc("swap-rb-matrix.icc").ToArray()).Apply(tier2, size, size);

        for (int i = 0; i < pixels.Length; i++)
            Assert.True(Math.Abs(tier1[i] - tier2[i]) <= 1, $"byte {i}: tier 1 {tier1[i]}, tier 2 {tier2[i]}");
    }

    [Fact]
    public void BuildRgbPng_StoresPixelsUncompressedWithTheGivenIccp()
    {
        byte[] iccp = PngChunks.BuildIccpData(Icc("swap-rb-matrix.icc").ToArray());
        byte[] png = PngChunks.BuildRgbPng(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, 2, 1, iccp);

        var chunks = new List<(string Type, byte[] Data)>();
        for (int pos = 8; pos < png.Length;)
        {
            int length = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
            chunks.Add((Encoding.ASCII.GetString(png, pos + 4, 4), png[(pos + 8)..(pos + 8 + length)]));
            pos += 12 + length;
        }

        Assert.Equal(new[] { "IHDR", "iCCP", "IDAT", "IEND" }, chunks.ConvertAll(c => c.Type));
        Assert.Equal(iccp, chunks[1].Data);
        byte[] scanline = { 0, 3, 2, 1, 6, 5, 4 }; // filter type 0, then R, G, B per pixel
        Assert.True(chunks[2].Data.AsSpan().IndexOf(scanline) >= 0, "the IDAT holds the scanline as is");
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
    public void GetCached_IsKeyedByTheProfileArrayReference()
    {
        byte[] profile = File.ReadAllBytes(TestFiles.Get("icc/swap-rb-matrix.icc"));
        IColorTransform? first = SkiaColorTransform.GetCached(profile);
        Assert.NotNull(first);

        // The same array gives the same transform; an equal copy is another key.
        Assert.Same(first, SkiaColorTransform.GetCached(profile));
        Assert.NotSame(first, SkiaColorTransform.GetCached((byte[])profile.Clone()));

        // A profile that is only part of its array is not cached.
        var padded = new byte[profile.Length + 1];
        profile.CopyTo(padded, 1);
        var part = new ReadOnlyMemory<byte>(padded, 1, profile.Length);
        Assert.NotSame(SkiaColorTransform.GetCached(part), SkiaColorTransform.GetCached(part));

        // The bytes are not looked at again: an array changed after the first call keeps the old transform, which is
        // why callers must not change it.
        Array.Clear(profile);
        Assert.Null(SkiaColorTransform.Create(profile));
        Assert.Same(first, SkiaColorTransform.GetCached(profile));
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
