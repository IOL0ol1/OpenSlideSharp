using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenSlideSharp.SkiaSharp;
using OpenSlideSharp.Tool.Export;

namespace OpenSlideSharp.Tool;

/// <summary>The options of <c>openslide-sharp deepzoom</c>, with the defaults of deepzoom_tile.py.</summary>
/// <param name="Slide">The slide file.</param>
internal sealed record DeepZoomArguments(string Slide)
{
    /// <summary>The output base name; null for the slide file name without its extension, in the current directory.</summary>
    public string? Output { get; init; }

    public int TileSize { get; init; } = 254;

    public int Overlap { get; init; } = 1;

    public ImageFormat Format { get; init; } = ImageFormat.Jpeg;

    public int Quality { get; init; } = 90;

    /// <summary>False with <c>-B</c>: the entire scan area rather than the non-empty region.</summary>
    public bool LimitBounds { get; init; } = true;

    public ColorMode ColorMode { get; init; } = ColorMode.Default;

    public int Jobs { get; init; } = Environment.ProcessorCount;

    /// <summary><c>-r</c>: write a directory with the slide, its associated images and an HTML viewer.</summary>
    public bool Viewer { get; init; }

    /// <summary>Called with the path of each file written; a test hook.</summary>
    public Action<string>? FileWritten { get; init; }
}

/// <summary>
/// <c>openslide-sharp deepzoom</c>: writes <c>&lt;name&gt;.dzi</c> and <c>&lt;name&gt;_files/{level}/{col}_{row}.{format}</c>
/// like openslide-python's deepzoom_tile.py, or with <c>-r</c> a directory <c>&lt;name&gt;/</c> with the slide,
/// each associated image and an HTML viewer.
/// </summary>
internal static class DeepZoomCommand
{
    /// <summary>Runs the export.</summary>
    /// <returns>0 on success, 1 if the slide cannot be opened or the export fails, 2 for invalid arguments, 130 if canceled.</returns>
    internal static async Task<int> RunAsync(DeepZoomArguments args, TextWriter stderr, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stderr);

        if (Validate(args) is { } error)
        {
            stderr.WriteLine($"openslide-sharp: {error}");
            return ExportSupport.InvalidArguments;
        }

        if (!ExportSupport.TryOpen(args.Slide, stderr, out OpenSlide? slide))
        {
            return ExportSupport.Failure;
        }

        using (slide)
        {
            try
            {
                if (CheckTileSizes(slide, args) is { } tooLarge)
                {
                    stderr.WriteLine($"openslide-sharp: {tooLarge}");
                    return ExportSupport.InvalidArguments;
                }

                await ExportAsync(slide, args, stderr, ct).ConfigureAwait(false);
                return ExportSupport.Success;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                stderr.WriteLine("openslide-sharp: canceled.");
                return ExportSupport.Canceled;
            }
            catch (Exception ex) when (ExportSupport.IsExportError(ex))
            {
                stderr.WriteLine($"openslide-sharp: {ex.Message}");
                return ExportSupport.Failure;
            }
        }
    }

    private static string? Validate(DeepZoomArguments args)
    {
        if (string.IsNullOrWhiteSpace(args.Slide))
        {
            return "The slide path is empty.";
        }

        if (args.Output is not null && string.IsNullOrWhiteSpace(args.Output))
        {
            return "The output name is empty.";
        }

        if (args.TileSize < 1 || args.Overlap < 0 || args.Quality < 0 || args.Quality > 100 || args.Jobs < 1)
        {
            return "The tile size and jobs must be at least 1, the overlap at least 0, and the quality from 0 to 100.";
        }

        return null;
    }

    // DeepZoomGenerator accepts any tile size and overlap and lets GetTile reject a tile, or the slide region read
    // for it, that needs more bytes than a .NET array can hold. Here that would happen after the old output was
    // moved aside and deleted, so every image the export writes is checked first, with the same rule. Large values
    // alone are fine: a tile never extends past its level, so a small slide with a large -s has small tiles. A
    // tile's width and its region's width depend only on its column: all interior columns are TileSize + 2 *
    // Overlap wide, and their regions only shrink to the right (they are clipped to the level), so the widest are
    // in column 0, 1 or the last one. Likewise for rows.
    private static string? CheckTileSizes(OpenSlide slide, DeepZoomArguments args)
    {
        var images = new List<(DeepZoomGenerator Generator, string Name)>
        {
            (new DeepZoomGenerator(slide, args.TileSize, args.Overlap, args.LimitBounds), ExportSupport.Describe(args.Slide, null)),
        };
        if (args.Viewer)
        {
            foreach (KeyValuePair<string, AssociatedImage> image in slide.AssociatedImages)
            {
                images.Add((new DeepZoomGenerator(image.Value, args.TileSize, args.Overlap), ExportSupport.Describe(args.Slide, image.Key)));
            }
        }

        foreach ((DeepZoomGenerator generator, string name) in images)
        {
            for (int level = 0; level < generator.LevelCount; level++)
            {
                TileGridSize grid = generator.LevelTiles[level];
                foreach (long column in Widest(grid.Columns))
                {
                    foreach (long row in Widest(grid.Rows))
                    {
                        SlideSize tile = generator.GetTileDimensions(level, column, row);
                        TileRegion region = generator.GetTileCoordinates(level, column, row);
                        string? what = TooLarge(tile.Width, tile.Height) ? $"a {tile.Width} x {tile.Height} tile at level {level} of {name}"
                            : TooLarge(region.Width, region.Height) ? $"a tile at level {level} of {name} read from a {region.Width} x {region.Height} slide region"
                            : null;
                        if (what is not null)
                        {
                            return $"--size {args.TileSize} and --overlap {args.Overlap} make {what}, which needs more bytes than " +
                                "a .NET array can hold. Choose a smaller -s/--size or -e/--overlap.";
                        }
                    }
                }
            }
        }

        return null;

        static IEnumerable<long> Widest(long count) => new[] { 0L, 1L, count - 1 }.Where(i => i >= 0 && i < count).Distinct();

        // OpenSlide.CheckedPixelBytes, which GetTile uses: width * height * 4 bytes must fit in an array.
        static bool TooLarge(long width, long height)
        {
            width = Math.Max(width, 0);
            height = Math.Max(height, 0);
            return width != 0 && height > Array.MaxLength / 4 / width;
        }
    }

    private static async Task ExportAsync(OpenSlide slide, DeepZoomArguments args, TextWriter stderr, CancellationToken ct)
    {
        string name = Path.TrimEndingDirectorySeparator(args.Output ?? Path.GetFileNameWithoutExtension(args.Slide));
        var writer = new TileWriter(ExportSupport.LimitJobs(args.Jobs, stderr), stderr, args.FileWritten);

        if (!args.Viewer)
        {
            await ExportImageAsync(writer, CreateGenerator(slide, null, args, stderr), name, ViewerWriter.SlideName, args, stderr, ct)
                .ConfigureAwait(false);
            return;
        }

        ExportSupport.PrepareDirectory(name, args.Slide, stderr);

        var dziData = new Dictionary<string, string>(StringComparer.Ordinal);
        var slideImage = CreateGenerator(slide, null, args, stderr);
        dziData[ViewerWriter.SlideName + ".dzi"] = await ExportImageAsync(
            writer, slideImage, Path.Combine(name, ViewerWriter.SlideName), ViewerWriter.SlideName, args, stderr, ct).ConfigureAwait(false);

        var associated = new List<KeyValuePair<string, string>>();
        foreach ((string imageName, string slug) in ViewerWriter.AssignSlugs(slide.AssociatedImages.Keys, ViewerWriter.SlideName))
        {
            var image = CreateGenerator(slide, imageName, args, stderr);
            dziData[slug + ".dzi"] = await ExportImageAsync(writer, image, Path.Combine(name, slug), imageName, args, stderr, ct)
                .ConfigureAwait(false);
            associated.Add(new KeyValuePair<string, string>(imageName, slug + ".dzi"));
        }

        var properties = slide.Properties.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        string html = ViewerWriter.RenderIndex(ViewerWriter.FormatMpp(slide.Properties), associated, properties, dziData);
        writer.WriteFile(Path.Combine(name, "index.html"), Encoding.UTF8.GetBytes(html));
        ViewerWriter.WriteStatic(writer, Path.Combine(name, "static"));
    }

    private static (DeepZoomGenerator Generator, ColorMode EncodeMode) CreateGenerator(
        OpenSlide slide, string? associated, DeepZoomArguments args, TextWriter stderr)
    {
        if (associated is null)
        {
            var (transform, mode) = ExportSupport.DeepZoomColor(
                slide.ColorProfile, args.ColorMode, ExportSupport.Describe(args.Slide, null), stderr);
            return (new DeepZoomGenerator(slide, args.TileSize, args.Overlap, args.LimitBounds, transform), mode);
        }

        AssociatedImage image = slide.AssociatedImages[associated];
        var (imageTransform, imageMode) = ExportSupport.DeepZoomColor(
            image.ColorProfile, args.ColorMode, ExportSupport.Describe(args.Slide, associated), stderr);
        return (new DeepZoomGenerator(image, args.TileSize, args.Overlap, imageTransform), imageMode);
    }

    // Writes the tiles of one image and then its DZI, as deepzoom_tile.py does, and returns the DZI.
    private static async Task<string> ExportImageAsync(
        TileWriter writer,
        (DeepZoomGenerator Generator, ColorMode EncodeMode) image,
        string basePath,
        string displayName,
        DeepZoomArguments args,
        TextWriter stderr,
        CancellationToken ct)
    {
        DeepZoomGenerator generator = image.Generator;
        string extension = args.Format == ImageFormat.Png ? "png" : "jpeg";
        string dziPath = basePath + ".dzi";
        string files = basePath + "_files";

        // Check both before changing anything. Then the old tiles move aside in one step (if that fails, nothing has
        // changed), and the old DZI goes before anything new is written: an export that stops early must not leave
        // it pointing at a partial tree.
        ExportSupport.EnsureReplaceableFile(dziPath, args.Slide);
        string? old = ExportSupport.MoveAside(files, args.Slide, stderr);
        File.Delete(dziPath);
        ExportSupport.DeleteOldOutput(old, stderr);
        ExportSupport.CreateOutputDirectory(files);

        var levels = new string[generator.LevelCount];
        for (int level = 0; level < levels.Length; level++)
        {
            levels[level] = Path.Combine(files, level.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(levels[level]);
        }

        IEnumerable<(int Level, long Column, long Row)> Tiles()
        {
            for (int level = 0; level < generator.LevelCount; level++)
            {
                TileGridSize grid = generator.LevelTiles[level];
                for (long row = 0; row < grid.Rows; row++)
                {
                    for (long column = 0; column < grid.Columns; column++)
                    {
                        yield return (level, column, row);
                    }
                }
            }
        }

        await writer.RunAsync(
            writer.StartProgress(displayName, generator.TileCount),
            Tiles(),
            (tile, _) =>
            {
                byte[] encoded = generator.GetTile(tile.Level, tile.Column, tile.Row).Encode(args.Format, args.Quality, image.EncodeMode);
                string fileName = string.Create(CultureInfo.InvariantCulture, $"{tile.Column}_{tile.Row}.{extension}");
                writer.WriteFile(Path.Combine(levels[tile.Level], fileName), encoded);
                return 1;
            },
            ct).ConfigureAwait(false);

        string dzi = generator.GetDzi(extension);
        writer.WriteFile(dziPath, Encoding.UTF8.GetBytes(dzi));
        return dzi;
    }
}
