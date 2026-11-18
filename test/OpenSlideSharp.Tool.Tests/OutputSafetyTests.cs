using System;
using System.Collections.Generic;
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

    // Every entry below the directory, with a hash of each file's content.
    private static string[] Snapshot(string directory) =>
        Directory.GetFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .Select(p => p + (File.Exists(p) ? " " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) : "/"))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] Leftovers(string directory) =>
        Directory.GetDirectories(directory, "*.openslide-sharp-old-*");

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
