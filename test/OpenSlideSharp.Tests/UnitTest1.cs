using System;
using Xunit;
using Xunit.Abstractions;

namespace OpenSlideSharp.Tests
{
    public class UnitTest1
    {
        protected readonly ITestOutputHelper Output;

        public UnitTest1(ITestOutputHelper outputHelper)
        {
            Output = outputHelper;
        }


        string file = @"E:\ndpi\2020-05-23 18.22.31.ndpi";

        [Fact]
        public void TestStaticFunction()
        {
            Assert.NotNull(OpenSlideImage.DetectVendor(file));
            Assert.NotNull(OpenSlideImage.LibraryVersion);
        }

        [Fact]
        public void TestReadInfo()
        {
            using (var slide = OpenSlideImage.Open(file))
            {
                var names = slide.GetProperties();
                foreach (var item in names)
                {
                    Output.WriteLine($"{item.Key} = {item.Value}");
                }


            }
        }

    }
}
