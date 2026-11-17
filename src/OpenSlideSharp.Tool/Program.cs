using System;
using System.Collections.Generic;
using System.Globalization;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using OpenSlideSharp.SkiaSharp;
using OpenSlideSharp.Tool.Export;

namespace OpenSlideSharp.Tool;

/// <summary>
/// The <c>openslide-sharp</c> command line: parses the arguments and hands them to <see cref="DeepZoomCommand"/> or
/// <see cref="OmeZarrCommand"/>. Exit codes: 0 on success, 1 on failure, 2 for invalid arguments, 130 when cancelled.
/// </summary>
internal static class Program
{
    // On Ctrl+C the commands finish the files they are writing and return 130; this is how long they may take.
    private static readonly TimeSpan TerminationTimeout = TimeSpan.FromSeconds(10);

    // deepzoom, with the options of openslide-python's deepzoom_tile.py.
    private static readonly Argument<string> DeepZoomSlide = new("slide") { Description = "The slide file." };

    private static readonly Option<string?> DeepZoomOutput = new("--output", "-o")
    {
        HelpName = "name",
        Description = "Base name of the output (default: the slide file name without its extension, in the current directory).",
    };

    private static readonly Option<int> TileSize = AtLeast(
        new Option<int>("--size", "-s") { HelpName = "pixels", Description = "Tile size.", DefaultValueFactory = _ => 254 }, 1);

    private static readonly Option<int> Overlap = AtLeast(
        new Option<int>("--overlap", "-e") { HelpName = "pixels", Description = "Overlap of adjacent tiles.", DefaultValueFactory = _ => 1 }, 0);

    private static readonly Option<string> Format = new Option<string>("--format", "-f")
    {
        Description = "Image format of the tiles.",
        DefaultValueFactory = _ => "jpeg",
    }.AcceptOnly("jpeg", "png");

    private static readonly Option<int> Quality = InRange(
        new Option<int>("--quality", "-Q") { HelpName = "quality", Description = "JPEG compression quality.", DefaultValueFactory = _ => 90 }, 0, 100);

    private static readonly Option<bool> DeepZoomIgnoreBounds = IgnoreBoundsOption();

    private static readonly Option<string> DeepZoomColorMode = new Option<string>("--color-mode", "-c")
    {
        Description = "Convert tiles to sRGB with the ICC profile (default), embed the profile in the tiles (embed), or drop it (ignore).",
        DefaultValueFactory = _ => "default",
    }.AcceptOnly("default", "embed", "ignore");

    private static readonly Option<int> DeepZoomJobs = JobsOption();

    private static readonly Option<bool> Viewer = new("--viewer", "-r")
    {
        Description = "Write a directory with the slide, its associated images and an HTML viewer.",
    };

    // omezarr
    private static readonly Argument<string> OmeZarrSlide = new("slide") { Description = "The slide file." };

    private static readonly Option<string?> OmeZarrOutput = new("--output", "-o")
    {
        HelpName = "dir",
        Description = "Output directory (default: the slide file name without its extension plus .zarr, in the current directory).",
    };

    private static readonly Option<string> Ngff = new Option<string>("--ngff")
    {
        Description = "OME-NGFF version: 0.5 (Zarr v3) or 0.4 (Zarr v2).",
        DefaultValueFactory = _ => "0.5",
    }.AcceptOnly("0.5", "0.4");

    private static readonly Option<int> ChunkSize = InRange(
        new Option<int>("--chunk") { HelpName = "pixels", Description = "Chunk width and height.", DefaultValueFactory = _ => 512 }, 16, 8192);

    private static readonly Option<string> Compression = new Option<string>("--compression")
    {
        Description = "Chunk compression.",
        DefaultValueFactory = _ => "none",
    }.AcceptOnly("none", "gzip");

    private static readonly Option<int> GzipLevel = InRange(
        new Option<int>("--gzip-level") { HelpName = "level", Description = "gzip level, with --compression gzip.", DefaultValueFactory = _ => 6 }, 0, 9);

    private static readonly Option<bool> OmeZarrIgnoreBounds = IgnoreBoundsOption();

    private static readonly Option<string> OmeZarrColorMode = new Option<string>("--color-mode", "-c")
    {
        Description = "Convert pixels to sRGB with the ICC profile (default), or leave them as they are (ignore).",
        DefaultValueFactory = _ => "default",
    }.AcceptOnly("default", "ignore");

    private static readonly Option<int> OmeZarrJobs = JobsOption();

    private static readonly Option<bool> Associated = new("--associated")
    {
        Description = "Also export the associated images, to <output>/../<slide name>_associated/<image>.zarr.",
    };

    private static readonly RootCommand Root = CreateRootCommand();

    /// <summary>Runs the tool.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>The exit code.</returns>
    public static int Main(string[] args)
    {
        ParseResult result = Parse(args);
        int exitCode = result.Invoke(new InvocationConfiguration { ProcessTerminationTimeout = TerminationTimeout });

        // System.CommandLine prints the errors and help, and returns 1; the tool returns 2, as Python's argparse does.
        return result.Action is ParseErrorAction ? ExportSupport.InvalidArguments : exitCode;
    }

    internal static ParseResult Parse(IReadOnlyList<string> args) => Root.Parse(args);

    internal static DeepZoomArguments BindDeepZoom(ParseResult result) => new(result.GetRequiredValue(DeepZoomSlide))
    {
        Output = result.GetValue(DeepZoomOutput),
        TileSize = result.GetValue(TileSize),
        Overlap = result.GetValue(Overlap),
        Format = result.GetValue(Format) == "png" ? ImageFormat.Png : ImageFormat.Jpeg,
        Quality = result.GetValue(Quality),
        LimitBounds = !result.GetValue(DeepZoomIgnoreBounds),
        ColorMode = ParseColorMode(result.GetValue(DeepZoomColorMode)),
        Jobs = result.GetValue(DeepZoomJobs),
        Viewer = result.GetValue(Viewer),
    };

    internal static OmeZarrArguments BindOmeZarr(ParseResult result) => new(result.GetRequiredValue(OmeZarrSlide))
    {
        Output = result.GetValue(OmeZarrOutput),
        Version = result.GetValue(Ngff) == "0.4" ? NgffVersion.V0_4 : NgffVersion.V0_5,
        ChunkSize = result.GetValue(ChunkSize),
        Compression = result.GetValue(Compression) == "gzip" ? OmeZarrCompression.Gzip : OmeZarrCompression.None,
        GzipLevel = result.GetValue(GzipLevel),
        LimitBounds = !result.GetValue(OmeZarrIgnoreBounds),
        ColorMode = ParseColorMode(result.GetValue(OmeZarrColorMode)),
        Jobs = result.GetValue(OmeZarrJobs),
        Associated = result.GetValue(Associated),
    };

    private static RootCommand CreateRootCommand()
    {
        var deepZoom = new Command("deepzoom", "Export a slide as Deep Zoom tiles, like openslide-python's deepzoom_tile.py.")
        {
            DeepZoomSlide, DeepZoomOutput, TileSize, Overlap, Format, Quality, DeepZoomIgnoreBounds, DeepZoomColorMode, DeepZoomJobs, Viewer,
        };
        deepZoom.SetAction((result, ct) => DeepZoomCommand.RunAsync(BindDeepZoom(result), result.InvocationConfiguration.Error, ct));

        var omeZarr = new Command("omezarr", "Export a slide as an OME-Zarr store.")
        {
            OmeZarrSlide, OmeZarrOutput, Ngff, ChunkSize, Compression, GzipLevel, OmeZarrIgnoreBounds, OmeZarrColorMode, OmeZarrJobs, Associated,
        };
        omeZarr.SetAction((result, ct) => OmeZarrCommand.RunAsync(BindOmeZarr(result), result.InvocationConfiguration.Error, ct));

        var root = new RootCommand("Export OpenSlide slides as Deep Zoom or OME-Zarr.") { deepZoom, omeZarr };
        root.HelpName = "openslide-sharp";
        return root;
    }

    private static Option<bool> IgnoreBoundsOption() => new("--ignore-bounds", "-B")
    {
        Description = "Export the entire scan area, not only the non-empty region of the slide.",
    };

    private static Option<int> JobsOption() => AtLeast(
        new Option<int>("--jobs", "-j")
        {
            HelpName = "count",
            Description = "Number of worker threads.",
            DefaultValueFactory = _ => Environment.ProcessorCount,
        },
        1);

    private static ColorMode ParseColorMode(string? value) => value switch
    {
        "embed" => ColorMode.Embed,
        "ignore" => ColorMode.Ignore,
        _ => ColorMode.Default,
    };

    private static Option<string> AcceptOnly(this Option<string> option, params string[] values)
    {
        option.AcceptOnlyFromAmong(values);
        return option;
    }

    private static Option<int> AtLeast(Option<int> option, int minimum) => InRange(option, minimum, int.MaxValue);

    private static Option<int> InRange(Option<int> option, int minimum, int maximum)
    {
        option.Validators.Add(result =>
        {
            // Only a given value needs checking (defaults are valid), and only one that is a number: System.CommandLine
            // reports the others itself, and GetValueOrDefault would throw for them.
            if (result.Tokens.Count > 0
                && int.TryParse(result.Tokens[^1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                && (value < minimum || value > maximum))
            {
                result.AddError(maximum == int.MaxValue
                    ? $"{option.Name} must be at least {minimum}."
                    : $"{option.Name} must be from {minimum} to {maximum}.");
            }
        });
        return option;
    }
}
