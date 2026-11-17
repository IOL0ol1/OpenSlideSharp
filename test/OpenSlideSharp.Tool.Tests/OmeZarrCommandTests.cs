using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenSlideSharp.SkiaSharp;
using Xunit;

namespace OpenSlideSharp.Tool.Tests;

public class OmeZarrCommandTests
{
    private const string Marker = ".openslide-sharp-output";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OmeZarr_Boxes_WritesEveryKey()
    {
        using var temp = new TempDirectory();
        string output = temp.Combine("boxes.zarr");
        var stderr = new StringWriter();

        int code = await OmeZarrCommand.RunAsync(
            new OmeZarrArguments(TestFiles.Get("boxes.tiff"))
            {
                Output = output,
                ChunkSize = 64,
                Version = NgffVersion.V0_4,
                Compression = OmeZarrCompression.Gzip,
            },
            stderr,
            Token);

        Assert.True(code == 0, stderr.ToString());
        using var slide = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        var generator = new OmeZarrGenerator(
            slide,
            new OmeZarrOptions { ChunkSize = 64, Version = NgffVersion.V0_4, Compression = OmeZarrCompression.Gzip, LimitBounds = true });
        string[] keys = generator.EnumerateKeys().ToArray();
        Assert.Contains(".zattrs", keys);
        foreach (string key in keys)
        {
            Assert.True(generator.TryGetEntry(key, out byte[]? expected));
            string file = Path.Combine(output, key.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file), key);
            Assert.Equal(expected, File.ReadAllBytes(file));
        }

        // Besides the keys, only the marker that lets a later run replace the store.
        Assert.Equal(keys.Length + 1, Directory.GetFiles(output, "*", SearchOption.AllDirectories).Length);
        Assert.True(File.Exists(Path.Combine(output, Marker)));
        Assert.Contains($"Tiling slide: {keys.Length}/{keys.Length} tiles", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OmeZarr_Associated_WritesThumbnailStore()
    {
        using var temp = new TempDirectory();
        string output = temp.Combine("small.zarr");

        int code = await OmeZarrCommand.RunAsync(
            new OmeZarrArguments(TestFiles.Get("small.svs")) { Output = output, Associated = true }, new StringWriter(), Token);

        Assert.Equal(0, code);
        Assert.True(File.Exists(Path.Combine(output, "zarr.json")));
        Assert.True(File.Exists(temp.Combine(Path.Combine("small_associated", "thumbnail.zarr", "zarr.json"))));
        Assert.True(File.Exists(temp.Combine(Path.Combine("small_associated", "thumbnail.zarr", "0", "zarr.json"))));
        Assert.True(File.Exists(temp.Combine(Path.Combine("small_associated", Marker))));
        Assert.True(File.Exists(temp.Combine(Path.Combine("small_associated", "thumbnail.zarr", Marker))));
    }

    [Fact]
    public async Task OmeZarr_ExistingOutput_IsReplaced()
    {
        using var temp = new TempDirectory();
        string output = temp.Combine("boxes.zarr");
        var args = new OmeZarrArguments(TestFiles.Get("boxes.tiff")) { Output = output };
        Assert.Equal(0, await OmeZarrCommand.RunAsync(args, new StringWriter(), Token));
        string stale = Path.Combine(output, "7", "c", "0", "0", "0");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllText(stale, "stale");
        File.WriteAllText(Path.Combine(output, "notes.txt"), "part of the earlier output now");

        int code = await OmeZarrCommand.RunAsync(args, new StringWriter(), Token);

        Assert.Equal(0, code);
        Assert.False(Directory.Exists(Path.Combine(output, "7")));
        Assert.False(File.Exists(Path.Combine(output, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(output, "zarr.json")));
        Assert.True(File.Exists(Path.Combine(output, Marker)));
    }

    [Fact]
    public async Task OmeZarr_UnmarkedNumberedDirectories_Survive()
    {
        using var temp = new TempDirectory();
        string output = temp.Combine("archive");
        string[] kept = [Path.Combine(output, "2023", "a.txt"), Path.Combine(output, "2024", "b.txt")];
        foreach (string file in kept)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "keep me");
        }

        var stderr = new StringWriter();
        int code = await OmeZarrCommand.RunAsync(
            new OmeZarrArguments(TestFiles.Get("boxes.tiff")) { Output = output }, stderr, Token);

        Assert.Equal(1, code);
        Assert.Contains(Marker, stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(kept.Order(), Directory.GetFiles(output, "*", SearchOption.AllDirectories).Order());
    }

    [Theory]
    [InlineData(NgffVersion.V0_4)]
    [InlineData(NgffVersion.V0_5)]
    public async Task OmeZarr_RootMetadata_IsWrittenLast(NgffVersion version)
    {
        using var temp = new TempDirectory();
        string output = temp.Combine("boxes.zarr");
        var written = new ConcurrentQueue<string>();

        int code = await OmeZarrCommand.RunAsync(
            new OmeZarrArguments(TestFiles.Get("boxes.tiff"))
            {
                Output = output,
                Version = version,
                ChunkSize = 64,
                Jobs = 1,
                FileWritten = path => written.Enqueue(Path.GetRelativePath(output, path).Replace(Path.DirectorySeparatorChar, '/')),
            },
            new StringWriter(),
            Token);

        Assert.Equal(0, code);
        string[] root = version == NgffVersion.V0_5 ? ["zarr.json"] : [".zgroup", ".zattrs"];
        string[] order = written.ToArray();
        Assert.Equal(root.Order(), order[^root.Length..].Order());
        Assert.DoesNotContain(order[..^root.Length], key => !key.Contains('/'));
        Assert.Contains(version == NgffVersion.V0_5 ? "0/zarr.json" : "0/.zarray", order);
    }

    [Fact]
    public async Task OmeZarr_OutputIsSlide_IsKept()
    {
        using var temp = new TempDirectory();
        string slide = temp.Combine("x.tiff");
        File.Copy(TestFiles.Get("boxes.tiff"), slide);
        var stderr = new StringWriter();

        int code = await OmeZarrCommand.RunAsync(new OmeZarrArguments(slide) { Output = slide }, stderr, Token);

        Assert.Equal(1, code);
        Assert.Contains("x.tiff", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(File.ReadAllBytes(TestFiles.Get("boxes.tiff")), File.ReadAllBytes(slide));
        Assert.Equal(new[] { slide }, Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task OmeZarr_DoesNotReplaceForeignDirectory()
    {
        using var temp = new TempDirectory();
        string output = temp.Combine("data");
        string foreign = Path.Combine(output, "notes.txt");
        Directory.CreateDirectory(output);
        File.WriteAllText(foreign, "keep me");
        var stderr = new StringWriter();

        int code = await OmeZarrCommand.RunAsync(
            new OmeZarrArguments(TestFiles.Get("boxes.tiff")) { Output = output }, stderr, Token);

        Assert.Equal(1, code);
        Assert.Contains(Marker, stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(new[] { foreign }, Directory.GetFileSystemEntries(output));
    }

    [Fact]
    public async Task OmeZarr_UnopenableSlide_Returns1()
    {
        using var temp = new TempDirectory();
        var stderr = new StringWriter();

        int code = await OmeZarrCommand.RunAsync(
            new OmeZarrArguments(TestFiles.Get("unopenable.tiff")) { Output = temp.Combine("out.zarr") }, stderr, Token);

        Assert.Equal(1, code);
        Assert.Contains("unopenable.tiff", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Program_ParsesArguments()
    {
        Assert.Equal(0, Program.Main(["deepzoom", "--help"]));
        Assert.Equal(2, Program.Main(["deepzoom"]));
        Assert.Equal(0, Program.Main(["omezarr", "--help"]));
        Assert.Equal(2, Program.Main(["omezarr"]));
        Assert.Equal(2, Program.Main(["deepzoom", "slide.svs", "-f", "gif"]));
        Assert.Equal(2, Program.Main(["deepzoom", "slide.svs", "-s", "0"]));
        Assert.Equal(2, Program.Main(["deepzoom", "slide.svs", "-s", "abc"]));
        Assert.Equal(2, Program.Main(["deepzoom", "slide.svs", "-Q", "101"]));
        Assert.Equal(2, Program.Main(["deepzoom", "slide.svs", "-j", "0"]));
        Assert.Equal(2, Program.Main(["omezarr", "slide.svs", "--ngff", "0.3"]));
        Assert.Equal(2, Program.Main(["omezarr", "slide.svs", "-c", "embed"]));
        Assert.Equal(2, Program.Main(["omezarr", "slide.svs", "--chunk", "8193"]));
        Assert.Equal(2, Program.Main(["omezarr", "slide.svs", "--gzip-level", "10"]));
        Assert.Equal(2, Program.Main(["bogus"]));
    }

    [Fact]
    public void Program_BindsOptions()
    {
        DeepZoomArguments deepZoom = Program.BindDeepZoom(Program.Parse(
            ["deepzoom", "a.svs", "-o", "out", "-s", "510", "-e", "2", "-f", "png", "-Q", "70", "-B", "-c", "embed", "-j", "3", "-r"]));
        Assert.Equal(
            new DeepZoomArguments("a.svs")
            {
                Output = "out",
                TileSize = 510,
                Overlap = 2,
                Format = ImageFormat.Png,
                Quality = 70,
                LimitBounds = false,
                ColorMode = ColorMode.Embed,
                Jobs = 3,
                Viewer = true,
            },
            deepZoom);

        DeepZoomArguments defaults = Program.BindDeepZoom(Program.Parse(["deepzoom", "a.svs"]));
        Assert.Equal(new DeepZoomArguments("a.svs"), defaults);
        Assert.Equal(254, defaults.TileSize);
        Assert.Equal(1, defaults.Overlap);
        Assert.Equal(90, defaults.Quality);
        Assert.True(defaults.LimitBounds);
        Assert.Equal(Environment.ProcessorCount, defaults.Jobs);

        OmeZarrArguments omeZarr = Program.BindOmeZarr(Program.Parse(
            ["omezarr", "a.svs", "-o", "x.zarr", "--ngff", "0.4", "--chunk", "256", "--compression", "gzip", "--gzip-level", "9", "-B", "-c", "ignore", "-j", "5", "--associated"]));
        Assert.Equal(
            new OmeZarrArguments("a.svs")
            {
                Output = "x.zarr",
                Version = NgffVersion.V0_4,
                ChunkSize = 256,
                Compression = OmeZarrCompression.Gzip,
                GzipLevel = 9,
                LimitBounds = false,
                ColorMode = ColorMode.Ignore,
                Jobs = 5,
                Associated = true,
            },
            omeZarr);
        Assert.Equal(new OmeZarrArguments("a.svs"), Program.BindOmeZarr(Program.Parse(["omezarr", "a.svs"])));
    }
}
