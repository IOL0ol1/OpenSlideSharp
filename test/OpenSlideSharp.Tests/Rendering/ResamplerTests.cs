using System;
using System.Collections.Generic;
using Xunit;

namespace OpenSlideSharp.Tests.Rendering;

public class ResamplerTests
{
    public sealed class ResampleOracle
    {
        public string Source { get; set; } = "";
        public List<ResampleOutput> Outputs { get; set; } = new();
    }

    public sealed class ResampleOutput
    {
        public int[] Size { get; set; } = Array.Empty<int>();
        public string File { get; set; } = "";
    }

    public static IEnumerable<object[]> Sizes()
    {
        foreach (var o in OracleData.Load<ResampleOracle>("resample.json").Outputs)
            yield return new object[] { o.Size[0], o.Size[1], o.File };
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Resize_MatchesPillowExactly(int width, int height, string file)
    {
        var oracle = OracleData.Load<ResampleOracle>("resample.json");
        var src = OracleData.LoadPng(oracle.Source);
        var expected = OracleData.LoadPng(file);
        Assert.Equal((width, height), (expected.Width, expected.Height));

        byte[] dst = new byte[width * height * 4];
        LanczosResampler.Resize(src.Bgra, src.Width, src.Height, dst, width, height);

        for (int i = 0; i < dst.Length; i++)
            if (dst[i] != expected.Bgra[i])
                Assert.Fail($"{width}x{height}: byte {i} (pixel {i / 4}, channel {i % 4}) expected {expected.Bgra[i]} got {dst[i]}");
    }

    [Fact]
    public void Resize_ConstantImageStaysConstant()
    {
        byte[] src = new byte[53 * 31 * 4];
        Array.Fill(src, (byte)77);
        byte[] dst = new byte[7 * 29 * 4];
        LanczosResampler.Resize(src, 53, 31, dst, 7, 29);
        Assert.All(dst, b => Assert.Equal(77, b));
    }

    [Fact]
    public void Resize_InvalidArguments_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LanczosResampler.Resize(new byte[16], 2, 2, new byte[16], 0, 2));
        Assert.Throws<ArgumentException>(() =>
            LanczosResampler.Resize(new byte[8], 2, 2, new byte[16], 2, 2));
        Assert.Throws<ArgumentException>(() =>
            LanczosResampler.Resize(new byte[16], 2, 2, new byte[4], 2, 2));
    }
}
