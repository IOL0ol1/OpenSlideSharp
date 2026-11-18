using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenSlideSharp.AspNetCore.Internal;

namespace OpenSlideSharp.AspNetCore;

/// <summary>Registers the OpenSlide server services.</summary>
public static class OpenSlideServiceCollectionExtensions
{
    private const string BindingMessage =
        "Binding OpenSlideServerOptions from configuration uses reflection, which trimming and native AOT can break. " +
        "In trimmed or AOT apps, use AddOpenSlide(services, configure) and set the options in code.";

    /// <summary>
    /// Registers <see cref="OpenSlideServerOptions"/>, their validation and a singleton <see cref="SlideCache"/>,
    /// which the Deep Zoom and OME-Zarr endpoints share.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets the options, or <see langword="null"/> to keep the defaults.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <remarks>
    /// Invalid options throw <see cref="OptionsValidationException"/> when they are first resolved, and when the
    /// host starts. This overload does not use reflection, so it suits trimmed and native AOT apps.
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
    /// <remarks>
    /// Binding uses reflection. Trimmed and native AOT apps should use
    /// <see cref="AddOpenSlide(IServiceCollection, Action{OpenSlideServerOptions}?)"/> instead.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configuration"/> is <see langword="null"/>.</exception>
    [RequiresUnreferencedCode(BindingMessage)]
    [RequiresDynamicCode(BindingMessage)]
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
}
