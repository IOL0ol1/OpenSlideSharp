using BruTile;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenSlideSharp.BruTile
{
    public class ImageUtil
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
                            src.SaveImage($"{Guid.NewGuid()}.jpg");
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
            if (fmin < 1) // src > dst
            {
                using (var srcResized = src.Resize(new Size(src.Width * fmin, src.Height * fmin)))
                {
                    using (var sub = new Mat(dst, new Rect(0, 0, srcResized.Width, srcResized.Height)))
                    {
                        srcResized.CopyTo(sub);
                    }
                }
            }
            else // src <= dst
            {
                using (var sub = new Mat(dst, new Rect(0, 0, src.Width, src.Height)))
                {
                    src.CopyTo(sub);
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
        /// <summary>
        /// To ensure image quality, try to use high-resolution level downsampling to low-resolution level 
        /// </summary>
        /// <param name="resolutions"></param>
        /// <param name="unitsPerPixel"></param>
        /// <param name="sampleMode"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        /// <exception cref="Exception"></exception>
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
}
