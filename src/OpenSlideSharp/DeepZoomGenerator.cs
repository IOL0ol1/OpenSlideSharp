// See openslide-python (https://github.com/openslide/openslide-python)
// See OpenSlideNET (https://github.com/yigolden/OpenSlideNET)
// *Changed some code supported net3.5.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;


namespace OpenSlideSharp
{
    /// <summary>
    /// Deep zoom generator for OpenSlide image.
    /// </summary>
    public class DeepZoomGenerator : IDisposable
    {
        private OpenSlideImage _image;
        private readonly long _boundX;
        private readonly long _boundY;
        private readonly long _width;
        private readonly long _height;
        private readonly int _tileSize;
        private readonly int _overlap;
        private readonly DeepZoomLayerInformation[] _layers;

        /// <summary>
        /// Get the underlying OpenSlide image object.
        /// </summary>
        public OpenSlideImage Image => _image;

        /// <summary>
        /// Initialize a <see cref="DeepZoomGenerator"/> instance with the specified parameters.
        /// </summary>
        /// <param name="image">The OpenSlide image.</param>
        /// <param name="tileSize">The tile size paramter.</param>
        /// <param name="overlap">The overlap paramter.</param>
        /// <param name="limitBounds">Whether image bounds should be respected.</param>
        /// <param name="isOwner">Whether the OpenSlide image instance should be disposed when this <see cref="DeepZoomGenerator"/> instance is disposed.</param>
        public DeepZoomGenerator(OpenSlideImage image, int tileSize = 254, int overlap = 1, bool limitBounds = true, bool isOwner = true)
        {
            _image = image ?? throw new ArgumentNullException(nameof(image));
            disposedValue = !isOwner;

            long width, height;
            var size = image.Dimensions;
            if (limitBounds)
            {
                _boundX = image.BoundsX ?? 0;
                _boundY = image.BoundsY ?? 0;
                _width = width = image.BoundsWidth ?? size.Width;
                _height = height = image.BoundsHeight ?? size.Height;
            }
            else
            {
                _boundX = 0;
                _boundY = 0;
                _width = width = size.Width;
                _height = height = size.Height;
            }

            DeepZoomLayer[] dzLayers = DeepZoomLayer.CalculateDeepZoomLayers(width, height);
            DeepZoomLayerInformation[] layers = new DeepZoomLayerInformation[dzLayers.Length];

            for (int i = 0; i < layers.Length; i++)
            {
                DeepZoomLayer dzLayer = dzLayers[i];
                int layerDownsample = 1 << (dzLayers.Length - i - 1);

                int level = image.GetBestLevelForDownsample(layerDownsample);
                (long levelWidth, long levelHeight) = image.GetLevelDimension(level);
                layers[i] = new DeepZoomLayerInformation
                {
                    Level = level,
                    LevelDownsample = image.GetLevelDownsample(level),
                    LevelWidth = levelWidth,
                    LevelHeight = levelHeight,
                    LayerDownsample = layerDownsample,
                    LayerWidth = dzLayer.Width,
                    LayerHeight = dzLayer.Height
                };
            }

            _layers = layers;
            _tileSize = tileSize;
            _overlap = overlap;
        }

        /// <summary>
        /// The Count of the deep zoom level.
        /// </summary>
        public int LevelCount => _layers.Length;

        private IEnumerable<ImageDimension> _levelTilesCache;

        /// <summary>
        /// The number of tiles in each level.
        /// </summary>
        public IEnumerable<ImageDimension> LevelTiles
            => _levelTilesCache != null ? _levelTilesCache : _levelTilesCache = _layers.Select(l => new ImageDimension((int)((l.LayerWidth + _tileSize - 1) / _tileSize), (int)((l.LayerHeight + _tileSize - 1) / _tileSize)));

        private IEnumerable<ImageDimension> _levelDimensionsCache;

        /// <summary>
        /// The dimensions of each level.
        /// </summary>
        public IEnumerable<ImageDimension> LevelDimensions
            => _levelDimensionsCache != null ? _levelDimensionsCache : _levelDimensionsCache = _layers.Select(l => new ImageDimension(l.LayerWidth, l.LayerHeight));

        private int? _tileCountCache;

        /// <summary>
        /// The total number of tiles.
        /// </summary>
        public int TileCount
            => _tileCountCache.HasValue ? _tileCountCache.Value : (_tileCountCache = (int)_layers.Sum(l => (l.LayerWidth + _tileSize - 1) * (l.LayerHeight + _tileSize - 1) / _tileSize / _tileSize)).GetValueOrDefault();

        /// <summary>
        /// Get the pre-multiplied BGRA data for the specified tile.
        /// </summary>
        /// <param name="level">The deep zoom level.</param>
        /// <param name="col">Horizontal tile index.</param>
        /// <param name="row">Vertical tile index.</param>
        /// <param name="info">Information of the specified tile.</param>
        /// <returns>Pre-multiplied BGRA image data.</returns>
        public byte[] GetTile(int level, int col, int row, out TileInfo info)
        {
            info = GetTileInfo(level, col, row);
            return _image.ReadRegion(info.SlideLevel, info.X, info.Y, info.Width, info.Height);
        }

        /// <summary>
        /// Get information of the specified tile.
        /// </summary>
        /// <param name="level">The deep zoom level.</param>
        /// <param name="col">Horizontal tile index.</param>
        /// <param name="row">Vertical tile index.</param>
        /// <returns>Information of the specified tile.</returns>
        public TileInfo GetTileInfo(int level, int col, int row)
        {
            var layers = _layers;
            if ((uint)level >= (uint)layers.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(level));
            }
            var layer = layers[level];
            int tileSize = _tileSize;
            int overlap = _overlap;
            int horizontalTileCount = (int)((layer.LayerWidth + tileSize - 1) / tileSize);
            int verticalTileCount = (int)((layer.LayerHeight + tileSize - 1) / tileSize);
            if ((uint)col >= (uint)horizontalTileCount)
            {
                throw new ArgumentOutOfRangeException(nameof(col));
            }
            if ((uint)row >= (uint)verticalTileCount)
            {
                throw new ArgumentOutOfRangeException(nameof(row));
            }

            long offsetX = (long)col * tileSize;
            long offsetY = (long)row * tileSize;
            int width = (int)Math.Min(tileSize, layer.LayerWidth - offsetX);
            int height = (int)Math.Min(tileSize, layer.LayerHeight - offsetY); ;
            if (col != 0)
            {
                offsetX -= overlap;
                width += overlap;
            }
            if (col != horizontalTileCount - 1)
            {
                width += overlap;
            }
            if (row != 0)
            {
                offsetY -= overlap;
                height += overlap;
            }
            if (row != verticalTileCount - 1)
            {
                height += overlap;
            }

            return new TileInfo
            {
                X = _boundX + offsetX * layer.LayerDownsample,
                Y = _boundY + offsetY * layer.LayerDownsample,
                SlideLevel = layer.Level,
                Width = (long)(width * layer.LayerDownsample / layer.LevelDownsample),
                Height = (long)(height * layer.LayerDownsample / layer.LevelDownsample),
                TileWidth = width,
                TileHeight = height
            };
        }

        /// <summary>
        /// Get the dzi file content.
        /// </summary>
        /// <param name="format">The iamge format.</param>
        /// <returns>Dzi file content.</returns>
        public string GetDzi(string format = "jpeg")
        {
            return
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<Image xmlns=\"http://schemas.microsoft.com/deepzoom/2008\" " +
                "Format=\"" + format + "\" " +
                "Overlap=\"" + _overlap + "\" " +
                "TileSize=\"" + _tileSize + "\">" +
                "<Size Height=\"" + _height + "\" Width=\"" + _width + "\" />" +
                "</Image>";
        }

        /// <summary>
        /// Information of a tile.
        /// </summary>
        public class TileInfo
        {
            /// <summary>
            /// The X coordinate in the base level.
            /// </summary>
            public long X { get; set; }


            /// <summary>
            /// The Y coordinate in the base level.
            /// </summary>
            public long Y { get; set; }

            /// <summary>
            /// The corresponding level in the OpenSlide image.
            /// </summary>
            public int SlideLevel { get; set; }

            /// <summary>
            /// Width of the image to read from OpenSlide image.
            /// </summary>
            public long Width { get; set; }

            /// <summary>
            /// Height of the image to read from OpenSlide image.
            /// </summary>
            public long Height { get; set; }

            /// <summary>
            /// The width of the deep zoom tile.
            /// </summary>
            public int TileWidth { get; set; }


            /// <summary>
            /// The height of the deep zoom tile.
            /// </summary>
            public int TileHeight { get; set; }
        }

        private class DeepZoomLayerInformation
        {
            public int Level { get; set; }
            public long LevelWidth { get; set; }
            public long LevelHeight { get; set; }
            public double LevelDownsample { get; set; }
            public int LayerDownsample { get; set; }
            public long LayerWidth { get; set; }
            public long LayerHeight { get; set; }
        }

        private readonly struct DeepZoomLayer
        {
            public long Width { get; }
            public long Height { get; }

            public DeepZoomLayer(long width, long height)
            {
                Width = width;
                Height = height;
            }
            public static DeepZoomLayer[] CalculateDeepZoomLayers(long width, long height)
            {
                if (width <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(width));
                }
                if (height <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(height));
                }
                var layers = new List<DeepZoomLayer>();
                DeepZoomLayer currentLayer = new DeepZoomLayer(width, height);
                layers.Add(currentLayer);
                while (currentLayer.Width != 1 || currentLayer.Height != 1)
                {
                    currentLayer = new DeepZoomLayer((currentLayer.Width + 1) / 2, (currentLayer.Height + 1) / 2);
                    layers.Add(currentLayer);
                }
                DeepZoomLayer[] layersArray = layers.ToArray();
                Array.Reverse(layersArray);
                return layersArray;
            }
        }

        private bool disposedValue;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="disposing"></param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _image.Dispose();
                }

                disposedValue = true;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
