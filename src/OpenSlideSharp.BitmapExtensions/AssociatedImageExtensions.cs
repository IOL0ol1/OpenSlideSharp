using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace OpenSlideSharp.BitmapExtensions
{
    /// <summary>
    /// 
    /// </summary>
    public static class AssociatedImageExtensions
    {
        /// <summary>
        /// To bitmap.
        /// </summary>
        /// <param name="image"></param>
        /// <returns></returns>
        public unsafe static Bitmap ToBitmap(this AssociatedImage image)
        {
            if (image == null) throw new NullReferenceException();
            fixed (byte* scan0 = image.Data)
            {
                return new Bitmap((int)image.Dimensions.Width, (int)image.Dimensions.Height, (int)image.Dimensions.Width * 4, PixelFormat.Format32bppArgb, (IntPtr)scan0);
            }
        }

        /// <summary>
        /// To jpeg array
        /// </summary>
        /// <param name="image"></param>
        /// <param name="quality"></param>
        /// <returns></returns>

        public static byte[] ToJpeg(this AssociatedImage image, int? quality = null)
        {
            using (var ms = ToJpegStream(image, quality))
            {
                return ms.ToArray();
            }
        }

        /// <summary>
        /// To jpeg stream
        /// </summary>
        /// <param name="image"></param>
        /// <param name="quality"></param>
        /// <returns></returns>

        public static MemoryStream ToJpegStream(this AssociatedImage image, int? quality = null)
        {
            return ToBitmap(image).ToStream(ImageFormat.Jpeg, quality);
        }

        /// <summary>
        /// To png array.
        /// </summary>
        /// <param name="image"></param>
        /// <param name="quality"></param>
        /// <returns></returns>
        public static byte[] ToPng(this AssociatedImage image, int? quality = null)
        {
            using (var ms = ToPngStream(image, quality))
            {
                return ms.ToArray();
            }
        }

        /// <summary>
        /// To png stream.
        /// </summary>
        /// <param name="image"></param>
        /// <param name="quality"></param>
        /// <returns></returns>
        public static MemoryStream ToPngStream(this AssociatedImage image, int? quality = null)
        {
            return ToBitmap(image).ToStream(ImageFormat.Png, quality);
        }
    }


}
