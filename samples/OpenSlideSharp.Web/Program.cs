using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenSlideSharp.AspNetCore;

namespace OpenSlideSharp.Web;

/// <summary>
/// A web viewer for slides: one slide (like openslide-python's <c>deepzoom_server.py</c>) or a directory of slides
/// (like <c>deepzoom_multiserver.py</c>), with the Deep Zoom and OME-Zarr endpoints of OpenSlideSharp.AspNetCore.
/// </summary>
public partial class Program
{
    private const string Usage = """
        Usage: OpenSlideSharp.Web [options] SLIDE_OR_DIRECTORY

          -l, --listen HOST         address to listen on (default 127.0.0.1)
          -p, --port PORT           port to listen on (default 5000)
          -s, --size N              Deep Zoom tile size without overlap (default 254)
          -e, --overlap N           Deep Zoom tile overlap on each side (default 1)
          -f, --format jpeg|png     Deep Zoom tile format (default jpeg)
          -Q, --quality N           JPEG quality, 0 to 100 (default 75)
              --color-mode MODE     default, embed or ignore (default default)
          -B, --ignore-bounds       show the whole slide instead of its bounds rectangle
              --Key=Value           any other configuration value, e.g. --OpenSlide:SlideCacheSize=20
        """;

    // Option name -> configuration key, for the options that take a value.
    private static readonly Dictionary<string, string> ValueOptions = new(StringComparer.Ordinal)
    {
        ["-s"] = "OpenSlide:DeepZoom:TileSize",
        ["--size"] = "OpenSlide:DeepZoom:TileSize",
        ["-e"] = "OpenSlide:DeepZoom:Overlap",
        ["--overlap"] = "OpenSlide:DeepZoom:Overlap",
        ["-f"] = "OpenSlide:DeepZoom:Format",
        ["--format"] = "OpenSlide:DeepZoom:Format",
        ["-Q"] = "OpenSlide:DeepZoom:Quality",
        ["--quality"] = "OpenSlide:DeepZoom:Quality",
        ["--color-mode"] = "OpenSlide:ColorMode",
    };

    private const string DefaultUrl = "http://127.0.0.1:5000";

    /// <summary>Starts the web viewer.</summary>
    public static void Main(string[] args) => Environment.ExitCode = Run(args, Console.Error);

    /// <summary>
    /// Runs the web viewer until it is stopped, and returns the exit code: 0, or 2 after writing the problem and the
    /// usage to <paramref name="stderr"/> when the arguments, <c>SlidePath</c> or the options are not valid.
    /// </summary>
    internal static int Run(string[] args, TextWriter stderr)
    {
        string[] mapped;
        try
        {
            mapped = MapArguments(args);
        }
        catch (ArgumentException e)
        {
            return Fail(stderr, e.Message);
        }

        WebApplicationBuilder builder = WebApplication.CreateBuilder(mapped);
        if (DefaultUrls(builder.Configuration) is { } urls)
        {
            builder.WebHost.UseUrls(urls);
        }

        builder.Services.AddOpenSlide(builder.Configuration.GetSection("OpenSlide"));
        builder.Services.AddSingleton<SlideSite>();
        builder.Services.AddRazorPages();

        WebApplication app = builder.Build();
        SlideSite site;
        try
        {
            // Opens nothing yet, but checks SlidePath and, through the slide cache, the OpenSlide options.
            site = app.Services.GetRequiredService<SlideSite>();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or OptionsValidationException)
        {
            return Fail(stderr, e.Message);
        }

        app.UseStaticFiles();
        app.MapDeepZoom("/dz", site.Resolver);
        app.MapOmeZarr("/zarr", site.Resolver);
        app.MapRazorPages();
        app.Run();
        return 0;
    }

    private static int Fail(TextWriter stderr, string message)
    {
        stderr.WriteLine(message);
        stderr.WriteLine();
        stderr.WriteLine(Usage);
        return 2;
    }

    /// <summary>
    /// Turns Python-style command-line options into configuration values: <c>-s</c>, <c>-e</c>, <c>-f</c>, <c>-Q</c>
    /// and <c>--color-mode</c> set the matching <c>OpenSlide:*</c> keys, <c>-l</c> and <c>-p</c> form <c>urls</c>,
    /// <c>-B</c> turns off <c>LimitBounds</c> for Deep Zoom and OME-Zarr, and the one positional argument is
    /// <c>SlidePath</c>. <c>--Key=Value</c> sets any other configuration key.
    /// </summary>
    /// <exception cref="ArgumentException">An option is unknown or lacks its value, or there are several positional arguments.</exception>
    internal static IConfiguration BuildConfiguration(string[] args) =>
        new ConfigurationBuilder().AddCommandLine(MapArguments(args)).Build();

    /// <summary>Maps the Python-style options to <c>--Key=Value</c> arguments; see <see cref="BuildConfiguration"/>.</summary>
    /// <exception cref="ArgumentException">An option is unknown or lacks its value, or there are several positional arguments.</exception>
    internal static string[] MapArguments(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? host = null;
        string? port = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (ValueOptions.TryGetValue(arg, out string? key))
            {
                values[key] = NextValue(args, ref i);
            }
            else if (arg is "-l" or "--listen")
            {
                host = NextValue(args, ref i);
            }
            else if (arg is "-p" or "--port")
            {
                port = NextValue(args, ref i);
            }
            else if (arg is "-B" or "--ignore-bounds")
            {
                values["OpenSlide:DeepZoom:LimitBounds"] = "false";
                values["OpenSlide:OmeZarr:LimitBounds"] = "false";
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Contains('=', StringComparison.Ordinal))
            {
                int equals = arg.IndexOf('=', StringComparison.Ordinal);
                values[arg[2..equals]] = arg[(equals + 1)..];
            }
            else if (arg.StartsWith('-') && arg.Length > 1)
            {
                throw new ArgumentException($"Unknown option '{arg}'.");
            }
            else if (values.ContainsKey("SlidePath"))
            {
                throw new ArgumentException($"Unexpected argument '{arg}': only one slide or directory can be given.");
            }
            else
            {
                values["SlidePath"] = arg;
            }
        }

        if (host is not null || port is not null)
        {
            values["urls"] = $"http://{host ?? "127.0.0.1"}:{port ?? "5000"}";
        }

        return [.. values.Select(v => $"--{v.Key}={v.Value}")];
    }

    /// <summary>
    /// The address to listen on when nothing else sets one: <c>http://127.0.0.1:5000</c>, like the Python scripts.
    /// Returns null when <c>urls</c> (<c>ASPNETCORE_URLS</c>, <c>--urls</c>, <c>-l</c>/<c>-p</c>),
    /// <c>http_ports</c> or <c>https_ports</c> (<c>ASPNETCORE_HTTP_PORTS</c>, <c>ASPNETCORE_HTTPS_PORTS</c>), or
    /// Kestrel endpoints are configured.
    /// </summary>
    internal static string? DefaultUrls(IConfiguration configuration) =>
        string.IsNullOrEmpty(configuration["urls"])
        && string.IsNullOrEmpty(configuration["http_ports"])
        && string.IsNullOrEmpty(configuration["https_ports"])
        && !configuration.GetSection("Kestrel:Endpoints").Exists()
            ? DefaultUrl
            : null;

    private static string NextValue(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"Option '{args[i]}' needs a value.");
        }

        return args[++i];
    }
}
