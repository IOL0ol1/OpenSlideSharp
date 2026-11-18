using System;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia;

namespace OpenSlideSharp.Viewer;

internal static class Program
{
    // The size of the region --check reads.
    private const int CheckRegionSize = 256;

    // Don't use Avalonia, third-party APIs or SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--check")
        {
            ParentConsole.AttachIfNoOutput();
            return RunCheck(args, Console.Out);
        }

        return RunApp(args);
    }

    // Avalonia configuration; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// <c>--check &lt;slide&gt;</c>: checks without a window (or a display) that the viewer can load OpenSlide and
    /// read the slide. Prints <c>openslide=&lt;version&gt;</c> and <c>slide=&lt;width&gt;x&lt;height&gt;</c> after
    /// reading the top-left 256 x 256 region of the lowest-resolution level.
    /// </summary>
    /// <returns>
    /// 0 on success, 1 if the slide cannot be opened or read, 2 for invalid arguments, 3 if the native OpenSlide
    /// library cannot be loaded.
    /// </returns>
    internal static int RunCheck(string[] args, TextWriter output)
    {
        if (args.Length != 2 || args[0] != "--check")
        {
            output.WriteLine("Usage: OpenSlideSharp.Viewer [slide] | --check <slide>");
            return 2;
        }

        string path = args[1];
        try
        {
            output.WriteLine("openslide=" + OpenSlideLibrary.Version);
            using OpenSlide slide = OpenSlide.Open(path);
            var pixels = new byte[CheckRegionSize * CheckRegionSize * 4];
            slide.ReadRegion(0, 0, slide.LevelCount - 1, CheckRegionSize, CheckRegionSize, pixels);
            output.WriteLine(FormattableString.Invariant($"slide={slide.Dimensions.Width}x{slide.Dimensions.Height}"));
            return 0;
        }
        catch (DllNotFoundException e)
        {
            output.WriteLine(e.Message);
            return 3;
        }
        catch (Exception e) when (e is OpenSlideException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            output.WriteLine($"Cannot open {path}: {e.Message}");
            return 1;
        }
    }

    // Kept out of Main so that --check does not even load Avalonia.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApp(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
}
