using System;
using OpenSlideSharp;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Console.WriteLine("version=" + OpenSlideLibrary.Version);
            using var slide = OpenSlide.Open(args[0]);
            Console.WriteLine("vendor=" + slide.Properties[OpenSlideProperties.Vendor]);
            var size = slide.Dimensions;
            Console.WriteLine("size=" + size.Width + "x" + size.Height);
            return 0;
        }
        catch (DllNotFoundException ex)
        {
            Console.WriteLine(ex.Message);
            return 3;
        }
    }
}
