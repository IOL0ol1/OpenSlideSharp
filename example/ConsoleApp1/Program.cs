using System;
using OpenSlide.Interop;

namespace ConsoleApp1
{
    class Program
    {
        static void Main(string[] args)
        {
            string file = "boxes.tiff";

            var a = OpenSlideImage.DetectVendor(file);

            using (var slide = OpenSlideImage.Open(file))
            {
                foreach (var item in slide.GetAllPropertyNames())
                {
                    Console.WriteLine(item);
                }

                if (slide.TryGetProperty("openslide.vendor1", out var v))
                    Console.WriteLine(v);

            }
            Console.WriteLine("Hello World!");
            Console.ReadLine();
        }
    }
}
