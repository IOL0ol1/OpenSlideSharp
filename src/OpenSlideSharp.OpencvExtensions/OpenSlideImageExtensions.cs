using OpenCvSharp;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace OpenSlideSharp.OpencvExtensions
{
    /// <summary>
    /// 
    /// </summary>
    public static class OpenSlideImageExtensions
    {
        /// <summary>
        /// Read region mat.
        /// </summary>
        /// <param name="image"></param>
        /// <param name="level"></param>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="width"></param>
        /// <param name="height"></param>
        /// <returns></returns>
        public unsafe static Mat ReadRegionImage(this OpenSlideImage image, int level, long x, long y, long width, long height)
        {
            if (image == null) throw new NullReferenceException();
            var data = image.ReadRegion(level, x, y, width, height);
            fixed (byte* scan0 = data)
            {
                return new Mat((int)height, (int)width, MatType.CV_8UC4, (IntPtr)scan0, (int)width * 4);
            }
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
        /// <param name="quality">For JPEG, it can be a quality from 0 to 100 (the higher is the better). Default
        /// value is 95.</param>
        /// <returns></returns>
        public unsafe static MemoryStream ReadRegionJpegStream(this OpenSlideImage image, int level, long x, long y, long width, long height, int? quality = null)
        {
            return new MemoryStream(ReadRegionJpeg(image, level, x, y, width, height, quality));
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
        /// <param name="quality">For JPEG, it can be a quality from 0 to 100 (the higher is the better). Default
        /// value is 95.</param>
        /// <returns></returns>

        public unsafe static byte[] ReadRegionJpeg(this OpenSlideImage image, int level, long x, long y, long width, long height, int? quality = null)
        {
            var prms = quality != null ? new int[] { (int)ImwriteFlags.JpegQuality, quality.Value } : null;
            return ReadRegionImage(image, level, x, y, width, height).ToBytes(".jpg", prms);
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
        /// <param name="quality">        
        /// For PNG, it can be the compression level from 0 to 9. A higher value means a
        /// smaller size and longer compression time. Default value is 3.
        /// </param>
        /// <returns></returns>
        public unsafe static MemoryStream ReadRegionPngStream(this OpenSlideImage image, int level, long x, long y, long width, long height, int? quality = null)
        {
            return new MemoryStream(ReadRegionPng(image, level, x, y, width, height, quality));
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
        /// <param name="quality">        
        /// For PNG, it can be the compression level from 0 to 9. A higher value means a
        /// smaller size and longer compression time. Default value is 3.</param>
        /// <returns></returns>
        public unsafe static byte[] ReadRegionPng(this OpenSlideImage image, int level, long x, long y, long width, long height, int? quality = null)
        {
            var prms = quality != null ? new int[] { (int)ImwriteFlags.PngCompression, quality.Value } : null;
            return ReadRegionImage(image, level, x, y, width, height).ToBytes(".png", prms);
        }

        /// <summary>
        /// Generate thumbnail image.
        /// </summary>
        /// <param name="image"></param>
        /// <param name="maxWidth"></param>
        /// <param name="maxHeight"></param>
        /// <returns></returns>
        public static Mat GenerateThumbnailImage(this OpenSlideImage image, int maxWidth, int maxHeight)
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

            var bitmap = ReadRegionImage(image, level, 0, 0, levelWidth, levelHeight);
            bitmap.Resize(targetWidth, targetHeight);
            return bitmap;
        }

        /// <summary>
        /// Generate thumbnail jpeg stream.
        /// </summary>
        /// <param name="image"></param>
        /// <param name="maxWidth"></param>
        /// <param name="maxHeight"></param>
        /// <param name="targetWidth"></param>
        /// <param name="targetHeight"></param>
        /// <param name="quality">For JPEG, it can be a quality from 0 to 100 (the higher is the better). Default
        /// value is 95.</param>
        /// <returns></returns>
        public static MemoryStream GenerateThumbnailJpegStream(this OpenSlideImage image, int maxWidth, int maxHeight, out int targetWidth, out int targetHeight, int? quality = null)
        {
            return new MemoryStream(GenerateThumbnailJpeg(image, maxWidth, maxHeight, out targetWidth, out targetHeight, quality));
        }

        /// <summary>
        /// Generate thumbnail jpeg.
        /// </summary>
        /// <param name="image"></param>
        /// <param name="maxWidth"></param>
        /// <param name="maxHeight"></param>
        /// <param name="targetWidth"></param>
        /// <param name="targetHeight"></param>
        /// <param name="quality">For JPEG, it can be a quality from 0 to 100 (the higher is the better). Default
        /// value is 95.</param>
        /// <returns></returns>
        public static byte[] GenerateThumbnailJpeg(this OpenSlideImage image, int maxWidth, int maxHeight, out int targetWidth, out int targetHeight, int? quality = null)
        {
            using (var bitmap = GenerateThumbnailImage(image, maxWidth, maxHeight))
            {
                targetWidth = bitmap.Width;
                targetHeight = bitmap.Height;
                var prms = quality != null ? new int[] { (int)ImwriteFlags.JpegQuality, quality.Value } : null;
                return bitmap.ToBytes(".jpg", prms);
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
        /// <param name="quality">
        /// For PNG, it can be the compression level from 0 to 9. A higher value means a
        /// smaller size and longer compression time. Default value is 3.
        /// </param>
        /// <returns></returns>
        public static MemoryStream GenerateThumbnailPngStream(this OpenSlideImage image, int maxWidth, int maxHeight, out int targetWidth, out int targetHeight, int? quality = null)
        {
            return new MemoryStream(GenerateThumbnailPng(image, maxWidth, maxHeight, out targetWidth, out targetHeight, quality));
        }

        /// <summary>
        /// Generate thumbnail png.
        /// </summary>
        /// <param name="image"></param>
        /// <param name="maxWidth"></param>
        /// <param name="maxHeight"></param>
        /// <param name="targetWidth"></param>
        /// <param name="targetHeight"></param>
        /// <param name="quality">        
        /// For PNG, it can be the compression level from 0 to 9. A higher value means a
        /// smaller size and longer compression time. Default value is 3.
        /// </param>
        /// <returns></returns>
        public static byte[] GenerateThumbnailPng(this OpenSlideImage image, int maxWidth, int maxHeight, out int targetWidth, out int targetHeight, int? quality = null)
        {
            using (var bitmap = GenerateThumbnailImage(image, maxWidth, maxHeight))
            {
                targetWidth = bitmap.Width;
                targetHeight = bitmap.Height;
                var prms = quality != null ? new int[] { (int)ImwriteFlags.PngCompression, quality.Value } : null;
                return bitmap.ToBytes(".png", prms);
            }
        }
    }


}
