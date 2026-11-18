using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenSlideSharp.SkiaSharp;
using OpenSlideSharp.Tool.Export;

namespace OpenSlideSharp.Tool;

/// <summary>The options of <c>openslide-sharp omezarr</c>, with the defaults of <see cref="OmeZarrOptions"/>.</summary>
/// <param name="Slide">The slide file.</param>
internal sealed record OmeZarrArguments(string Slide)
{
    /// <summary>The output directory; null for the slide file name without its extension plus <c>.zarr</c>.</summary>
    public string? Output { get; init; }

    public NgffVersion Version { get; init; } = NgffVersion.V0_5;

    public int ChunkSize { get; init; } = 512;

    public OmeZarrCompression Compression { get; init; } = OmeZarrCompression.None;

    public int GzipLevel { get; init; } = 6;

    /// <summary>False with <c>-B</c>: the entire scan area rather than the non-empty region.</summary>
    public bool LimitBounds { get; init; } = true;

    /// <summary><see cref="ColorMode.Default"/> or <see cref="ColorMode.Ignore"/>; Embed is treated as Ignore, with a warning.</summary>
    public ColorMode ColorMode { get; init; } = ColorMode.Default;

    public int Jobs { get; init; } = Environment.ProcessorCount;

    /// <summary>Also export each associated image, to <c>&lt;output&gt;/../&lt;stem&gt;_associated/&lt;slug&gt;.zarr</c>.</summary>
    public bool Associated { get; init; }

    /// <summary>Called with the path of each file written; a test hook.</summary>
    public Action<string>? FileWritten { get; init; }
}

/// <summary>
/// <c>openslide-sharp omezarr</c>: writes every key of <see cref="OmeZarrGenerator.EnumerateKeys"/> as a file, with
/// <c>/</c> in the key as directories, and optionally the associated images as stores of their own.
/// </summary>
internal static class OmeZarrCommand
{
    /// <summary>Runs the export.</summary>
    /// <returns>0 on success, 1 if the slide cannot be opened or the export fails, 2 for invalid arguments, 130 if canceled.</returns>
    internal static async Task<int> RunAsync(OmeZarrArguments args, TextWriter stderr, CancellationToken ct)
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

    private static string? Validate(OmeZarrArguments args)
    {
        if (string.IsNullOrWhiteSpace(args.Slide))
        {
            return "The slide path is empty.";
        }

        if (args.Output is not null && string.IsNullOrWhiteSpace(args.Output))
        {
            return "The output directory is empty.";
        }

        if (args.ChunkSize < 16 || args.ChunkSize > 8192 || args.GzipLevel < 0 || args.GzipLevel > 9 || args.Jobs < 1)
        {
            return "The chunk size must be from 16 to 8192, the gzip level from 0 to 9, and jobs at least 1.";
        }

        return null;
    }

    private static async Task ExportAsync(OpenSlide slide, OmeZarrArguments args, TextWriter stderr, CancellationToken ct)
    {
        string stem = Path.GetFileNameWithoutExtension(args.Slide);
        string output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args.Output ?? stem + ".zarr"));
        string directory = Path.Combine(Path.GetDirectoryName(output) ?? output, stem + "_associated");

        // Check every output directory before changing anything. The associated directory is a sibling of the
        // output, so they can only clash by being the same: replacing it would delete the store just written.
        ExportSupport.EnsureReplaceableDirectory(output, args.Slide);
        if (args.Associated)
        {
            if (ExportSupport.IsSamePath(output, directory))
            {
                throw new IOException(
                    $"Refusing to export to '{output}': with --associated it is also the directory of the associated images " +
                    $"(<output>/../{stem}_associated). Choose another output.");
            }

            ExportSupport.EnsureReplaceableDirectory(directory, args.Slide);
        }

        args = args with { Jobs = ExportSupport.LimitJobs(args.Jobs, stderr) };
        var writer = new TileWriter(args.Jobs, stderr, args.FileWritten);
        IColorTransform? transform = ExportSupport.OmeZarrColor(
            slide.ColorProfile, args.ColorMode, ExportSupport.Describe(args.Slide, null), stderr);
        var generator = new OmeZarrGenerator(slide, CreateOptions(args, args.LimitBounds, transform));
        await ExportStoreAsync(writer, generator, output, "slide", args, stderr, ct).ConfigureAwait(false);

        if (!args.Associated)
        {
            return;
        }

        ExportSupport.PrepareDirectory(directory, args.Slide, stderr);
        foreach ((string name, string slug) in ViewerWriter.AssignSlugs(slide.AssociatedImages.Keys))
        {
            AssociatedImage image = slide.AssociatedImages[name];
            IColorTransform? imageTransform = ExportSupport.OmeZarrColor(
                image.ColorProfile, args.ColorMode, ExportSupport.Describe(args.Slide, name), stderr);

            // An associated image has no bounds of its own.
            var imageGenerator = new OmeZarrGenerator(image, CreateOptions(args, false, imageTransform));
            await ExportStoreAsync(writer, imageGenerator, Path.Combine(directory, slug + ".zarr"), name, args, stderr, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The render cache size for <paramref name="jobs"/> workers: <c>min(max(2 × jobs, 4), max(jobs, 1 GiB / region))</c>,
    /// where a region is <c>chunkSize² × 4</c> bytes.
    /// </summary>
    /// <remarks>
    /// Each worker holds the region whose channels it is writing, so the cache keeps twice as many (at least 4), but
    /// no more than fit in 1 GiB, unless that is fewer than one per worker.
    /// </remarks>
    internal static int RenderCacheSize(int jobs, int chunkSize)
    {
        long regionBytes = (long)chunkSize * chunkSize * 4;
        long byBytes = Math.Max(jobs, (1L << 30) / regionBytes);
        return (int)Math.Min(Math.Max(2L * jobs, 4), byBytes);
    }

    private static OmeZarrOptions CreateOptions(OmeZarrArguments args, bool limitBounds, IColorTransform? transform) => new()
    {
        Version = args.Version,
        ChunkSize = args.ChunkSize,
        Compression = args.Compression,
        GzipLevel = args.GzipLevel,
        LimitBounds = limitBounds,
        ColorTransform = transform,
        RenderCacheSize = RenderCacheSize(args.Jobs, args.ChunkSize),
    };

    // Writes the chunks, then the level metadata, then the root metadata, so that a store whose export stopped early
    // has no root metadata and is not taken for a complete one. The chunks go out region by region, the channels of a
    // region together, so one render serves all three.
    private static async Task ExportStoreAsync(
        TileWriter writer,
        OmeZarrGenerator generator,
        string store,
        string displayName,
        OmeZarrArguments args,
        TextWriter stderr,
        CancellationToken ct)
    {
        ExportSupport.PrepareDirectory(store, args.Slide, stderr);

        long total = 0;
        foreach (string _ in generator.EnumerateKeys())
        {
            total++;
        }

        // Filled while the regions are enumerated; the metadata keys come first.
        var metadata = new List<string>();

        IEnumerable<string[]> Regions()
        {
            var region = new List<string>(3);
            string? regionId = null;
            foreach (string key in generator.EnumerateKeys())
            {
                if (!IsChunkKey(key, out string? id))
                {
                    metadata.Add(key);
                    continue;
                }

                if (id != regionId && region.Count > 0)
                {
                    yield return region.ToArray();
                    region.Clear();
                }

                regionId = id;
                region.Add(key);
            }

            if (region.Count > 0)
            {
                yield return region.ToArray();
            }
        }

        TileWriter.Progress progress = writer.StartProgress(displayName, total);
        await writer.RunAsync(
            progress,
            Regions(),
            (keys, token) =>
            {
                long written = 0;
                foreach (string key in keys)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    WriteEntry(writer, generator, store, key);
                    written++;
                }

                return written;
            },
            ct).ConfigureAwait(false);

        // One after the other, after every chunk: each level's array metadata, then the root group metadata
        // (zarr.json for 0.5, .zgroup and .zattrs for 0.4), the only keys without a '/'.
        foreach (string key in metadata.Where(k => k.Contains('/')).Concat(metadata.Where(k => !k.Contains('/'))))
        {
            ct.ThrowIfCancellationRequested();
            WriteEntry(writer, generator, store, key);
            progress.Add(1);
        }
    }

    private static void WriteEntry(TileWriter writer, OmeZarrGenerator generator, string store, string key)
    {
        if (!generator.TryGetEntry(key, out byte[]? content))
        {
            throw new InvalidOperationException($"The generator listed the key '{key}' but has no entry for it.");
        }

        string path = Path.Combine(store, key.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        writer.WriteFile(path, content);
    }

    // Chunk keys end in numeric y/x segments, after the channel: {level}/{c}/{y}/{x} (0.4) or {level}/c/{c}/{y}/{x}
    // (0.5). The region is the key without its channel. Metadata keys end in a file name such as zarr.json.
    private static bool IsChunkKey(string key, out string? regionId)
    {
        string[] segments = key.Split('/');
        if (segments.Length < 4 || !IsNumber(segments[^1]))
        {
            regionId = null;
            return false;
        }

        regionId = segments[0] + "/" + segments[^2] + "/" + segments[^1];
        return true;
    }

    private static bool IsNumber(string segment)
    {
        foreach (char c in segment)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return segment.Length > 0;
    }
}
