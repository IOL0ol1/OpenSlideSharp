using System;
using System.Collections.Generic;
using System.IO;
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

        Assert.Throws<IOException>(() => ExportSupport.PrepareDirectory(data, slide));

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

        Assert.Throws<IOException>(() => ExportSupport.PrepareDirectory(output, slide));

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
