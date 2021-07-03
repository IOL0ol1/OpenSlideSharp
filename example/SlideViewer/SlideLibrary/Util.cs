using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using BruTile;
using OpenCvSharp;
using TurboJpegWrapper;
using Point = System.Drawing.Point;

namespace SlideLibrary
{
    public static class LibraryInfo
    {
        public static string AssemblyLocation => Assembly.GetCallingAssembly().Location;
        public static string AssemblyDirectory => Directory.GetParent(AssemblyLocation)?.FullName;
    }

    public class BgraData
    {
        static BgraData()
        {
            var directoryName = Path.GetDirectoryName(typeof(ImageUtil).Assembly.Location);
            var platformName = $"win-{(Environment.Is64BitProcess ? "x64" : "x86")}";
            var dllPath = Path.Combine(directoryName, platformName);
            TJInitializer.Initialize(dllPath, logger: _ => Trace.TraceInformation(_));
        }


        public BgraData(int width, int height, int stride, byte[] bgraData, int bytesPerPixel)
        {
            Width = width;
            Height = height;
            Stride = stride;
            Data = bgraData;
            BytePerPixel = bytesPerPixel;
        }

        public BgraData(byte[] jpegData, bool isConvert2Bgra = true)
        {
            using (var jpegDecode = new TJDecompressor())
            {
                var bgra = jpegDecode.Decompress(jpegData, isConvert2Bgra ? TJPixelFormats.TJPF_BGRA : TJPixelFormats.TJPF_BGR, TJFlags.NONE);
                Width = bgra.Width;
                Height = bgra.Height;
                Stride = bgra.RowBytes;
                Data = bgra.Data;
                BytePerPixel = isConvert2Bgra ? 4 : 3;
            }
        }

        /// <summary>
        /// Pixel width
        /// </summary>
        public int Width { get; private set; }

        /// <summary>
        /// Pixel height.
        /// </summary>
        public int Height { get; private set; }

        /// <summary>
        /// Byte count per line
        /// </summary>
        public int Stride { get; private set; }

        /// <summary>
        /// Byte count per pixel
        /// </summary>
        public int BytePerPixel { get; private set; }

        /// <summary>
        /// data
        /// </summary>
        public byte[] Data { get; private set; }

        public byte[] ToJpeg(int quality = 100)
        {
            using (var jpegEncode = new TJCompressor())
            {
                return jpegEncode.Compress(Data, Stride, Width, Height, BytePerPixel == 4 ? TJPixelFormats.TJPF_BGRA : TJPixelFormats.TJPF_BGR, TJSubsamplingOptions.TJSAMP_420, quality, TJFlags.NONE);
            }
        }
    }

    public class ImageUtil
    {

        /// <summary>
        /// 将BGR或BGRA的原始数据转换为jpg编码后的数据，如果指定了输出大小，左上角对齐到指定大小，其它区域填充白色
        /// </summary>
        /// <param name="raw">BGR数据</param>
        /// <param name="bytesPerLine">每行的字节数</param>
        /// <param name="width">宽度</param>
        /// <param name="height">高度</param>
        /// <param name="dstWidth">目标缩放宽度</param>
        /// <param name="dstHeight">目标缩放高度</param>
        /// <returns></returns>
        public static byte[] Raw2Jpeg(byte[] raw, int bytesPerPixel, int bytesPerLine, int width, int height, int dstWidth = 0, int dstHeight = 0)
        {
            if (raw == null) return null;
            if (bytesPerPixel != 3 && bytesPerPixel != 4) throw new ArgumentException(nameof(bytesPerPixel));
            var pixel = bytesPerPixel == 3 ? PixelFormat.Format24bppRgb : PixelFormat.Format32bppRgb;
            using (var bmp = new Bitmap(width, height))
            {
                var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, pixel);
                var lineSize = Math.Min(bytesPerLine, data.Stride);
                for (int i = 0; i < height; i++)
                {
                    Marshal.Copy(raw, bytesPerLine * i, data.Scan0 + data.Stride * i, lineSize);
                }
                bmp.UnlockBits(data);

                // 填充
                if ((dstWidth <= 0 && dstHeight <= 0) || (dstWidth == width && dstHeight == height))
                {
                    return bmp.ToArray(ImageFormat.Jpeg);
                }
                else
                {
                    using (var dstImage = new Bitmap(dstWidth, dstHeight))
                    using (var g = Graphics.FromImage(dstImage))
                    {
                        g.Clear(Color.White);
                        g.DrawImage(bmp, new Point(0, 0));
                        return dstImage.ToArray(ImageFormat.Jpeg);
                    }
                }
            }
        }


        public static unsafe byte[] BitmapWithTrans2Jpeg(Bitmap bitmap)
        {
            var bitmapData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppRgb);
            try
            {
                using (var mat = new Mat(bitmapData.Height, bitmapData.Width, MatType.CV_8UC4, bitmapData.Scan0, bitmapData.Stride))
                {
                    // 黑色透明区域转白色不透明
                    mat.ForEachAsVec4b((_v, _i) => { if (_v->Item3 == 0) _v->Item0 = _v->Item1 = _v->Item2 = _v->Item3 = 255; });
                    return mat.ToBytes(".jpg");
                }
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }
        }

        public static BgraData Jpeg2Bgra(byte[] jpeg)
        {
            return new BgraData(jpeg);
        }

        /// <summary>
        /// 将 <paramref name="srcPixelTiles"/> 的图像拼接之后，通过 <paramref name="srcPixelExtent"/> 框取有用区域，并缩放到 <paramref name="dstPixelExtent"/> 大小
        /// </summary>
        /// <param name="srcPixelTiles">瓦片集合，包含瓦片的区域和瓦片数据</param>
        /// <param name="srcPixelExtent">框取大小，从拼合的瓦片中框取的区域</param>
        /// <param name="dstPixelExtent">目标大小，内部只使用其宽高值</param>
        /// <param name="dstQuality">压缩质量</param>
        /// <param name="backgroundBGRA">背景色(默认填充为白色)</param>
        /// <returns></returns>
        public static byte[] Join(IEnumerable<Tuple<Extent, BgraData>> srcPixelTiles, Extent srcPixelExtent, Extent dstPixelExtent, int? dstQuality = 85, uint backgroundBGRA = 0xFFFFFFFF)
        {
            // 15ms
            if (srcPixelTiles == null || !srcPixelTiles.Any()) return null;
            try
            {
                srcPixelExtent = srcPixelExtent.ToIntegerExtent();
                dstPixelExtent = dstPixelExtent.ToIntegerExtent();
                var canvasWidth = (int)srcPixelExtent.Width;
                var canvasHeight = (int)srcPixelExtent.Height;
                var dstWidth = (int)dstPixelExtent.Width;
                var dstHeight = (int)dstPixelExtent.Height;
                var bytesPerPixel = 4;
                var pixelFormat = MatType.CV_8UC(bytesPerPixel);
                using (var canvas = new Mat(canvasHeight, canvasWidth, pixelFormat, new Scalar((int)(backgroundBGRA >> 24 & 0xFF), (int)(backgroundBGRA >> 16 & 0xFF), (int)(backgroundBGRA >> 8 & 0xFF), (int)(backgroundBGRA & 0xFF))))
                {
                    foreach (var tile in srcPixelTiles)
                    {
                        var tileExtent = tile.Item1.ToIntegerExtent();
                        var tileRawData = tile.Item2;
                        var intersect = srcPixelExtent.Intersect(tileExtent); // 画布和瓦片的重叠区域

                        /// 重叠区域在瓦片中的像素坐标偏移
                        var tileOffsetPixelX = (int)(intersect.MinX - tileExtent.MinX);
                        var tileOffsetPixelY = (int)(intersect.MinY - tileExtent.MinY);

                        /// 重叠区域在画布中的像素坐标偏移
                        var canvasOffsetPixelX = (int)(intersect.MinX - srcPixelExtent.MinX);
                        var canvasOffsetPixelY = (int)(intersect.MinY - srcPixelExtent.MinY);

                        /// 将瓦片中的指定区域拷贝到画布对应位置
                        using (var tileMat = new Mat((int)tileExtent.Height, (int)tileExtent.Width, pixelFormat, tileRawData.Data, tileRawData.Stride))
                        {
                            var tileRegion = new Mat(tileMat, new Rect(tileOffsetPixelX, tileOffsetPixelY, (int)intersect.Width, (int)intersect.Height));
                            var canvasRegion = new Mat(canvas, new Rect(canvasOffsetPixelX, canvasOffsetPixelY, (int)intersect.Width, (int)intersect.Height));
                            tileRegion.CopyTo(canvasRegion);
                        }
                    }

                    /// 获取图像编码压缩质量
                    var prms = dstQuality != null ? new int[] { (int)ImwriteFlags.JpegQuality, dstQuality.Value } : null;
                    /// 如果需要调整输出大小，缩放画布到指定大小
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

        /// <summary>
        /// 从左上角将指定图像绘制到指定大小指定颜色的画布，
        /// </summary>
        /// <param name="img">已编码过的图像数据</param>
        /// <param name="dstWidth">目标画布的宽度</param>
        /// <param name="dstHeight">目标画布的高度</param>
        /// <param name="dstBackground">目标画布的背景色ARGB，默认白色</param>
        /// <returns></returns>
        public static byte[] Fill(byte[] img, int dstWidth, int dstHeight, uint dstBackground = 0xFFFFFFFF)
        {

            if (img == null) return null;
            using (var bitmap = new Bitmap(new MemoryStream(img)))
            {
                if (bitmap.Width != dstWidth || bitmap.Height != dstHeight)
                {
                    /// 确保output和bitmap的dpi一致
                    using (var bg0 = Graphics.FromImage(bitmap))
                    using (var output = new Bitmap(dstWidth, dstHeight, bg0))
                    using (var bg1 = Graphics.FromImage(output))
                    {
                        bg1.Clear(Color.FromArgb((int)(dstBackground >> 24 & 0xFF), (int)(dstBackground >> 16 & 0xFF), (int)(dstBackground >> 8 & 0xFF), (int)(dstBackground & 0xFF)));
                        bg1.DrawImage(bitmap, 0, 0);
                        return output.ToArray(ImageFormat.Jpeg);
                    }
                }
            }
            return img;
        }

        /// <summary>
        /// 将指定图像拉伸填充缩放到指定画布大小
        /// </summary>
        /// <param name="img"></param>
        /// <param name="dstWidth"></param>
        /// <param name="dstHeight"></param>
        /// <param name="quality">压缩质量</param>
        /// <returns></returns>
        public static byte[] Scale(byte[] img, int dstWidth, int dstHeight, int? quality = null)
        {
            if (img == null) return null;
            using (var bitmap = new Bitmap(new MemoryStream(img)))
            {
                if (bitmap.Width != dstWidth || bitmap.Height != dstHeight)
                {
                    using (var bg0 = Graphics.FromImage(bitmap)) // DPI 一致性
                    using (var tmp = new Bitmap(dstWidth, dstHeight, bg0))
                    using (var gc = Graphics.FromImage(tmp))
                    {
                        gc.Clear(Color.White);
                        gc.DrawImage(bitmap, 0, 0, dstWidth, dstHeight);
                        return tmp.ToArray(ImageFormat.Jpeg, quality);
                    }
                }
            }
            return img;
        }
    }


    public class Utilities
    {
        public static int GetLevel(IDictionary<int, Resolution> resolutions, double unitsPerPixel, SampleMode sampleMode)
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

        /// <summary>
        /// 将编码器列表转换为字典提高查询速度
        /// </summary>
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
