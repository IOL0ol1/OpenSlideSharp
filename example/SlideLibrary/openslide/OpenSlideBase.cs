using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BruTile;
using BruTile.Cache;
using OpenSlide.Interop;

namespace SlideLibrary.Openslide
{
    public class OpenSlideBase : SlideSourceBase
    {
        public readonly OpenSlideImage SlideImage;
        private readonly bool _enableCache;
        private readonly Action<string> _logger;
        private readonly MemoryCache<byte[]> _tileCache = new MemoryCache<byte[]>();

        static OpenSlideBase()
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            Environment.SetEnvironmentVariable("PATH", $"{path};{Path.Combine(LibraryInfo.AssemblyDirectory, "openslide", Environment.Is64BitProcess ? "x64" : "x86")}");
        }

        public OpenSlideBase(string source, bool enableCache = true, Action<string> logger = null)
        {
            Source = source;
            _enableCache = enableCache;
            _logger = logger;
            SlideImage = OpenSlideImage.Open(source);
            var minUnitsPerPixel = SlideImage.MicronsPerPixelX ?? SlideImage.MicronsPerPixelY ?? 1;
            MinUnitsPerPixel = UseRealResolution ? minUnitsPerPixel : 1;
            if (MinUnitsPerPixel <= 0) MinUnitsPerPixel = 1;
            var height = SlideImage.Dimensions.Height * MinUnitsPerPixel;
            var width = SlideImage.Dimensions.Width * MinUnitsPerPixel;
            ExternInfo = GetInfo();
            Schema = new TileSchemaFixed
            {
                YAxis = YAxis.OSM,
                Format = "jpg",
                Extent = new Extent(0, -height, width, 0),
                OriginX = 0,
                OriginY = 0,
            };
            InitResolutions(Schema.Resolutions, 256, 256);
        }

        public static string DetectVendor(string source)
        {
            return OpenSlideImage.DetectVendor(source);
        }


        public override byte[] GetExternImage(ImageType type)
        {
            switch (type)
            {
                case ImageType.Preview:
                    var r = Math.Max(Schema.Extent.Height, Schema.Extent.Width) / 512;
                    return  GetSlice(new SliceInfo { Extent = Schema.Extent, Resolution = r });
                default:
                    break;
            }
            return null;
        }

        public override byte[] GetTile(TileInfo tileInfo)
        {
            if (tileInfo == null) return null;
            if (_enableCache && _tileCache.Find(tileInfo.Index) is byte[] output) return output;
            var r = Schema.Resolutions[tileInfo.Index.Level].UnitsPerPixel;
            var tileWidth = Schema.Resolutions[tileInfo.Index.Level].TileWidth;
            var tileHeight = Schema.Resolutions[tileInfo.Index.Level].TileHeight;
            var curLevelOffsetXPixel = tileInfo.Extent.MinX / MinUnitsPerPixel;
            var curLevelOffsetYPixel = -tileInfo.Extent.MaxY / MinUnitsPerPixel;
            var curTileWidth = (int)(tileInfo.Extent.MaxX > Schema.Extent.Width ? tileWidth - (tileInfo.Extent.MaxX - Schema.Extent.Width) / r : tileWidth);
            var curTileHeight = (int)(-tileInfo.Extent.MinY > Schema.Extent.Height ? tileHeight - (-tileInfo.Extent.MinY - Schema.Extent.Height) / r : tileHeight);
            var bgraData = SlideImage.ReadRegion(tileInfo.Index.Level, (long)curLevelOffsetXPixel, (long)curLevelOffsetYPixel, curTileWidth, curTileHeight);
            var dst = ImageUtil.Raw2Jpeg(bgraData, 4, curTileWidth * 4, curTileWidth, curTileHeight, tileWidth, tileHeight);
            if (_enableCache && dst != null)
                _tileCache.Add(tileInfo.Index, dst);
            return dst;
        }

        protected IReadOnlyDictionary<string, object> GetInfo()
        {
            Dictionary<string, object> keys = SlideImage.GetFieldsProperties().ToDictionary(_ => _.Key, _ => _.Value);
            foreach (var item in SlideImage.GetProperties())
            {
                keys.Add(item.Key, item.Value);
            }
            return keys;
        }

        protected void InitResolutions(IDictionary<int, Resolution> resolutions, int tileWidth, int tileHeight)
        {
            for (int i = 0; i < SlideImage.LevelCount; i++)
            {
                bool useInternalWidth = int.TryParse(ExternInfo[$"openslide.level[{i}].tile-width"].ToString(), out var w) && w >= tileWidth;
                bool useInternalHeight = int.TryParse(ExternInfo[$"openslide.level[{i}].tile-height"].ToString(), out var h) && h >= tileHeight;

                bool useInternalSize = useInternalHeight && useInternalWidth;
                var tw = useInternalSize ? w : tileWidth;
                var th = useInternalSize ? h : tileHeight;
                resolutions.Add(i, new Resolution(i, MinUnitsPerPixel * SlideImage.GetLevelDownsample(i), tw, th));
            }
        }

        #region IDisposable
        private bool disposedValue;
        protected override void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    SlideImage.Dispose();
                }
                disposedValue = true;
            }
            base.Dispose(disposing);
        }

        #endregion
    }
}
