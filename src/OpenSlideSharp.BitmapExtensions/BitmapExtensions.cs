using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace OpenSlideSharp
{
    /// <summary>
    /// 
    /// </summary>
    public static class BitmapExtensions
    {
        private static IDictionary<Guid, IList<ImageCodecInfo>> encoders = new Dictionary<Guid, IList<ImageCodecInfo>>();

        static BitmapExtensions()
        {
            ImageCodecInfo[] array = ImageCodecInfo.GetImageEncoders();
            foreach (var coder in array)
            {
                if (encoders.ContainsKey(coder.FormatID))
                    encoders[coder.FormatID].Add(coder);
                else
                    encoders[coder.FormatID] = new List<ImageCodecInfo>(new[] { coder });
            }
        }

        /// <summary>
        /// To Stream
        /// </summary>
        /// <param name="bitmap"></param>
        /// <param name="format">image out format,null is JPEG</param>
        /// <param name="quality">image quality</param>
        /// <returns></returns>
        public static MemoryStream ToStream(this Image bitmap, ImageFormat format = null, int? quality = null)
        {
            if (bitmap == null) throw new NullReferenceException();
            var ms = new MemoryStream();
            EncoderParameters parameters = quality.HasValue ? new EncoderParameters() { Param = new[] { new EncoderParameter(Encoder.Quality, quality.Value) } } : null;
            bitmap.Save(ms, (format??ImageFormat.Jpeg).FindCodec(), parameters);
            ms.Seek(0, SeekOrigin.Begin);
            return ms;
        }

        /// <summary>
        /// Find codec for format 
        /// </summary>
        /// <param name="format"></param>
        /// <returns></returns>
        public static ImageCodecInfo FindCodec(this ImageFormat format)
        {
            if (encoders.ContainsKey(format.Guid))
                return encoders[format.Guid].FirstOrDefault();

            return null;
        }
    }


}
