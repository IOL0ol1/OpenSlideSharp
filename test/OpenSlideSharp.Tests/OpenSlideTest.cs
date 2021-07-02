using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

using Xunit;
using Xunit.Abstractions;

namespace OpenSlideSharp.Tests
{
    public class OpenSlideTest
    {
        protected readonly ITestOutputHelper Output;

        public OpenSlideTest(ITestOutputHelper outputHelper)
        {
             CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture;
             Output = outputHelper;
        }


        public static IEnumerable<object[]> GetOpenableFiles()
        {
            string currentDir = Directory.GetCurrentDirectory();
            yield return new object[] { Path.Combine(currentDir, "Assets", "boxes.tiff") };
            yield return new object[] { Path.Combine(currentDir, "Assets", "small.svs") };
        }

        public static IEnumerable<object[]> GetUnsupportedFiles()
        {
            string currentDir = Directory.GetCurrentDirectory();
            yield return new object[] { Path.Combine(currentDir, "Assets", "boxes.png") };
            yield return new object[] { Path.Combine(currentDir, "Assets", "unopenable.tiff") };
            yield return new object[] { Path.Combine(currentDir, "Assets", "unreadable.svs") };
        }


        [Fact]
        public void TestLibraryVersion()
        {
            string version = OpenSlideImage.LibraryVersion;
            Assert.NotNull(version);
            Assert.NotEqual(string.Empty, version);
        }

        [Theory]
        [MemberData(nameof(GetOpenableFiles))]
        public void TestOpen(string fileName)
        {
            using (var osr = OpenSlideImage.Open(fileName))
            {
                Assert.True(osr.Handle != IntPtr.Zero);
            }
        }

        [Theory]
        [MemberData(nameof(GetUnsupportedFiles))]
        public void TestUnsupportedFiles(string fileName)
        {
            Assert.Throws<OpenSlideException>(() => OpenSlideImage.Open(fileName));
        }


        public static IEnumerable<object[]> GetDetectFormatData()
        {
            string currentDir = Directory.GetCurrentDirectory();
            yield return new object[] { Path.Combine(currentDir, "Assets", "boxes.png"), null };
            yield return new object[] { Path.Combine(currentDir, "Assets", "boxes.tiff"), "generic-tiff" };
        }
        [Theory]
        [MemberData(nameof(GetDetectFormatData))]
        public void TestDetectFormat(string fileName, string format)
        {
            Assert.Equal(format, OpenSlideImage.DetectVendor(fileName));
        }

        [Fact]
        public void TestUnopenableFile()
        {
            string currentDir = Directory.GetCurrentDirectory();
            Assert.Throws<OpenSlideException>(() => OpenSlideImage.Open(Path.Combine(currentDir, "Assets", "unopenable.tiff")));
        }

        [Fact]
        public void TestOperationsOnClosedHandle()
        {
            string currentDir = Directory.GetCurrentDirectory();
            var osr = OpenSlideImage.Open(Path.Combine(currentDir, "Assets", "boxes.tiff"));
            Assert.NotEmpty(osr.GetAllPropertyNames());
            Assert.Empty(osr.GetAllAssociatedImageNames());
            osr.Dispose();
            Assert.Throws<ObjectDisposedException>(() => osr.LevelCount);
            Assert.Throws<ObjectDisposedException>(() => osr.ReadRegion(0, 0, 0, 100, 100));
            Assert.Throws<ObjectDisposedException>(() => osr.GetProperty<string>("openslide.vendor"));
            Assert.Throws<ObjectDisposedException>(() => { return osr.TryReadAssociatedImage("label", out var dimensions, out var dset) ? dset : null; });
        }

        [Fact]
        public void TestMeradata()
        {
            string currentDir = Directory.GetCurrentDirectory();
            using (var osr = OpenSlideImage.Open(Path.Combine(currentDir, "Assets", "boxes.tiff")))
            {
                Assert.Equal(4, osr.LevelCount);
                long width, height;

                osr.GetLevelDimensions(0).Deconstruct(out width, out height);
                Assert.Equal(300, width);
                Assert.Equal(250, height);

                osr.GetLevelDimensions(1).Deconstruct(out width, out height);
                Assert.Equal(150, width);
                Assert.Equal(125, height);

                osr.GetLevelDimensions(2).Deconstruct(out width, out height);
                Assert.Equal(75, width);
                Assert.Equal(62, height);

                osr.GetLevelDimensions(3).Deconstruct(out width, out height);
                Assert.Equal(37, width);
                Assert.Equal(31, height);

                Assert.Equal(1, osr.GetLevelDownsample(0));
                Assert.Equal(2, osr.GetLevelDownsample(1));
                Assert.Equal(4, osr.GetLevelDownsample(2), 0);
                Assert.Equal(8, osr.GetLevelDownsample(3), 0);

                Assert.Equal(0, osr.GetBestLevelForDownsample(0.5));
                Assert.Equal(1, osr.GetBestLevelForDownsample(3));
                Assert.Equal(3, osr.GetBestLevelForDownsample(37));
            }
        }

        [Fact]
        public void TestProperties()
        {
            string currentDir = Directory.GetCurrentDirectory();
            using (var osr = OpenSlideImage.Open(Path.Combine(currentDir, "Assets", "boxes.tiff")))
            {
                var props = osr.GetAllPropertyNames();
                string value = null;
                Assert.True(osr.TryGetProperty("openslide.vendor", out value));
                Assert.Equal("generic-tiff", value);
                string value2 = null;
                Assert.False(osr.TryGetProperty("__does_not_exist", out value2));
                Assert.Null(value2);
            }
        }

        [Fact]
        public void TestReadRegion()
        {
            string currentDir = Directory.GetCurrentDirectory();
            //using (var osr = OpenSlideImage.Open(Path.Combine(currentDir, "Assets", "boxes.tiff")))
            //{
            //    byte[] arr;
            //    arr = osr.ReadRegionToArray(1, -10, -10, 400, 400);
            //    Assert.Equal(400 * 400 * 4, arr.Length);
            //    arr = osr.ReadRegionToArray(4, 0, 0, 100, 100); // Bad level
            //    Assert.Equal(100 * 100 * 4, arr.Length);
            //    Assert.Throws<ArgumentOutOfRangeException>(() => { osr.ReadRegionToArray(1, 0, 0, 400, -5); });
            //}
        }

        [Fact]
        public void TestAssociatedImages()
        {
            string currentDir = Directory.GetCurrentDirectory();
            //using (var osr = OpenSlideImage.Open(Path.Combine(currentDir, "Assets", "small.svs")))
            //{
            //    Assert.NotEmpty(osr.GetAllAssociatedImageNames());
            //    byte[] arr;
            //    arr = osr.ReadAssociatedImageToArray("thumbnail", out var dims);
            //    Assert.Equal(16, dims.Width);
            //    Assert.Equal(16, dims.Height);
            //    Assert.Equal(16 * 16 * 4, arr.Length);
            //    Assert.Throws<KeyNotFoundException>(() => { osr.ReadAssociatedImageToArray("__missing"); });
            //}
        }

        [Fact]
        public void TestUnreadableSlideBadRegion()
        {
            string currentDir = Directory.GetCurrentDirectory();
            using (var osr = OpenSlideImage.Open(Path.Combine(currentDir, "Assets", "unreadable.svs")))
            {
                //Assert.Equal("aperio", osr.GetProperty("openslide.vendor", string.Empty));
                //Assert.Throws<OpenSlideException>(() => { osr.ReadRegionToArray(0, 0, 0, 16, 16); });
                //// openslide object has turned into an unusable state.
                //Assert.False(osr.TryGetProperty("", out string value));
            }
        }

        [Fact]
        public void TestUnreadableSlideBadAssociatedImage()
        {
            string currentDir = Directory.GetCurrentDirectory();
            using (var osr = OpenSlideImage.Open(Path.Combine(currentDir, "Assets", "unreadable.svs")))
            {
                //Assert.Equal("aperio", osr.GetProperty("openslide.vendor", string.Empty));
                //Assert.Throws<OpenSlideException>(() => { osr.ReadAssociatedImageToArray("thumbnail"); });
                //// openslide object has turned into an unusable state.
                //Assert.False(osr.TryGetProperty("", out string value));
            }
        }

    }
}
