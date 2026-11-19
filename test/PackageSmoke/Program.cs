using System;
using OpenSlideSharp;
using OpenSlideSharp.SkiaSharp;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Console.WriteLine("process=" + (Environment.Is64BitProcess ? "64-bit" : "32-bit"));
            Console.WriteLine("version=" + OpenSlideLibrary.Version);
            using var slide = OpenSlide.Open(args[0]);
            Console.WriteLine("vendor=" + slide.Properties[OpenSlideProperties.Vendor]);
            var size = slide.Dimensions;
            Console.WriteLine("size=" + size.Width + "x" + size.Height);
            byte[] png = slide.ReadRegion(0, 0, 0, 16, 16).ToOpaque(255, 255, 255).Encode(ImageFormat.Png);
            byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            for (int i = 0; i < signature.Length; i++)
            {
                if (png.Length <= i || png[i] != signature[i])
                {
                    Console.WriteLine("png=bad");
                    return 4;
                }
            }

            Console.WriteLine("png=ok");

            // The OME-Zarr metadata is written with System.Text.Json, a package dependency of the netstandard builds.
            var zarr = new OmeZarrGenerator(slide);
            if (!zarr.TryGetEntry("zarr.json", out byte[]? group) ||
                !System.Text.Encoding.UTF8.GetString(group).StartsWith("{\"zarr_format\":3,\"node_type\":\"group\",", StringComparison.Ordinal))
            {
                Console.WriteLine("zarr=bad");
                return 5;
            }

            Console.WriteLine("zarr=ok");
#if SMOKE_ASPNETCORE
            return AspNetCoreSmoke.RunAsync(args[0]).GetAwaiter().GetResult();
#else
            return 0;
#endif
        }
        catch (DllNotFoundException ex)
        {
            Console.WriteLine(ex.Message);
            return 3;
        }
    }
}
