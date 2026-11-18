using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace OpenSlideSharp.AspNetCore.Internal;

/// <summary>
/// Checks <see cref="OpenSlideServerOptions"/>. <c>AddOpenSlide</c> registers it for the options, and the
/// <see cref="SlideCache"/> constructor runs it too.
/// </summary>
internal sealed class OpenSlideServerOptionsValidator : IValidateOptions<OpenSlideServerOptions>
{
    /// <summary>Throws <see cref="OptionsValidationException"/> if <paramref name="options"/> are not valid.</summary>
    public static void ThrowIfInvalid(OpenSlideServerOptions options)
    {
        ValidateOptionsResult result = new OpenSlideServerOptionsValidator().Validate(Options.DefaultName, options);
        if (result.Failed)
        {
            throw new OptionsValidationException(Options.DefaultName, typeof(OpenSlideServerOptions), result.Failures);
        }
    }

    public ValidateOptionsResult Validate(string? name, OpenSlideServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.SlideCacheSize < 1)
        {
            failures.Add($"SlideCacheSize must be at least 1, but is {options.SlideCacheSize}.");
        }

        if (options.TileCacheSize < 0)
        {
            failures.Add($"TileCacheSize must not be negative, but is {options.TileCacheSize}.");
        }

        if (!Enum.IsDefined(options.ColorMode))
        {
            failures.Add($"ColorMode {options.ColorMode} is not a defined ColorMode.");
        }

        DeepZoomServerOptions deepZoom = options.DeepZoom;
        if (deepZoom.TileSize < 1)
        {
            failures.Add($"DeepZoom.TileSize must be at least 1, but is {deepZoom.TileSize}.");
        }

        if (deepZoom.Overlap < 0)
        {
            failures.Add($"DeepZoom.Overlap must not be negative, but is {deepZoom.Overlap}.");
        }

        // DeepZoomGenerator accepts any tile size and overlap, as openslide-python does, and only GetTile fails for a
        // tile too large for an array. A server has no use for tile edges this long: fail at startup instead of on
        // requests.
        if (deepZoom.TileSize >= 1 && deepZoom.Overlap >= 0 && deepZoom.TileSize + (2L * deepZoom.Overlap) > int.MaxValue / 4)
        {
            failures.Add("DeepZoom.TileSize plus twice DeepZoom.Overlap is too large.");
        }

        if (!Enum.IsDefined(deepZoom.Format))
        {
            failures.Add($"DeepZoom.Format {deepZoom.Format} is not a defined ImageFormat.");
        }

        if (deepZoom.Quality < 0 || deepZoom.Quality > 100)
        {
            failures.Add($"DeepZoom.Quality must be from 0 to 100, but is {deepZoom.Quality}.");
        }

        OmeZarrServerOptions omeZarr = options.OmeZarr;
        if (!Enum.IsDefined(omeZarr.Version))
        {
            failures.Add($"OmeZarr.Version {omeZarr.Version} is not a defined NgffVersion.");
        }

        if (omeZarr.ChunkSize < 16 || omeZarr.ChunkSize > 8192)
        {
            failures.Add($"OmeZarr.ChunkSize must be from 16 to 8192, but is {omeZarr.ChunkSize}.");
        }

        if (omeZarr.RenderCacheSize < 0)
        {
            failures.Add($"OmeZarr.RenderCacheSize must not be negative, but is {omeZarr.RenderCacheSize}.");
        }

        if (!Enum.IsDefined(omeZarr.Compression))
        {
            failures.Add($"OmeZarr.Compression {omeZarr.Compression} is not a defined OmeZarrCompression.");
        }

        if (omeZarr.GzipLevel < 0 || omeZarr.GzipLevel > 9)
        {
            failures.Add($"OmeZarr.GzipLevel must be from 0 to 9, but is {omeZarr.GzipLevel}.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
