using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace OpenSlideSharp.AspNetCore;

/// <summary>Registers the OpenSlide server services.</summary>
public static class OpenSlideServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="OpenSlideServerOptions"/>, their validation and a singleton <see cref="SlideCache"/>,
    /// which the Deep Zoom and OME-Zarr endpoints share.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets the options, or <see langword="null"/> to keep the defaults.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <remarks>
    /// Invalid options throw <see cref="OptionsValidationException"/> when they are first resolved, and when the
    /// host starts.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddOpenSlide(this IServiceCollection services, Action<OpenSlideServerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        OptionsBuilder<OpenSlideServerOptions> builder = AddCore(services);
        if (configure is not null)
        {
            builder.Configure(configure);
        }

        return services;
    }

    /// <summary>
    /// Registers the services like <see cref="AddOpenSlide(IServiceCollection, Action{OpenSlideServerOptions}?)"/>,
    /// with the options bound from a configuration section (for example <c>OpenSlide:DeepZoom:TileSize</c> for the
    /// section <c>OpenSlide</c>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration section holding the options.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configuration"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddOpenSlide(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        AddCore(services).Bind(configuration);
        return services;
    }

    private static OptionsBuilder<OpenSlideServerOptions> AddCore(IServiceCollection services)
    {
        services.AddLogging();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<OpenSlideServerOptions>, OpenSlideServerOptionsValidator>());
        services.TryAddSingleton<SlideCache>();
        return services.AddOptions<OpenSlideServerOptions>().ValidateOnStart();
    }

    private sealed class OpenSlideServerOptionsValidator : IValidateOptions<OpenSlideServerOptions>
    {
        public ValidateOptionsResult Validate(string? name, OpenSlideServerOptions options)
        {
            var failures = new List<string>();
            if (options.SlideCacheSize < 1)
            {
                failures.Add($"SlideCacheSize must be at least 1, but is {options.SlideCacheSize}.");
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

            // DeepZoomGenerator's own limit on the tile edge with overlap.
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
}
