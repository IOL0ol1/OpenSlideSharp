using BruTile;
using BruTile.Cache;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenSlideSharp.BruTile
{
    public class OpenSlideBase : SlideSourceBase
    {
        public readonly OpenSlideImage SlideImage;
        private readonly bool _enableCache;
        private readonly Action<string> _logger;
        private readonly MemoryCache<byte[]> _tileCache = new MemoryCache<byte[]>();

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
            Schema = new TileSchema
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


        public override IReadOnlyDictionary<string, byte[]> GetExternImages()
        {
            Dictionary<string, byte[]> images = new Dictionary<string, byte[]>();
            var r = Math.Max(Schema.Extent.Height, Schema.Extent.Width) / 512;
            images.Add("preview", GetSlice(new SliceInfo { Extent = Schema.Extent, Resolution = r }));
            foreach (var item in SlideImage.GetAssociatedImages())
            {
                var dim = item.Value.Dimensions;
                images.Add(item.Key, ImageUtil.GetJpeg(item.Value.Data, 4, 4 * (int)dim.Width, (int)dim.Width, (int)dim.Height));
            }
            return images;
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
            var dst = ImageUtil.GetJpeg(bgraData, 4, curTileWidth * 4, curTileWidth, curTileHeight, tileWidth, tileHeight);
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
                bool useInternalWidth = int.TryParse(ExternInfo.TryGetValue($"openslide.level[{i}].tile-width", out var _w) ? (string)_w : null, out var w) && w >= tileWidth;
                bool useInternalHeight = int.TryParse(ExternInfo.TryGetValue($"openslide.level[{i}].tile-height", out var _h) ? (string)_h : null, out var h) && h >= tileHeight;

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
