using OpenCvSharp;
using System;
using System.IO;
using static OpenSlideSharp.DeepZoomGenerator;

namespace OpenSlideSharp.OpencvExtensions
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
        /// <param name="quality">For JPEG, it can be a quality from 0 to 100 (the higher is the better). Default
        /// value is 95.</param>
        /// <returns></returns>
        public static byte[] GetTileAsJpeg(this DeepZoomGenerator generator, int level, int col, int row, out TileInfo tileInfo, int? quality = null)
        {
            using (var mat = GetTileImage(generator, level, col, row, out tileInfo))
            {
                var prms = quality != null ? new int[] { (int)ImwriteFlags.JpegQuality, quality.Value } : null;
                return mat.ToBytes(".jpg", prms);
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
        /// <param name="quality">For JPEG, it can be a quality from 0 to 100 (the higher is the better). Default
        /// value is 95.</param>
        /// <returns></returns>
        public static MemoryStream GetTileAsJpegStream(this DeepZoomGenerator generator, int level, int col, int row, out TileInfo tileInfo, int? quality = null)
        {
            return new MemoryStream(GetTileAsJpeg(generator, level, col, row, out tileInfo, quality));
        }

        /// <summary>
        /// Get tile as png.
        /// </summary>
        /// <param name="generator"></param>
        /// <param name="level"></param>
        /// <param name="col"></param>
        /// <param name="row"></param>
        /// <param name="tileInfo"></param>
        /// <param name="quality">        
        /// For PNG, it can be the compression level from 0 to 9. A higher value means a
        /// smaller size and longer compression time. Default value is 3.
        /// </param>
        /// <returns></returns>
        public static byte[] GetTileAsPng(this DeepZoomGenerator generator, int level, int col, int row, out TileInfo tileInfo, int? quality = null)
        {
            using (var mat = GetTileImage(generator, level, col, row, out tileInfo))
            {
                var prms = quality != null ? new int[] { (int)ImwriteFlags.PngCompression, quality.Value } : null;
                return mat.ToBytes(".png", prms);
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
        /// <param name="quality">        
        /// For PNG, it can be the compression level from 0 to 9. A higher value means a
        /// smaller size and longer compression time. Default value is 3.</param>
        /// <returns></returns>
        public static MemoryStream GetTileAsPngStream(this DeepZoomGenerator generator, int level, int col, int row, out TileInfo tileInfo, int? quality = null)
        {
            return new MemoryStream(GetTileAsPng(generator, level, col, row, out tileInfo, quality));
        }

        /// <summary>
        /// Get tile mat.
        /// </summary>
        /// <param name="generator"></param>
        /// <param name="level"></param>
        /// <param name="col"></param>
        /// <param name="row"></param>
        /// <param name="tileInfo"></param>
        /// <returns></returns>
        public unsafe static Mat GetTileImage(this DeepZoomGenerator generator, int level, int col, int row, out TileInfo tileInfo)
        {
            if (generator == null)
                throw new NullReferenceException();
            var raw = generator.GetTile(level, col, row, out tileInfo);
            fixed (byte* scan0 = raw)
            {
                return new Mat((int)tileInfo.Height, (int)tileInfo.Width, MatType.CV_8UC4, (IntPtr)scan0, (int)tileInfo.Width * 4);
            }
        }
    }
}
