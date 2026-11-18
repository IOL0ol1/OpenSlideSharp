using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using SkiaSharp;
using OpenSlideSharp.SkiaSharp;
using Xunit;

namespace OpenSlideSharp.Tests.Skia;

public class EncodeTests
{
    private static PixelImage Opaque(int w, int h, byte[] bgra, ReadOnlyMemory<byte> icc = default)
        => new(w, h, PixelFormat.Bgra32Opaque, bgra, icc);

    private static byte[] Icc(string name) => File.ReadAllBytes(TestFiles.Get("icc/" + name));

    private static byte[] BoxesIcc()
    {
        using var slide = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        return slide.ColorProfile.ToArray();
    }

    private static SKBitmap Decode(byte[] encoded) => SKBitmap.Decode(encoded);

    private static List<(string Type, byte[] Data)> PngChunks(byte[] png)
    {
        var list = new List<(string, byte[])>();
        int pos = 8;
        while (pos < png.Length)
        {
            int len = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
            string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            list.Add((type, png[(pos + 8)..(pos + 8 + len)]));
            pos += 12 + len;
        }
        return list;
    }

    private static byte[]? PngIccProfile(byte[] png)
    {
        foreach (var (type, data) in PngChunks(png))
        {
            if (type != "iCCP") continue;
            int nul = Array.IndexOf(data, (byte)0);
            Assert.Equal(0, data[nul + 1]);
            using var z = new ZLibStream(new MemoryStream(data, nul + 2, data.Length - nul - 2), CompressionMode.Decompress);
            var ms = new MemoryStream();
            z.CopyTo(ms);
            return ms.ToArray();
        }
        return null;
    }

    private static byte[] JpegIccProfile(byte[] jpeg)
    {
        var parts = new SortedDictionary<int, byte[]>();
        int pos = 2;
        while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF)
        {
            byte marker = jpeg[pos + 1];
            if (marker == 0xDA) break;
            int len = (jpeg[pos + 2] << 8) | jpeg[pos + 3];
            if (marker == 0xE2 && System.Text.Encoding.ASCII.GetString(jpeg, pos + 4, 11) == "ICC_PROFILE" && jpeg[pos + 15] == 0)
            {
                parts[jpeg[pos + 16]] = jpeg[(pos + 18)..(pos + 2 + len)];
            }
            pos += 2 + len;
        }
        var all = new List<byte>();
        foreach (var p in parts.Values) all.AddRange(p);
        return all.ToArray();
    }

    [Fact]
    public void Encode_Png_RoundTripsPixels()
    {
        // (B,G,R,A): red, green, blue, gray
        var bgra = new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 90, 90, 90, 255 };
        var png = Opaque(2, 2, bgra).Encode(ImageFormat.Png, colorMode: ColorMode.Ignore);

        Assert.DoesNotContain(PngChunks(png), c => c.Type == "iCCP");
        using var bmp = Decode(png);
        Assert.Equal(2, bmp.Width);
        Assert.Equal(new SKColor(255, 0, 0), bmp.GetPixel(0, 0));
        Assert.Equal(new SKColor(0, 255, 0), bmp.GetPixel(1, 0));
        Assert.Equal(new SKColor(0, 0, 255), bmp.GetPixel(0, 1));
        Assert.Equal(new SKColor(90, 90, 90), bmp.GetPixel(1, 1));
    }

    [Fact]
    public void Encode_Jpeg_HasJfifSoiAndDecodes()
    {
        var bgra = new byte[16 * 16 * 4];
        Array.Fill(bgra, (byte)200);
        var jpeg = Opaque(16, 16, bgra).Encode(ImageFormat.Jpeg, 90);

        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);
        using var codec = SKCodec.Create(new MemoryStream(jpeg));
        Assert.Equal(new SKSizeI(16, 16), new SKSizeI(codec.Info.Width, codec.Info.Height));
    }

    [Fact]
    public void Encode_EmbedPng_ContainsExactProfile()
    {
        var icc = BoxesIcc();
        var img = Opaque(2, 2, new byte[16], icc);
        var png = img.Encode(ImageFormat.Png, colorMode: ColorMode.Embed);

        Assert.Equal(icc, PngIccProfile(png));
        var chunks = PngChunks(png);
        Assert.Equal("IHDR", chunks[0].Type);
        Assert.Equal("iCCP", chunks[1].Type);
        Assert.Single(chunks, c => c.Type == "iCCP");
        Assert.DoesNotContain(chunks, c => c.Type is "sRGB" or "gAMA" or "cHRM");
        using var bmp = Decode(png);
        Assert.Equal(2, bmp.Width);
    }

    [Fact]
    public void Encode_EmbedJpeg_ContainsExactProfile()
    {
        var icc = BoxesIcc();
        var bgra = new byte[8 * 8 * 4];
        var jpeg = Opaque(8, 8, bgra, icc).Encode(ImageFormat.Jpeg, colorMode: ColorMode.Embed);
        Assert.Equal(icc, JpegIccProfile(jpeg));
        using (var bmp = Decode(jpeg)) Assert.Equal(8, bmp.Width);

        var big = new byte[70000];
        new Random(7).NextBytes(big);
        var jpeg2 = Opaque(8, 8, bgra, big).Encode(ImageFormat.Jpeg, colorMode: ColorMode.Embed);
        Assert.Equal(big, JpegIccProfile(jpeg2));
        int segments = 0;
        for (int i = 2; i + 16 < jpeg2.Length && i < 140000; i++)
        {
            if (jpeg2[i] == 0xFF && jpeg2[i + 1] == 0xE2 && jpeg2[i + 4] == (byte)'I' && jpeg2[i + 5] == (byte)'C') segments++;
        }
        Assert.Equal(2, segments);
    }

    [Fact]
    public void Encode_DefaultWithMatrixProfile_ConvertsAndDropsProfile()
    {
        var img = Opaque(1, 1, new byte[] { 0, 0, 255, 255 }, Icc("swap-rb-matrix.icc"));
        var png = img.Encode(ImageFormat.Png);

        Assert.DoesNotContain(PngChunks(png), c => c.Type == "iCCP");
        using var bmp = Decode(png);
        Assert.Equal(new SKColor(0, 0, 255), bmp.GetPixel(0, 0));
    }

    [Fact]
    public void Encode_DefaultWithGarbageIcc_EmbedsProfile()
    {
        var garbage = new byte[300];
        new Random(1).NextBytes(garbage);
        var png = Opaque(1, 1, new byte[] { 1, 2, 3, 255 }, garbage).Encode(ImageFormat.Png);

        Assert.Equal(garbage, PngIccProfile(png));
    }

    [Theory]
    [InlineData("GRAY")]
    [InlineData("CMYK")]
    public void Encode_DefaultWithNonRgbProfile_EmbedsProfileAndKeepsPixels(string signature)
    {
        byte[] icc = Icc("swap-rb-matrix.icc");
        System.Text.Encoding.ASCII.GetBytes(signature).CopyTo(icc, 16);
        var img = Opaque(2, 1, new byte[] { 0, 0, 255, 255, 255, 0, 0, 255 }, icc);

        var png = img.Encode(ImageFormat.Png);

        Assert.Equal(icc, PngIccProfile(png));
        var idat = PngChunks(png).Find(c => c.Type == "IDAT").Data;
        var unconverted = PngChunks(img.Encode(ImageFormat.Png, colorMode: ColorMode.Ignore)).Find(c => c.Type == "IDAT").Data;
        Assert.Equal(unconverted, idat);
    }

    [Fact]
    public void Encode_DefaultWithLutAToBLabProfile_ConvertsAndDropsProfile()
    {
        var img = Opaque(1, 1, new byte[] { 0, 0, 255, 255 }, Icc("swap-rb-mab-lab.icc"));
        var png = img.Encode(ImageFormat.Png);

        Assert.DoesNotContain(PngChunks(png), c => c.Type == "iCCP");
        using var bmp = Decode(png);
        var c = bmp.GetPixel(0, 0);
        Assert.True(c.Blue >= 252 && c.Red <= 3 && c.Green <= 3, c.ToString()); // red became blue
    }

    [Fact]
    public void Encode_DefaultWithRefusedLutProfile_EmbedsProfile()
    {
        foreach (string name in new[] { "swap-rb-lut.icc", "swap-rb-lut-lab.icc", "swap-rb-mab.icc" })
        {
            byte[] icc = Icc(name);
            var img = Opaque(1, 1, new byte[] { 0, 0, 255, 255 }, icc);
            Assert.Equal(icc, PngIccProfile(img.Encode(ImageFormat.Png)));
            Assert.Equal(icc, JpegIccProfile(img.Encode(ImageFormat.Jpeg)));
        }
    }

    [Fact]
    public void Encode_DefaultWithSrgbProfile_DropsProfileAndLeavesPixelsAlone()
    {
        // Premultiplied, half transparent: any conversion would unpremultiply the pixels itself, so only an
        // untouched image encodes exactly like Ignore.
        var bgra = new byte[] { 50, 100, 20, 200, 10, 20, 30, 40, 0, 0, 255, 255, 1, 2, 3, 3 };
        var img = new PixelImage(2, 2, PixelFormat.Bgra32Premultiplied, bgra, Icc("srgb-matrix.icc"));

        var png = img.Encode(ImageFormat.Png);
        var jpeg = img.Encode(ImageFormat.Jpeg, 90);

        Assert.DoesNotContain(PngChunks(png), c => c.Type == "iCCP");
        Assert.Equal(img.Encode(ImageFormat.Png, colorMode: ColorMode.Ignore), png);
        Assert.Empty(JpegIccProfile(jpeg));
        Assert.Equal(img.Encode(ImageFormat.Jpeg, 90, ColorMode.Ignore), jpeg);
    }

    private sealed class ThrowingTransform : IColorTransform
    {
        public void Apply(Span<byte> pixels, int width, int height) => throw new InvalidOperationException("cannot convert");
    }

    [Fact]
    public void Encode_DefaultWhenTransformThrows_FallsBackToEmbed()
    {
        byte[] profile = Icc("swap-rb-matrix.icc");
        var img = Opaque(2, 2, new byte[2 * 2 * 4], profile);

        byte[] png = PixelImageExtensions.Encode(img, ImageFormat.Png, 75, ColorMode.Default, _ => new ThrowingTransform());
        byte[] jpeg = PixelImageExtensions.Encode(img, ImageFormat.Jpeg, 75, ColorMode.Default, _ => new ThrowingTransform());

        Assert.Equal(profile, PngIccProfile(png));
        Assert.Equal(profile, JpegIccProfile(jpeg));
        Assert.Equal(img.Encode(ImageFormat.Png, colorMode: ColorMode.Embed), png);
    }

    [Fact]
    public void Encode_DefaultWithUnusableProfileTooLargeForJpeg_DropsIt()
    {
        // 255 APP2 segments hold 255 * 65519 bytes; Default must not throw because the profile does not fit.
        var huge = new byte[(255 * 65519) + 1];
        var img = Opaque(8, 8, new byte[8 * 8 * 4], huge);

        var jpeg = img.Encode(ImageFormat.Jpeg);

        Assert.Empty(JpegIccProfile(jpeg));
        using (var bmp = Decode(jpeg)) Assert.Equal(8, bmp.Width);
        Assert.Equal(huge, PngIccProfile(img.Encode(ImageFormat.Png))); // PNG has room for it
        Assert.Throws<ArgumentException>("profile", () => img.Encode(ImageFormat.Jpeg, colorMode: ColorMode.Embed));
    }

    [Fact]
    public void Encode_Ignore_DropsProfile()
    {
        var img = Opaque(1, 1, new byte[] { 0, 0, 255, 255 }, Icc("swap-rb-matrix.icc"));
        var png = img.Encode(ImageFormat.Png, colorMode: ColorMode.Ignore);
        Assert.DoesNotContain(PngChunks(png), c => c.Type == "iCCP");
        using var bmp = Decode(png);
        Assert.Equal(new SKColor(255, 0, 0), bmp.GetPixel(0, 0));

        var jpeg = img.Encode(ImageFormat.Jpeg, colorMode: ColorMode.Ignore);
        Assert.Empty(JpegIccProfile(jpeg));
    }

    [Fact]
    public void Encode_JpegWithTransparentPixels_FlattensOnWhite()
    {
        var img = new PixelImage(1, 1, PixelFormat.Bgra32Premultiplied, new byte[4]);
        var jpeg = img.Encode(ImageFormat.Jpeg, 100);
        using var bmp = Decode(jpeg);
        var c = bmp.GetPixel(0, 0);
        Assert.True(c.Red >= 250 && c.Green >= 250 && c.Blue >= 250, c.ToString());
    }

    [Fact]
    public void Encode_TransparentPng_WithMatrixProfile_KeepsAlpha()
    {
        // Straight red at 50% alpha: color is converted, alpha preserved.
        var img = new PixelImage(1, 1, PixelFormat.Bgra32, new byte[] { 0, 0, 255, 128 }, Icc("swap-rb-matrix.icc"));
        var png = img.Encode(ImageFormat.Png);
        using var bmp = Decode(png);
        var c = bmp.GetPixel(0, 0);
        Assert.Equal(128, c.Alpha);
        Assert.True(c.Blue >= 250 && c.Red <= 5, c.ToString());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Encode_QualityOutOfRange_Throws(int quality)
    {
        var img = Opaque(1, 1, new byte[4]);
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Encode(ImageFormat.Jpeg, quality));
    }

    [Theory]
    [InlineData(PixelFormat.Bgra32Premultiplied, SKAlphaType.Premul)]
    [InlineData(PixelFormat.Bgra32, SKAlphaType.Unpremul)]
    [InlineData(PixelFormat.Bgra32Opaque, SKAlphaType.Opaque)]
    public void ToSKBitmap_MapsAlphaType(PixelFormat format, SKAlphaType expected)
    {
        var data = new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 };
        using var bmp = new PixelImage(2, 1, format, data).ToSKBitmap();

        Assert.Equal(expected, bmp.AlphaType);
        Assert.Equal(SKColorType.Bgra8888, bmp.ColorType);
        Assert.Null(bmp.Info.ColorSpace);
        Assert.Equal(data, bmp.Bytes);
        data[0] = 99;
        Assert.Equal(1, bmp.Bytes[0]); // copied
        using var image = new PixelImage(2, 1, format, data).ToSKImage();
        Assert.Equal(2, image.Width);
    }
}
