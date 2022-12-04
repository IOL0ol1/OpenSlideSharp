using OpenCvSharp;
using System;

namespace OpenSlideSharp.OpencvExtensions
{
    /// <summary>
    /// 
    /// </summary>
    public class JpegExtensions
    {
        /// <summary>
        /// BGR/BGRA convert to jpeg
        /// </summary>
        /// <param name="bgraBytes">BGR/BGRA</param>
        /// <param name="bytesPerPixel">bytes per pixel</param>
        /// <param name="bytesPerLine">bytes per line</param>
        /// <param name="width">width</param>
        /// <param name="height">height</param>
        /// <param name="dstWidth">dst width</param>
        /// <param name="dstHeight">dst height</param>
        /// <param name="quality">jpeg quality</param>
        /// <param name="background">background color for transparent if <paramref name="bytesPerPixel"/> == 4</param>
        /// <returns></returns>
        public static unsafe byte[] GetJpeg(byte[] bgraBytes, int bytesPerPixel, int bytesPerLine, int width, int height, int dstWidth = 0, int dstHeight = 0, int? quality = null, uint background = 0xFFFFFFFF)
        {
            if (bgraBytes == null) return null;
            if (bytesPerPixel != 3 && bytesPerPixel != 4) throw new ArgumentException(nameof(bytesPerPixel));
            var prms = quality != null ? new int[] { (int)ImwriteFlags.JpegQuality, quality.Value } : null;
            var pixel = MatType.CV_8UC(bytesPerPixel);
            fixed (byte* scan0 = bgraBytes)
            {
                using (var src = new Mat(height, width, pixel, (IntPtr)scan0, bytesPerLine))
                {
                    // black transparent to background
                    if (pixel.Channels == 4)
                    {
                        unchecked
                        {
                            src.ForEachAsInt32((_i, _p) =>
                            {
                                if (*_i == 0) *_i = (Int32)(background);
                            });
                        }
                    }
                    if ((dstWidth <= 0 && dstHeight <= 0) || (dstWidth == width && dstHeight == height))
                    {
                        return src.ToBytes(".jpg", prms);
                    }
                    else  // fill 
                    {
                        var scalar = new Scalar((int)(background >> 24 & 0xFF), (int)(background >> 16 & 0xFF), (int)(background >> 8 & 0xFF), (int)(background & 0xFF));
                        using (var dst = new Mat(dstHeight, dstWidth, pixel, scalar))
                        {
                            DrawImage(src, dst);
                            return dst.ToBytes(".jpg", prms);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Fill the source image with adaptive scaling to the target image
        /// </summary>
        /// <param name="src"></param>
        /// <param name="dst"></param>
        private static void DrawImage(Mat src, Mat dst)
        {
            var fx = (double)dst.Width / src.Width;
            var fy = (double)dst.Height / src.Height;
            var fmin = Math.Min(fx, fy);
            using (var srcResized = src.Resize(new Size(src.Width * fmin, src.Height * fmin)))
            {
                using (var sub = new Mat(dst, new Rect(0, 0, srcResized.Width, srcResized.Height)))
                {
                    srcResized.CopyTo(sub);
                }
            }
        }
    }
}
