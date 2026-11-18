using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenSlideSharp.SkiaSharp;
using OpenSlideSharp.Tool.Export;
using SkiaSharp;
using Xunit;

namespace OpenSlideSharp.Tool.Tests;

public class DeepZoomCommandTests
{
    private const string Marker = ".openslide-sharp-output";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DeepZoom_Boxes_WritesAllTiles()
    {
        using var temp = new TempDirectory();
        string output = temp.Combine("boxes");
        var stderr = new StringWriter();

        int code = await DeepZoomCommand.RunAsync(
            new DeepZoomArguments(TestFiles.Get("boxes.tiff")) { Output = output, TileSize = 254, Overlap = 1 }, stderr, Token);

        Assert.True(code == 0, stderr.ToString());
        using var slide = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        var generator = new DeepZoomGenerator(slide, 254, 1, limitBounds: true);
        Assert.Equal(generator.GetDzi("jpeg"), File.ReadAllText(output + ".dzi"));

        string files = output + "_files";
        string[] tiles = Directory.GetFiles(files, "*.jpeg", SearchOption.AllDirectories);
        Assert.Equal(generator.TileCount, tiles.Length);
        Assert.Equal(11, tiles.Length);

        // Besides the tiles, only the marker that lets a later run replace the directory.
        Assert.Equal(
            new[] { Path.Combine(files, Marker) },
            Directory.GetFiles(files, "*", SearchOption.AllDirectories).Except(tiles));
        Assert.Equal(
            Enumerable.Range(0, 10).Select(i => i.ToString(CultureInfo.InvariantCulture)),
            Directory.GetDirectories(files).Select(Path.GetFileName).OrderBy(n => int.Parse(n!, CultureInfo.InvariantCulture)));

        // Any tile: it decodes, with the size the generator gives for its address.
        string tile = tiles[Random.Shared.Next(tiles.Length)];
        int level = int.Parse(Path.GetFileName(Path.GetDirectoryName(tile))!, CultureInfo.InvariantCulture);
        string[] address = Path.GetFileNameWithoutExtension(tile).Split('_');
        SlideSize expected = generator.GetTileDimensions(
            level, long.Parse(address[0], CultureInfo.InvariantCulture), long.Parse(address[1], CultureInfo.InvariantCulture));
        Assert.Equal(".jpeg", Path.GetExtension(tile));
        using SKCodec codec = SKCodec.Create(tile)!;
        Assert.Equal(SKEncodedImageFormat.Jpeg, codec.EncodedFormat);
        Assert.Equal(((int)expected.Width, (int)expected.Height), (codec.Info.Width, codec.Info.Height));
        using SKBitmap bitmap = SKBitmap.Decode(codec)!;
        Assert.NotNull(bitmap);

        Assert.Contains("Tiling slide: 11/11 tiles", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeepZoom_Png_UsesPngExtension()
    {
        using var temp = new TempDirectory();
        string output = temp.Combine("boxes");

        int code = await DeepZoomCommand.RunAsync(
            new DeepZoomArguments(TestFiles.Get("boxes.tiff")) { Output = output, Format = ImageFormat.Png }, new StringWriter(), Token);

        Assert.Equal(0, code);
        Assert.Contains("Format=\"png\"", File.ReadAllText(output + ".dzi"), StringComparison.Ordinal);
        string[] tiles = Directory.GetFiles(output + "_files", "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != Marker)
            .ToArray();
        Assert.Equal(11, tiles.Length);
        Assert.All(tiles, t => Assert.Equal(".png", Path.GetExtension(t)));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, File.ReadAllBytes(tiles[0])[..4]);
    }

    [Fact]
    public async Task DeepZoom_Viewer_WritesIndexAndStatic()
    {
        using var temp = new TempDirectory();
        string output = temp.Combine("small");

        int code = await DeepZoomCommand.RunAsync(
            new DeepZoomArguments(TestFiles.Get("small.svs")) { Output = output, Viewer = true }, new StringWriter(), Token);

        Assert.Equal(0, code);
        string index = File.ReadAllText(Path.Combine(output, "index.html"));
        Assert.Contains("slide.dzi", index, StringComparison.Ordinal);
        Assert.Contains("thumbnail.dzi", index, StringComparison.Ordinal);
        Assert.Contains("openslide.vendor", index, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(output, "static", "openseadragon.min.js")));
        Assert.True(File.Exists(Path.Combine(output, "static", "openseadragon-scalebar.js")));
        Assert.True(File.Exists(Path.Combine(output, "static", "images", "zoomin_rest.png")));
        Assert.True(File.Exists(Path.Combine(output, "static", "licenses", "LICENSE.openseadragon")));
        Assert.True(File.Exists(Path.Combine(output, "slide.dzi")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(output, "slide_files"), "*.jpeg", SearchOption.AllDirectories));
        Assert.True(File.Exists(Path.Combine(output, "thumbnail.dzi")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(output, "thumbnail_files"), "*.jpeg", SearchOption.AllDirectories));

        // The DZI documents are in the page, so it works from file:// too.
        using var slide = OpenSlide.Open(TestFiles.Get("small.svs"));
        string thumbnailDzi = new DeepZoomGenerator(slide.AssociatedImages["thumbnail"], 254, 1).GetDzi("jpeg");
        Assert.Equal(thumbnailDzi, File.ReadAllText(Path.Combine(output, "thumbnail.dzi")));
        Assert.Contains(JsonSerializer.Serialize(thumbnailDzi), index, StringComparison.Ordinal);
    }

    [Fact]
    public void Viewer_EscapesSlideText()
    {
        string html = ViewerWriter.RenderIndex(
            "0.25",
            [new KeyValuePair<string, string>("<b>label</b>", "label.dzi")],
            [new KeyValuePair<string, string>("vendor\"><script>alert(1)</script>", "<img src=x onerror=alert(2)>")],
            new Dictionary<string, string> { ["slide.dzi"] = "<Image></script><script>alert(3)</script>" });

        Assert.DoesNotContain("<script>alert", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;label&lt;/b&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&lt;img src=x onerror=alert(2)&gt;", html, StringComparison.Ordinal);
        Assert.Equal(3, html.Split("<script").Length - 1);
    }

    [Theory]
    [InlineData("thumbnail", "thumbnail")]
    [InlineData("Macro Image", "macro_image")]
    [InlineData(" label--2 ", "_label_2_")]
    [InlineData("Ünïcödé", "unicode")]
    [InlineData("ＡＢＣ", "abc")]
    [InlineData("日本", "")]
    [InlineData("a日b", "ab")]
    [InlineData("℡", "_")]
    public void Slugify_MatchesPython(string name, string expected)
    {
        Assert.Equal(expected, ViewerWriter.Slugify(name));
    }

    [Fact]
    public async Task DeepZoom_ExistingOutput_IsReplaced()
    {
        using var temp = new TempDirectory();
        string output = temp.Combine("boxes");
        var args = new DeepZoomArguments(TestFiles.Get("boxes.tiff")) { Output = output };
        Assert.Equal(0, await DeepZoomCommand.RunAsync(args, new StringWriter(), Token));
        Assert.True(File.Exists(Path.Combine(output + "_files", Marker)));
        string stale = Path.Combine(output + "_files", "99", "stale.jpeg");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllText(stale, "stale");
        File.WriteAllText(output + ".dzi", "stale");

        int code = await DeepZoomCommand.RunAsync(args, new StringWriter(), Token);

        Assert.Equal(0, code);
        Assert.False(File.Exists(stale));
        Assert.False(Directory.Exists(Path.Combine(output + "_files", "99")));
        Assert.Equal(11, Directory.GetFiles(output + "_files", "*.jpeg", SearchOption.AllDirectories).Length);
        Assert.True(File.Exists(Path.Combine(output + "_files", Marker)));
        Assert.StartsWith("<Image ", File.ReadAllText(output + ".dzi"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeepZoom_Viewer_ExistingOutput_IsReplaced()
    {
        using var temp = new TempDirectory();
        string output = temp.Combine("boxes");
        var args = new DeepZoomArguments(TestFiles.Get("boxes.tiff")) { Output = output, Viewer = true };
        Assert.Equal(0, await DeepZoomCommand.RunAsync(args, new StringWriter(), Token));
        Assert.True(File.Exists(Path.Combine(output, Marker)));
        string stale = Path.Combine(output, "old_files", "0", "0_0.jpeg");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllText(stale, "stale");
        File.WriteAllText(Path.Combine(output, "notes.txt"), "part of the earlier output now");

        int code = await DeepZoomCommand.RunAsync(args, new StringWriter(), Token);

        Assert.Equal(0, code);
        Assert.False(Directory.Exists(Path.Combine(output, "old_files")));
        Assert.False(File.Exists(Path.Combine(output, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(output, "index.html")));
        Assert.True(File.Exists(Path.Combine(output, Marker)));
    }

    [Fact]
    public async Task DeepZoom_Viewer_DoesNotReplaceForeignDirectory()
    {
        // MIRAX keeps its data in a directory named like the slide, which is also -r's default output name.
        using var temp = new TempDirectory();
        string output = temp.Combine("boxes");
        string foreign = Path.Combine(output, "Slidedat.ini");
        Directory.CreateDirectory(output);
        File.WriteAllText(foreign, "[GENERAL]");
        var stderr = new StringWriter();

        int code = await DeepZoomCommand.RunAsync(
            new DeepZoomArguments(TestFiles.Get("boxes.tiff")) { Output = output, Viewer = true }, stderr, Token);

        Assert.Equal(1, code);
        Assert.Contains(Marker, stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(new[] { foreign }, Directory.GetFileSystemEntries(output));
    }

    [Theory]
    [InlineData("deepzoom")]
    [InlineData("omezarr")]
    [InlineData("omezarr-0.4")]
    public async Task Export_Cancellation_LeavesNoPartialFiles(string command)
    {
        using var temp = new TempDirectory();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        int writes = 0;
        void Cancel(string path)
        {
            Interlocked.Increment(ref writes);
            cts.Cancel();
        }

        var stderr = new StringWriter();
        int code = command == "deepzoom"
            ? await DeepZoomCommand.RunAsync(
                new DeepZoomArguments(TestFiles.Get("boxes.tiff")) { Output = temp.Combine("boxes"), Jobs = 2, FileWritten = Cancel },
                stderr,
                cts.Token)
            : await OmeZarrCommand.RunAsync(
                new OmeZarrArguments(TestFiles.Get("boxes.tiff"))
                {
                    Output = temp.Combine("boxes.zarr"),
                    Version = command == "omezarr-0.4" ? NgffVersion.V0_4 : NgffVersion.V0_5,
                    ChunkSize = 16,
                    Jobs = 2,
                    FileWritten = Cancel,
                },
                stderr,
                cts.Token);

        Assert.Equal(130, code);

        // How many files a complete export writes, for the same arguments as above.
        using var slide = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        long total = command == "deepzoom"
            ? new DeepZoomGenerator(slide, 254, 1, limitBounds: true).TileCount
            : new OmeZarrGenerator(slide, new OmeZarrOptions
            {
                Version = command == "omezarr-0.4" ? NgffVersion.V0_4 : NgffVersion.V0_5,
                ChunkSize = 16,
            }).EnumerateKeys().LongCount();

        // Each of the two workers may finish the item it had already started, so the exact count depends on scheduling.
        // What matters is that something was written and the export stopped before writing everything.
        Assert.True(writes >= 1, $"writes: {writes}");
        Assert.True(writes < total, $"writes: {writes}, a full export writes {total}");
        string[] files = Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        Assert.DoesNotContain(files, f => f.EndsWith(".tmp", StringComparison.Ordinal));
        Assert.All(files.Where(f => Path.GetFileName(f) != Marker), f => Assert.NotEqual(0, new FileInfo(f).Length));

        // The DZI and the root metadata are written last, so an interrupted export is not mistaken for a complete one.
        Assert.False(File.Exists(temp.Combine("boxes.dzi")));
        foreach (string rootMetadata in new[] { "zarr.json", ".zgroup", ".zattrs" })
        {
            Assert.False(File.Exists(Path.Combine(temp.Combine("boxes.zarr"), rootMetadata)), rootMetadata);
        }
    }

    [Fact]
    public async Task DeepZoom_UnopenableSlide_Returns1()
    {
        using var temp = new TempDirectory();
        var stderr = new StringWriter();

        int code = await DeepZoomCommand.RunAsync(
            new DeepZoomArguments(TestFiles.Get("unopenable.tiff")) { Output = temp.Combine("out") }, stderr, Token);

        Assert.Equal(1, code);
        Assert.Contains("unopenable.tiff", stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }
}
