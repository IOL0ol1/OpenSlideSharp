using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace OpenSlideSharp
{
    /// <summary>
    /// 
    /// </summary>
    public static class OpenSlideImageExtensions
    {
        /// <summary>
        /// Read region bitmap.
        /// </summary>
        /// <param name="image"></param>
        /// <param name="level"></param>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="width"></param>
        /// <param name="height"></param>
        /// <returns></returns>
        public unsafe static Bitmap ReadRegionImage(this OpenSlideImage image, int level, long x, long y, long width, long height)
        {
            if (image == null) throw new NullReferenceException();
            var data = image.ReadRegion(level, x, y, width, height);
            var bitmap = new Bitmap((int)width, (int)height);
            var bitmapData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            var bytesPerLine = (int)Math.Min(width * 4, bitmapData.Stride);
            for (int i = 0; i < bitmap.Height; i++)
            {
                Marshal.Copy(data, (int)width * 4 * i, (IntPtr)((byte*)bitmapData.Scan0 + (bitmapData.Stride * i)), bytesPerLine);
            }
            bitmap.UnlockBits(bitmapData);
            return bitmap;
        }

        /// <summary>
        /// Read region jpeg stream.
        /// </summary>
        /// <param name="image"></param>
        /// <param name="level"></param>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="width"></param>
        /// <param name="height"></param>
        /// <param name="quality"></param>
        /// <returns></returns>
        public unsafe static MemoryStream ReadRegionJpegStream(this OpenSlideImage image, int level, long x, long y, long width, long height, int? quality = null)
        {
            return ReadRegionImage(image, level, x, y, width, height).ToStream(ImageFormat.Jpeg, quality);
        }

        /// <summary>
        /// Read region jpeg
        /// </summary>
        /// <param name="image"></param>
        /// <param name="level"></param>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="width"></param>
        /// <param name="height"></param>
        /// <param name="quality"></param>
        /// <returns></returns>

        public unsafe static byte[] ReadRegionJpeg(this OpenSlideImage image, int level, long x, long y, long width, long height, int? quality = null)
        {
            using (var ms = ReadRegionJpegStream(image, level, x, y, width, height, quality))
            {
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Read region png stream.
        /// </summary>
        /// <param name="image"></param>
        /// <param name="level"></param>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="width"></param>
        /// <param name="height"></param>
        /// <param name="quality"></param>
        /// <returns></returns>
        public unsafe static MemoryStream ReadRegionPngStream(this OpenSlideImage image, int level, long x, long y, long width, long height, int? quality = null)
        {
            return ReadRegionImage(image, level, x, y, width, height).ToStream(ImageFormat.Png, quality);
        }

        /// <summary>
        /// Read region png.
        /// </summary>
        /// <param name="image"></param>
        /// <param name="level"></param>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="width"></param>
        /// <param name="height"></param>
        /// <param name="quality"></param>
        /// <returns></returns>
        public unsafe static byte[] ReadRegionPng(this OpenSlideImage image, int level, long x, long y, long width, long height, int? quality = null)
        {
            using (var ms = ReadRegionPngStream(image, level, x, y, width, height, quality))
            {
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Generate thumbnail image.
        /// </summary>
        /// <param name="image"></param>
        /// <param name="maxWidth"></param>
        /// <param name="maxHeight"></param>
        /// <returns></returns>
        public static Bitmap GenerateThumbnailImage(this OpenSlideImage image, int maxWidth, int maxHeight)
        {
            if (image == null) throw new NullReferenceException();

            (long width, long height) = image.Dimensions;

            // Find the appropriate level
            double downsampleWidth = width / (double)maxWidth;
            double downsampleHeight = height / (double)maxHeight;
            double downsample = Math.Max(downsampleWidth, downsampleHeight);
            int level = image.GetBestLevelForDownsample(downsample);
            (long levelWidth, long levelHeight) = image.GetLevelDimension(level);

            // Calculate target size
            int targetWidth, targetHeight;
            if (downsampleWidth > downsampleHeight)
            {
                targetWidth = maxWidth;
                targetHeight = (int)(height / downsampleWidth);
            }
            else
            {
                targetWidth = (int)(width / downsampleHeight);
                targetHeight = maxHeight;
            }

            using (var bitmap = ReadRegionImage(image, level, 0, 0, levelWidth, levelHeight))
            {
                return new Bitmap(bitmap, new Size(targetWidth, targetHeight));
            }
        }

        /// <summary>
        /// Generate thumbnail jpeg stream.
        /// </summary>
        /// <param name="image"></param>
        /// <param name="maxWidth"></param>
        /// <param name="maxHeight"></param>
        /// <param name="targetWidth"></param>
        /// <param name="targetHeight"></param>
        /// <param name="quality"></param>
        /// <returns></returns>
        public static MemoryStream GenerateThumbnailJpegStream(this OpenSlideImage image, int maxWidth, int maxHeight, out int targetWidth, out int targetHeight, int? quality = null)
        {
            using (var bitmap = GenerateThumbnailImage(image, maxWidth, maxHeight))
            {
                targetWidth = bitmap.Width;
                targetHeight = bitmap.Height;
                return bitmap.ToStream(ImageFormat.Jpeg, quality);
            }
        }

        /// <summary>
        /// Generate thumbnail jpeg.
        /// </summary>
        /// <param name="image"></param>
        /// <param name="maxWidth"></param>
        /// <param name="maxHeight"></param>
        /// <param name="targetWidth"></param>
        /// <param name="targetHeight"></param>
        /// <param name="quality"></param>
        /// <returns></returns>
        public static byte[] GenerateThumbnailJpeg(this OpenSlideImage image, int maxWidth, int maxHeight, out int targetWidth, out int targetHeight, int? quality = null)
        {
            using (var ms = GenerateThumbnailJpegStream(image, maxWidth, maxHeight, out targetWidth, out targetHeight, quality))
            {
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Generate thumbail png stream.
        /// </summary>
        /// <param name="image"></param>
        /// <param name="maxWidth"></param>
        /// <param name="maxHeight"></param>
        /// <param name="targetWidth"></param>
        /// <param name="targetHeight"></param>
        /// <param name="quality"></param>
        /// <returns></returns>
        public static MemoryStream GenerateThumbnailPngStream(this OpenSlideImage image, int maxWidth, int maxHeight, out int targetWidth, out int targetHeight, int? quality = null)
        {
            using (var bitmap = GenerateThumbnailImage(image, maxWidth, maxHeight))
            {
                targetWidth = bitmap.Width;
                targetHeight = bitmap.Height;
                return bitmap.ToStream(ImageFormat.Png, quality);
            }
        }

        /// <summary>
        /// Generate thumbnail png.
        /// </summary>
        /// <param name="image"></param>
        /// <param name="maxWidth"></param>
        /// <param name="maxHeight"></param>
        /// <param name="targetWidth"></param>
        /// <param name="targetHeight"></param>
        /// <param name="quality"></param>
        /// <returns></returns>
        public static byte[] GenerateThumbnailPng(this OpenSlideImage image, int maxWidth, int maxHeight, out int targetWidth, out int targetHeight, int? quality = null)
        {
            using (var ms = GenerateThumbnailPngStream(image, maxWidth, maxHeight, out targetWidth, out targetHeight, quality))
            {
                return ms.ToArray();
            }
        }
    }


}
