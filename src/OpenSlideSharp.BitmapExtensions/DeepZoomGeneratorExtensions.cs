using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using static OpenSlideSharp.DeepZoomGenerator;

namespace OpenSlideSharp
{
    /// <summary>
    /// 
    /// </summary>
    public static class DeepZoomGeneratorExtensions
    {
        /// <summary>
        /// Get tile as jpeg.
        /// </summary>
        /// <param name="generator"></param>
        /// <param name="level"></param>
        /// <param name="col"></param>
        /// <param name="row"></param>
        /// <param name="tileInfo"></param>
        /// <param name="quality"></param>
        /// <returns></returns>
        public static byte[] GetTileAsJpeg(this DeepZoomGenerator generator, int level, int col, int row, out TileInfo tileInfo, int? quality = null)
        {
            using (var ms = GetTileAsJpegStream(generator, level, col, row, out tileInfo, quality))
            {
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Get tile as jpeg stream.
        /// </summary>
        /// <param name="generator"></param>
        /// <param name="level"></param>
        /// <param name="col"></param>
        /// <param name="row"></param>
        /// <param name="tileInfo"></param>
        /// <param name="quality"></param>
        /// <returns></returns>
        public static MemoryStream GetTileAsJpegStream(this DeepZoomGenerator generator, int level, int col, int row, out TileInfo tileInfo, int? quality = null)
        {
            return GetTileImage(generator, level, col, row, out tileInfo, quality).ToStream(ImageFormat.Jpeg, quality);

        }

        /// <summary>
        /// Get tile as png.
        /// </summary>
        /// <param name="generator"></param>
        /// <param name="level"></param>
        /// <param name="col"></param>
        /// <param name="row"></param>
        /// <param name="tileInfo"></param>
        /// <param name="quality"></param>
        /// <returns></returns>
        public static byte[] GetTileAsPng(this DeepZoomGenerator generator, int level, int col, int row, out TileInfo tileInfo, int? quality = null)
        {
            using (var ms = GetTileAsPngStream(generator, level, col, row, out tileInfo, quality))
            {
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Get tile as png stream.
        /// </summary>
        /// <param name="generator"></param>
        /// <param name="level"></param>
        /// <param name="col"></param>
        /// <param name="row"></param>
        /// <param name="tileInfo"></param>
        /// <param name="quality"></param>
        /// <returns></returns>
        public static MemoryStream GetTileAsPngStream(this DeepZoomGenerator generator, int level, int col, int row, out TileInfo tileInfo, int? quality = null)
        {
            return GetTileImage(generator, level, col, row, out tileInfo, quality).ToStream(ImageFormat.Png, quality);
        }

        /// <summary>
        /// Get tile image stream.
        /// </summary>
        /// <param name="generator"></param>
        /// <param name="level"></param>
        /// <param name="col"></param>
        /// <param name="row"></param>
        /// <param name="tileInfo"></param>
        /// <param name="quality"></param>
        /// <returns></returns>
        public unsafe static Bitmap GetTileImage(this DeepZoomGenerator generator, int level, int col, int row, out TileInfo tileInfo, int? quality = null)
        {
            if (generator == null)
                throw new NullReferenceException();
            var raw = generator.GetTile(level, col, row, out tileInfo);
            fixed (byte* scan0 = raw)
            {
                return new Bitmap((int)tileInfo.Width, (int)tileInfo.Height, (int)tileInfo.Width * 4, PixelFormat.Format32bppArgb, (IntPtr)scan0);
            }
        }
    }
}
