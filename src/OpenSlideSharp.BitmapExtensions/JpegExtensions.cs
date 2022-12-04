using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace OpenSlideSharp.BitmapExtensions
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
        /// <returns></returns>
        public static unsafe byte[] GetJpeg(byte[] bgraBytes, int bytesPerPixel, int bytesPerLine, int width, int height, int dstWidth = 0, int dstHeight = 0, int? quality = null)
        {
            if (bgraBytes == null) return null;
            if (bytesPerPixel != 3 && bytesPerPixel != 4) throw new ArgumentException(nameof(bytesPerPixel));
            var pixel = bytesPerPixel == 3 ? PixelFormat.Format24bppRgb : PixelFormat.Format32bppRgb;
            fixed (byte* scan0 = bgraBytes)
            {
                using (var bmp = new Bitmap(width, height, bytesPerLine, pixel, (IntPtr)scan0))
                {

                    if ((dstWidth <= 0 && dstHeight <= 0) || (dstWidth == width && dstHeight == height))
                    {
                        return bmp.ToStream(ImageFormat.Jpeg, quality).ToArray();
                    }
                    else  // fill
                    {
                        using (var dstImage = new Bitmap(dstWidth, dstHeight))
                        using (var g = Graphics.FromImage(dstImage))
                        {
                            g.Clear(Color.White);
                            g.DrawImage(bmp, new Point(0, 0));
                            return dstImage.ToStream(ImageFormat.Jpeg, quality).ToArray();
                        }
                    }
                }
            }
        }

    }
}
