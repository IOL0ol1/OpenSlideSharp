using System;
using OpenSlideSharp;

namespace ConsoleApp2
{
    class Program
    {
        static void Main(string[] args)
        {
            var file = @"E:\cc\ndpi\2020-05-23 18.22.31.ndpi";

            var b = OpenSlideImage.DetectVendor(file);
            using (var slide = OpenSlideImage.Open(file))
            {
                foreach (var item in slide.GetProperties())
                {
                    Console.WriteLine($"{item}");
                }
                foreach (var item in slide.GetAssociatedImages())
                {
                    Console.WriteLine($"[{item.Key}, {item.Value.Dimensions}]");
                }
            }
            Console.WriteLine("Hello World!");
        }
    }
}
