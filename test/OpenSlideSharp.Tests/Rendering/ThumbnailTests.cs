using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace OpenSlideSharp.Tests.Rendering;

public class ThumbnailTests
{
    public sealed class ThumbnailOracle
    {
        public List<ThumbnailEntry> Thumbnails { get; set; } = new();
    }

    public sealed class ThumbnailEntry
    {
        public string Source { get; set; } = "";
        public int[] MaxSize { get; set; } = Array.Empty<int>();
        public int Level { get; set; }
        public int[] PythonSize { get; set; } = Array.Empty<int>();
        public bool Exact { get; set; }
        public string File { get; set; } = "";
    }

    public static IEnumerable<object[]> Cases() =>
        OracleData.Load<ThumbnailOracle>("thumbnail.json").Thumbnails
            .Select(t => new object[] { t.Source, t.MaxSize[0], t.MaxSize[1] });

    [Theory]
    [MemberData(nameof(Cases))]
    public void GetThumbnail_MatchesPython(string source, int maxWidth, int maxHeight)
    {
        ThumbnailEntry entry = OracleData.Load<ThumbnailOracle>("thumbnail.json").Thumbnails
            .Single(t => t.Source == source && t.MaxSize[0] == maxWidth && t.MaxSize[1] == maxHeight);
        using var osr = OpenSlide.Open(TestFiles.Get(source));

        PixelImage thumb = osr.GetThumbnail(maxWidth, maxHeight);

        Assert.Equal(PixelFormat.Bgra32Opaque, thumb.Format);
        Assert.Equal((entry.PythonSize[0], entry.PythonSize[1]), (thumb.Width, thumb.Height));
        Assert.Equal(588, thumb.IccProfile.Length);
        if (entry.Exact)
        {
            var expected = OracleData.LoadPng(entry.File);
            Assert.Equal((expected.Width, expected.Height), (thumb.Width, thumb.Height));
            for (int i = 0; i < expected.Bgra.Length; i++)
                if (thumb.Data[i] != expected.Bgra[i])
                    Assert.Fail($"{entry.File}: byte {i} (pixel {i / 4}, channel {i % 4}) expected {expected.Bgra[i]} got {thumb.Data[i]}");
        }
    }

    [Fact]
    public void GetThumbnail_InvalidSize_Throws()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        var width = Assert.Throws<ArgumentOutOfRangeException>(() => osr.GetThumbnail(0, 10));
        Assert.Equal("maxWidth", width.ParamName);
        var height = Assert.Throws<ArgumentOutOfRangeException>(() => osr.GetThumbnail(10, -1));
        Assert.Equal("maxHeight", height.ParamName);
    }
}
