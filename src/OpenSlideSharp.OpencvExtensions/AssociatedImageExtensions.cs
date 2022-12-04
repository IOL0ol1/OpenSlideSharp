using OpenCvSharp;
using System;
using System.IO;

namespace OpenSlideSharp.OpencvExtensions
{
    /// <summary>
    /// 
    /// </summary>
    public static class AssociatedImageExtensions
    {
        /// <summary>
        /// To mat.
        /// </summary>
        /// <param name="image"></param>
        /// <returns></returns>
        public unsafe static Mat ToMat(this AssociatedImage image)
        {
            if (image == null) throw new NullReferenceException();
            fixed (byte* scan0 = image.Data)
            {
                return new Mat((int)image.Dimensions.Height, (int)image.Dimensions.Width, MatType.CV_8UC4, (IntPtr)scan0, (int)image.Dimensions.Width * 4);
            }
        }

        /// <summary>
        /// To jpeg array
        /// </summary>
        /// <param name="image"></param>
        /// <param name="quality">
        /// For JPEG, it can be a quality from 0 to 100 (the higher is the better). Default
        /// value is 95.
        /// </param>
        /// <returns></returns>

        public static byte[] ToJpeg(this AssociatedImage image, int? quality = null)
        {
            var prms = quality != null ? new int[] { (int)ImwriteFlags.JpegQuality, quality.Value } : null;
            using (var mat = ToMat(image))
            {
                return mat.ToBytes(".jpg", prms);
            }
        }

        /// <summary>
        /// To jpeg stream
        /// </summary>
        /// <param name="image"></param>
        /// <param name="quality">
        /// For JPEG, it can be a quality from 0 to 100 (the higher is the better). Default
        /// value is 95.
        /// </param>
        /// <returns></returns>

        public static MemoryStream ToJpegStream(this AssociatedImage image, int? quality = null)
        {
            return new MemoryStream(ToJpeg(image, quality));
        }

        /// <summary>
        /// To png array.
        /// </summary>
        /// <param name="image"></param>
        /// <param name="quality">
        /// For PNG, it can be the compression level from 0 to 9. A higher value means a
        /// smaller size and longer compression time. Default value is 3.
        /// </param>
        /// <returns></returns>
        public static byte[] ToPng(this AssociatedImage image, int? quality = null)
        {
            var prms = quality != null ? new int[] { (int)ImwriteFlags.PngCompression, quality.Value } : null;
            using (var mat = ToMat(image))
            {
                return mat.ToBytes(".png", prms);
            }
        }

        /// <summary>
        /// To png stream.
        /// </summary>
        /// <param name="image"></param>
        /// <param name="quality">
        /// For PNG, it can be the compression level from 0 to 9. A higher value means a
        /// smaller size and longer compression time. Default value is 3.
        /// </param>
        /// <returns></returns>
        public static MemoryStream ToPngStream(this AssociatedImage image, int? quality = null)
        {
            return new MemoryStream(ToPng(image, quality));
        }
    }


}
