using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using OpenSlideSharp.Tool.Export;
using Xunit;

namespace OpenSlideSharp.Tool.Tests;

/// <summary>An export never deletes or overwrites the slide, or a directory it did not write.</summary>
public class OutputSafetyTests
{
    private const string Marker = ".openslide-sharp-output";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static byte[] Boxes => File.ReadAllBytes(TestFiles.Get("boxes.tiff"));

    [Fact]
    public async Task DeepZoom_Viewer_OutputIsSlide_IsKept()
    {
        using var temp = new TempDirectory();
        string slide = temp.Combine("x.tiff");
        File.Copy(TestFiles.Get("boxes.tiff"), slide);
        var stderr = new StringWriter();

        int code = await DeepZoomCommand.RunAsync(new DeepZoomArguments(slide) { Output = slide, Viewer = true }, stderr, Token);

        Assert.Equal(1, code);
        Assert.Contains("x.tiff", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("a file has that name", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(Boxes, File.ReadAllBytes(slide));
        Assert.Equal(new[] { slide }, Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task DeepZoom_DziIsSlide_IsKept()
    {
        // A slide named like the DZI the export would write.
        using var temp = new TempDirectory();
        string slide = temp.Combine("boxes.dzi");
        File.Copy(TestFiles.Get("boxes.tiff"), slide);
        var stderr = new StringWriter();

        int code = await DeepZoomCommand.RunAsync(
            new DeepZoomArguments(slide) { Output = temp.Combine("boxes") }, stderr, Token);

        Assert.Equal(1, code);
        Assert.Contains("it is the slide", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(Boxes, File.ReadAllBytes(slide));
        Assert.Equal(new[] { slide }, Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task OmeZarr_OutputIsAssociatedDirectory_IsRefused()
    {
        // --associated writes to <output>/../<stem>_associated: with that very directory as the output, replacing it
        // for the associated images would delete the store just written.
        using var temp = new TempDirectory();
        var outputs = new List<string> { temp.Combine("small_associated") };
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            outputs.Add(temp.Combine("SMALL_Associated")); // the same directory on these file systems
        }

        foreach (string output in outputs)
        {
            var stderr = new StringWriter();
            int code = await OmeZarrCommand.RunAsync(
                new OmeZarrArguments(TestFiles.Get("small.svs")) { Output = output, Associated = true }, stderr, Token);

            Assert.Equal(1, code);
            Assert.Contains("is also the directory of the associated images", stderr.ToString(), StringComparison.Ordinal);
            Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
        }
    }

    [Theory]
    [InlineData("omezarr")]
    [InlineData("deepzoom")]
    [InlineData("deepzoom -r")]
    public async Task Output_IsSymlinkToMarkedDirectory_IsRefused(string command)
    {
        // Replacing the output moves it aside and deletes it: through a link, that would delete the files of the
        // directory it points to, even though the marker there says the tool wrote them.
        using var temp = new TempDirectory();
        string target = temp.Combine("target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, Marker), string.Empty);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "not the tool's");
        string slide = TestFiles.Get("boxes.tiff");
        string output = temp.Combine("boxes");
        string link = command switch
        {
            "omezarr" => temp.Combine("boxes.zarr"),
            "deepzoom" => output + "_files",
            _ => output,
        };
        CreateDirectoryLinkOrSkip(link, target);
        string[] before = Snapshot(target);
        var stderr = new StringWriter();

        int code = command == "omezarr"
            ? await OmeZarrCommand.RunAsync(new OmeZarrArguments(slide) { Output = link }, stderr, Token)
            : await DeepZoomCommand.RunAsync(new DeepZoomArguments(slide) { Output = output, Viewer = command == "deepzoom -r" }, stderr, Token);

        Assert.Equal(1, code);
        Assert.Contains("is a link", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(target));
        Assert.NotNull(new DirectoryInfo(link).LinkTarget);
        Assert.Equal(new[] { link, target }.Order(StringComparer.Ordinal), Directory.GetFileSystemEntries(temp.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task DeepZoom_OldDzi_IsDeletedBeforeAnyTile()
    {
        using var temp = new TempDirectory();
        string output = temp.Combine("boxes");
        Assert.Equal(0, await DeepZoomCommand.RunAsync(new DeepZoomArguments(TestFiles.Get("boxes.tiff")) { Output = output }, new StringWriter(), Token));

        bool? dziAtFirstTile = null;
        var args = new DeepZoomArguments(TestFiles.Get("boxes.tiff"))
        {
            Output = output,
            Jobs = 1,
            FileWritten = path =>
            {
                if (path.EndsWith(".jpeg", StringComparison.Ordinal))
                {
                    dziAtFirstTile ??= File.Exists(output + ".dzi");
                }
            },
        };

        Assert.Equal(0, await DeepZoomCommand.RunAsync(args, new StringWriter(), Token));
        Assert.False(dziAtFirstTile);
        Assert.True(File.Exists(output + ".dzi"));
    }

    [Fact]
    public async Task DeepZoom_LockedOldTile_FailsUntouchedOrWarns_AndNextRunWorks()
    {
        using var temp = new TempDirectory();
        string output = temp.Combine("boxes");
        string files = output + "_files";
        var args = new DeepZoomArguments(TestFiles.Get("boxes.tiff")) { Output = output };
        Assert.Equal(0, await DeepZoomCommand.RunAsync(args, new StringWriter(), Token));
        string[] before = Snapshot(temp.Path);
        string dzi = File.ReadAllText(output + ".dzi");

        var stderr = new StringWriter();
        int code;
        using (Lock(Path.Combine(files, "9")))
        {
            code = await DeepZoomCommand.RunAsync(args, stderr, Token);

            if (code == 1)
            {
                // The old output could not be moved aside (Windows: a file in it is open): nothing was touched.
                Assert.Equal(before, Snapshot(temp.Path));
                Assert.Equal(dzi, File.ReadAllText(output + ".dzi"));
            }
            else
            {
                // It was moved aside and the export went on. What could not be deleted is named in a warning.
                Assert.Equal(0, code);
                Assert.Equal(11, Directory.GetFiles(files, "*.jpeg", SearchOption.AllDirectories).Length);
                foreach (string leftover in Leftovers(temp.Path))
                {
                    Assert.Contains(leftover, stderr.ToString(), StringComparison.Ordinal);
                }
            }
        }

        // Once the lock is gone, the next run works without any manual cleanup, and removes what was left.
        var again = new StringWriter();
        Assert.True(await DeepZoomCommand.RunAsync(args, again, Token) == 0, again.ToString());
        Assert.Equal(11, Directory.GetFiles(files, "*.jpeg", SearchOption.AllDirectories).Length);
        Assert.True(File.Exists(Path.Combine(files, Marker)));
        Assert.Equal(dzi, File.ReadAllText(output + ".dzi"));
        Assert.Empty(Leftovers(temp.Path));
    }

    [Fact]
    public async Task Leftovers_AreCleanedUpOnlyWithMarker()
    {
        using var temp = new TempDirectory();
        string output = temp.Combine("boxes.zarr");
        string marked = temp.Combine(".boxes.zarr.openslide-sharp-old-0123");
        string unmarked = temp.Combine(".boxes.zarr.openslide-sharp-old-4567");
        string other = temp.Combine(".other.zarr.openslide-sharp-old-89ab");
        foreach (string directory in new[] { marked, unmarked, other })
        {
            Directory.CreateDirectory(Path.Combine(directory, "0", "c"));
            File.WriteAllText(Path.Combine(directory, "0", "c", "0"), "old chunk");
        }

        File.WriteAllText(Path.Combine(marked, Marker), string.Empty);
        File.WriteAllText(Path.Combine(other, Marker), string.Empty);

        int code = await OmeZarrCommand.RunAsync(new OmeZarrArguments(TestFiles.Get("boxes.tiff")) { Output = output }, new StringWriter(), Token);

        Assert.Equal(0, code);
        Assert.False(Directory.Exists(marked));
        Assert.True(File.Exists(Path.Combine(unmarked, "0", "c", "0"))); // not ours: no marker
        Assert.True(File.Exists(Path.Combine(other, "0", "c", "0")));    // another output's
    }

    [Theory]
    [InlineData(false, 30_000, 1)]
    [InlineData(true, 30_000, 1)]
    [InlineData(false, 254, 600_000_000)]
    [InlineData(true, 254, 600_000_000)]
    public async Task DeepZoom_TileTooLarge_IsRefusedBeforeTouchingOutput(bool viewer, int tileSize, int overlap)
    {
        // A tile that needs more bytes than a .NET array can hold: a 30000-pixel tile of a 40000 x 40000 slide, or
        // the boxes slide with a huge overlap. The old output is replaced only after every image's tiles are known to fit.
        using var temp = new TempDirectory();
        string output = temp.Combine("out");
        Assert.Equal(0, await DeepZoomCommand.RunAsync(
            new DeepZoomArguments(TestFiles.Get("boxes.tiff")) { Output = output, Viewer = viewer }, new StringWriter(), Token));
        string slide = TestFiles.Get("boxes.tiff");
        if (tileSize > 254)
        {
            slide = temp.Combine("large.tiff");
            WriteBlankTiledTiff(slide, 40_000, 40_000);
        }

        string[] before = Snapshot(temp.Path);
        var stderr = new StringWriter();

        int code = await DeepZoomCommand.RunAsync(
            new DeepZoomArguments(slide) { Output = output, Viewer = viewer, TileSize = tileSize, Overlap = overlap }, stderr, Token);

        Assert.Equal(2, code);
        Assert.Contains($"--size {tileSize} and --overlap {overlap} make a ", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("Choose a smaller -s/--size or -e/--overlap.", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Parameter", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(temp.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeepZoom_SmallSlideLargeTileSize_Works(bool viewer)
    {
        // Tiles never extend past their level: with a tile size larger than the slide each level is one small tile.
        using var temp = new TempDirectory();
        string output = temp.Combine("boxes");
        var stderr = new StringWriter();

        int code = await DeepZoomCommand.RunAsync(
            new DeepZoomArguments(TestFiles.Get("boxes.tiff")) { Output = output, Viewer = viewer, TileSize = 600_000_000 }, stderr, Token);

        Assert.True(code == 0, stderr.ToString());
        string files = viewer ? Path.Combine(output, "slide_files") : output + "_files";
        Assert.Equal(10, Directory.GetFiles(files, "*.jpeg", SearchOption.AllDirectories).Length);
        Assert.Contains("TileSize=\"600000000\"", File.ReadAllText(viewer ? Path.Combine(output, "slide.dzi") : output + ".dzi"), StringComparison.Ordinal);
    }

    // An uncompressed tiled RGB TIFF whose 256 x 256 tiles all share one blank tile's bytes: a large slide in a small
    // file, which OpenSlide opens as a generic TIFF.
    private static void WriteBlankTiledTiff(string path, int width, int height)
    {
        const int Tile = 256;
        int tiles = checked(((width + Tile - 1) / Tile) * ((height + Tile - 1) / Tile));
        const int TileBytes = Tile * Tile * 3;
        (ushort Tag, ushort Type, int Count, int Value)[] entries =
        [
            (256, 4, 1, width),          // ImageWidth
            (257, 4, 1, height),         // ImageLength
            (258, 3, 3, 0),              // BitsPerSample 8,8,8 (at bitsAt)
            (259, 3, 1, 1),              // Compression: none
            (262, 3, 1, 2),              // PhotometricInterpretation: RGB
            (277, 3, 1, 3),              // SamplesPerPixel
            (284, 3, 1, 1),              // PlanarConfiguration: contiguous
            (322, 3, 1, Tile),           // TileWidth
            (323, 3, 1, Tile),           // TileLength
            (324, 4, tiles, 0),          // TileOffsets (at offsetsAt)
            (325, 4, tiles, 0),          // TileByteCounts (at countsAt)
        ];
        int bitsAt = 8 + 2 + (entries.Length * 12) + 4;
        int tileAt = bitsAt + 6;
        int offsetsAt = tileAt + TileBytes;
        int countsAt = offsetsAt + (tiles * 4);

        using var file = new BinaryWriter(File.Create(path));
        file.Write("II"u8);
        file.Write((ushort)42);
        file.Write(8);
        file.Write((ushort)entries.Length);
        foreach ((ushort tag, ushort type, int count, int value) in entries)
        {
            file.Write(tag);
            file.Write(type);
            file.Write(count);
            int field = tag switch { 258 => bitsAt, 324 => offsetsAt, 325 => countsAt, _ => value };
            if (type == 3 && count == 1)
            {
                file.Write((ushort)field);
                file.Write((ushort)0);
            }
            else
            {
                file.Write(field);
            }
        }

        file.Write(0); // no next IFD
        file.Write(new ushort[] { 8, 8, 8 }.SelectMany(BitConverter.GetBytes).ToArray());
        file.Write(new byte[TileBytes]);
        for (int i = 0; i < tiles; i++)
        {
            file.Write(tileAt);
        }

        for (int i = 0; i < tiles; i++)
        {
            file.Write(TileBytes);
        }
    }

    // Every entry below the directory, with a hash of each file's content.
    private static string[] Snapshot(string directory) =>
        Directory.GetFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .Select(p => p + (File.Exists(p) ? " " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) : "/"))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] Leftovers(string directory) =>
        Directory.GetDirectories(directory, "*.openslide-sharp-old-*");

    // A symbolic link to a directory; on Windows without the right to create one (Developer Mode or an elevated
    // process), a directory junction, which the tool must treat the same way. Skips only if neither can be made.
    private static void CreateDirectoryLinkOrSkip(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (OperatingSystem.IsWindows() && TryCreateJunction(link, target))
            {
                return;
            }

            Assert.Skip($"Cannot create a symbolic link here: {e.Message}");
        }
    }

    private static bool TryCreateJunction(string link, string target)
    {
        var info = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("/c");
        info.ArgumentList.Add("mklink");
        info.ArgumentList.Add("/J");
        info.ArgumentList.Add(link);
        info.ArgumentList.Add(target);
        using Process process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    // Keeps a level directory from being deleted: a tile held open on Windows, or no write permission elsewhere
    // (which root ignores; the test then sees a plain success).
    private static IDisposable Lock(string level) =>
        OperatingSystem.IsWindows()
            ? new FileStream(Directory.GetFiles(level)[0], FileMode.Open, FileAccess.Read, FileShare.Read)
            : new ReadOnlyDirectory(level);

    [UnsupportedOSPlatform("windows")]
    private sealed class ReadOnlyDirectory : IDisposable
    {
        private readonly string _level;
        private readonly UnixFileMode _mode;

        public ReadOnlyDirectory(string level)
        {
            _level = level;
            _mode = File.GetUnixFileMode(level);
            File.SetUnixFileMode(level, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        // Gives write permission back to the directory, wherever the export moved it.
        public void Dispose()
        {
            string temp = Path.GetDirectoryName(Path.GetDirectoryName(_level)!)!;
            foreach (string leftover in Leftovers(temp))
            {
                foreach (string directory in Directory.GetDirectories(leftover, "*", SearchOption.AllDirectories))
                {
                    File.SetUnixFileMode(directory, _mode);
                }
            }

            if (Directory.Exists(_level))
            {
                File.SetUnixFileMode(_level, _mode);
            }
        }
    }

    [Fact]
    public async Task DeepZoom_Viewer_EmptyDirectory_IsUsed()
    {
        using var temp = new TempDirectory();
        string output = temp.Combine("viewer");
        Directory.CreateDirectory(output);

        int code = await DeepZoomCommand.RunAsync(
            new DeepZoomArguments(TestFiles.Get("boxes.tiff")) { Output = output, Viewer = true }, new StringWriter(), Token);

        Assert.Equal(0, code);
        Assert.True(File.Exists(Path.Combine(output, Marker)));
        Assert.True(File.Exists(Path.Combine(output, "index.html")));
    }

    [Fact]
    public void PrepareDirectory_RefusesMiraxDataDirectory()
    {
        // MIRAX keeps name.mrxs next to a name/ directory of data; even a marker there must not get it deleted.
        using var temp = new TempDirectory();
        string slide = temp.Combine("CMU-1.mrxs");
        File.WriteAllText(slide, "index");
        string data = temp.Combine("CMU-1");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "Slidedat.ini"), "[GENERAL]");
        File.WriteAllText(Path.Combine(data, Marker), string.Empty);

        Assert.Throws<IOException>(() => ExportSupport.PrepareDirectory(data, slide, TextWriter.Null));

        Assert.True(File.Exists(Path.Combine(data, "Slidedat.ini")));
    }

    [Fact]
    public void PrepareDirectory_RefusesDirectoryHoldingSlide()
    {
        using var temp = new TempDirectory();
        string output = temp.Combine("out");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, Marker), string.Empty);
        string slide = Path.Combine(output, "slide.tiff");
        File.Copy(TestFiles.Get("boxes.tiff"), slide);

        Assert.Throws<IOException>(() => ExportSupport.PrepareDirectory(output, slide, TextWriter.Null));

        Assert.Equal(Boxes, File.ReadAllBytes(slide));
    }
}

/// <summary>Tests that change the process's current directory, so they run alone.</summary>
[CollectionDefinition(nameof(CurrentDirectoryCollection), DisableParallelization = true)]
public sealed class CurrentDirectoryCollection
{
}

[Collection(nameof(CurrentDirectoryCollection))]
public class CurrentDirectoryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DeepZoom_Viewer_ExtensionlessSlide_IsKept()
    {
        // -r names its directory after the slide without its extension: for an extensionless slide, the slide itself.
        using var temp = new TempDirectory();
        string slide = temp.Combine("boxes");
        File.Copy(TestFiles.Get("boxes.tiff"), slide);
        string previous = Environment.CurrentDirectory;
        var stderr = new StringWriter();
        int code;
        try
        {
            Environment.CurrentDirectory = temp.Path;
            code = await DeepZoomCommand.RunAsync(new DeepZoomArguments("boxes") { Viewer = true }, stderr, Token);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }

        Assert.Equal(1, code);
        Assert.Contains("boxes", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("a file has that name", stderr.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(slide));
        Assert.Equal(File.ReadAllBytes(TestFiles.Get("boxes.tiff")), File.ReadAllBytes(slide));
        Assert.Equal(new[] { slide }, Directory.GetFileSystemEntries(temp.Path));
    }
}
