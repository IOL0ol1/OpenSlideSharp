using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;

using BruTile;

using OpenCvSharp;

using Point = System.Drawing.Point;

namespace OpenSlideSharp.BruTile
{
    public class ImageUtil
    {

        /// <summary>
        /// BGR/BGRA convert to jpeg
        /// </summary>
        /// <param name="raw">BGR/BGRA</param>
        /// <param name="bytesPerPixel">bytes per pixel</param>
        /// <param name="bytesPerLine">bytes per line</param>
        /// <param name="width">width</param>
        /// <param name="height">height</param>
        /// <param name="dstWidth">dst width</param>
        /// <param name="dstHeight">dst height</param>
        /// <param name="quality">jpeg quality</param>
        /// <returns></returns>
        public static unsafe byte[] GetJpeg(byte[] raw, int bytesPerPixel, int bytesPerLine, int width, int height, int dstWidth = 0, int dstHeight = 0, int? quality = null)
        {
            if (raw == null) return null;
            if (bytesPerPixel != 3 && bytesPerPixel != 4) throw new ArgumentException(nameof(bytesPerPixel));
            var pixel = bytesPerPixel == 3 ? PixelFormat.Format24bppRgb : PixelFormat.Format32bppRgb;
            fixed (byte* scan0 = raw)
            {
                using (var bmp = new Bitmap(width, height, bytesPerLine, pixel, (IntPtr)scan0))
                {

                    if ((dstWidth <= 0 && dstHeight <= 0) || (dstWidth == width && dstHeight == height))
                    {
                        return bmp.ToArray(ImageFormat.Jpeg, quality);
                    }
                    else  // fill
                    {
                        using (var dstImage = new Bitmap(dstWidth, dstHeight))
                        using (var g = Graphics.FromImage(dstImage))
                        {
                            g.Clear(Color.White);
                            g.DrawImage(bmp, new Point(0, 0));
                            return dstImage.ToArray(ImageFormat.Jpeg, quality);
                        }
                    }
                }
            }
        }


        /// <summary>
        /// Join by <paramref name="srcPixelTiles"/> and cut by <paramref name="srcPixelExtent"/> then scale to <paramref name="dstPixelExtent"/>(only height an width is useful).
        /// </summary>
        /// <param name="srcPixelTiles">tile with tile extent collection</param>
        /// <param name="srcPixelExtent">canvas extent</param>
        /// <param name="dstPixelExtent">jpeg output size</param>
        /// <param name="dstQuality">jpeg output quality</param>
        /// <param name="backgroundBGRA">background hex code,default is white</param>
        /// <returns></returns>
        public static byte[] Join(IEnumerable<Tuple<Extent, Mat>> srcPixelTiles, Extent srcPixelExtent, Extent dstPixelExtent, int? dstQuality = 85, uint backgroundBGRA = 0xFFFFFFFF)
        {
            if (srcPixelTiles == null || !srcPixelTiles.Any()) return null;
            try
            {
                srcPixelExtent = srcPixelExtent.ToIntegerExtent();
                dstPixelExtent = dstPixelExtent.ToIntegerExtent();
                var canvasWidth = (int)srcPixelExtent.Width;
                var canvasHeight = (int)srcPixelExtent.Height;
                var dstWidth = (int)dstPixelExtent.Width;
                var dstHeight = (int)dstPixelExtent.Height;
                var bytesPerPixel = 3;
                var pixelFormat = MatType.CV_8UC(bytesPerPixel);
                using (var canvas = new Mat(canvasHeight, canvasWidth, pixelFormat, new Scalar((int)(backgroundBGRA >> 24 & 0xFF), (int)(backgroundBGRA >> 16 & 0xFF), (int)(backgroundBGRA >> 8 & 0xFF), (int)(backgroundBGRA & 0xFF))))
                {
                    foreach (var tile in srcPixelTiles)
                    {
                        var tileExtent = tile.Item1.ToIntegerExtent();
                        var tileRawData = tile.Item2;
                        var intersect = srcPixelExtent.Intersect(tileExtent);

                        var tileOffsetPixelX = (int)(intersect.MinX - tileExtent.MinX);
                        var tileOffsetPixelY = (int)(intersect.MinY - tileExtent.MinY);

                        var canvasOffsetPixelX = (int)(intersect.MinX - srcPixelExtent.MinX);
                        var canvasOffsetPixelY = (int)(intersect.MinY - srcPixelExtent.MinY);

                        using (var tileMat = new Mat((int)tileExtent.Height, (int)tileExtent.Width, pixelFormat, tileRawData.Data, tileRawData.Step()))
                        {
                            var tileRegion = new Mat(tileMat, new Rect(tileOffsetPixelX, tileOffsetPixelY, (int)intersect.Width, (int)intersect.Height));
                            var canvasRegion = new Mat(canvas, new Rect(canvasOffsetPixelX, canvasOffsetPixelY, (int)intersect.Width, (int)intersect.Height));
                            tileRegion.CopyTo(canvasRegion);
                        }
                    }

                    var prms = dstQuality != null ? new int[] { (int)ImwriteFlags.JpegQuality, dstQuality.Value } : null;
                    if (dstWidth != canvasWidth || dstHeight != canvasHeight)
                    {
                        using (var output = new Mat())
                        {
                            Cv2.Resize(canvas, output, new OpenCvSharp.Size(dstWidth, dstHeight));
                            Cv2.ImEncode(".jpg", output, out var buf0, prms);
                            return buf0;
                        }
                    }
                    Cv2.ImEncode(".jpg", canvas, out var buf1, prms);
                    return buf1;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public class TileUtil
    {
        public static int GetLevel(IDictionary<int, Resolution> resolutions, double unitsPerPixel, SampleMode sampleMode = SampleMode.Nearest)
        {
            if (resolutions.Count == 0)
            {
                throw new ArgumentException("No tile resolutions");
            }

            IOrderedEnumerable<KeyValuePair<int, Resolution>> orderedEnumerable = resolutions.OrderByDescending((KeyValuePair<int, Resolution> r) => r.Value.UnitsPerPixel);
            if (orderedEnumerable.Last().Value.UnitsPerPixel > unitsPerPixel)
            {
                return orderedEnumerable.Last().Key;
            }

            if (orderedEnumerable.First().Value.UnitsPerPixel < unitsPerPixel)
            {
                return orderedEnumerable.First().Key;
            }

            switch (sampleMode)
            {
                case SampleMode.Nearest:
                    {
                        int id = -1;
                        double num = double.MaxValue;
                        foreach (KeyValuePair<int, Resolution> item in orderedEnumerable)
                        {
                            double num2 = Math.Abs(item.Value.UnitsPerPixel - unitsPerPixel);
                            if (num2 < num)
                            {
                                id = item.Key;
                                num = num2;
                            }
                        }

                        if (id == -1)
                        {
                            throw new Exception("Unexpected error when calculating nearest level");
                        }

                        return id;
                    }
                case SampleMode.NearestUp:
                    return orderedEnumerable.Last(_ => _.Value.UnitsPerPixel >= unitsPerPixel).Key;
                case SampleMode.NearestDwon:
                    return orderedEnumerable.First(_ => _.Value.UnitsPerPixel <= unitsPerPixel).Key;
                case SampleMode.Top:
                    return orderedEnumerable.First().Key;
                case SampleMode.Bottom:
                    return orderedEnumerable.Last().Key;
                default:
                    throw new Exception($"Unexpected error {nameof(sampleMode)}");
            }
        }
    }

    public static class BitmapEx
    {
        private static IDictionary<Guid, IList<ImageCodecInfo>> encoders = new Dictionary<Guid, IList<ImageCodecInfo>>();

        static BitmapEx()
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

        public static byte[] ToArray(this Image bitmap, ImageFormat format, int? quality = null)
        {
            using (var ms = new MemoryStream())
            {
                EncoderParameters parameters = quality.HasValue ? new EncoderParameters() { Param = new[] { new EncoderParameter(Encoder.Quality, quality.Value) } } : null;
                bitmap.Save(ms, format?.FindCodec(), parameters);
                return ms.GetBuffer();
            }
        }

        public static ImageCodecInfo FindCodec(this ImageFormat format)
        {
            if (encoders.ContainsKey(format.Guid))
                return encoders[format.Guid].FirstOrDefault();

            return null;
        }
    }

}
