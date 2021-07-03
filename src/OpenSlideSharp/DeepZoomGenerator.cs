// See openslide-python (https://github.com/openslide/openslide-python)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;


#if !ET35
namespace OpenSlideSharp
{
    /// <summary>
    /// Support for Deep Zoom images.
    /// </summary>
    public class DeepZoomGenerator : IDisposable
    {
        private OpenSlideImage _image;
        private readonly int _tileSize;
        private readonly int _overlap;

        private Offset _l0_offset;
        private ImageDimension[] _l_dimemsions;
        private ImageDimension[] _z_dimemsions;
        private TileDimensions[] _t_dimensions;
        private int _dz_levels;
        private int[] _slide_from_dz_level;
        private double[] _l0_l_downsamples;
        private double[] _l_z_downsamples;

        /// <summary>
        /// Get slide object
        /// </summary>
        public OpenSlideImage Image => _image;

        /// <summary>
        /// Ctor
        /// </summary>
        /// <param name="image"></param>
        /// <param name="tileSize"> the width and height of a single tile.  For best viewer
        /// performance, tile_size + 2 * overlap should be a power
        /// of two.</param>
        /// <param name="overlap">the number of extra pixels to add to each interior edge
        /// of a tile.</param>
        /// <param name="limitBounds">True to render only the non-empty slide region.</param>
        /// <param name="isOwner"></param>
        public DeepZoomGenerator(OpenSlideImage image, int tileSize = 254, int overlap = 1, bool limitBounds = true, bool isOwner = true)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));

            _image = image;
            _tileSize = tileSize;
            _overlap = overlap;
            disposedValue = !isOwner;

            // We have four coordinate planes:
            // - Row and column of the tile within the Deep Zoom level (t_)
            // - Pixel coordinates within the Deep Zoom level (z_)
            // - Pixel coordinates within the slide level (l_)
            // - Pixel coordinates within slide level 0 (l0_)

            // Precompute dimensions
            // Slide level and offset
            if (limitBounds)
            {
                // Level 0 coordinate offset
                _l0_offset = new Offset(image.BoundsX ?? 0, image.BoundsY ?? 0);
                // Slide level dimensions scale factor in each axis

                long boundsWidth = image.BoundsWidth ?? 0;
                boundsWidth = boundsWidth == 0 ? image.Dimensions.width : boundsWidth;
                long boundsHeight = image.BoundsHeight ?? 0;
                boundsHeight = boundsHeight == 0 ? image.Dimensions.height : boundsHeight;
                // Dimensions of active area
                _l_dimemsions = Enumerable.Range(0, image.LevelCount)
                    .Select(i => image.GetLevelDimension(i))
                    .Select(l_size => new ImageDimension(
                        (long)Math.Ceiling(l_size.width * boundsWidth / (double)image.Dimensions.width),
                        (long)Math.Ceiling(l_size.height * boundsHeight / (double)image.Dimensions.height)))
                    .ToArray();
            }
            else
            {
                _l0_offset = new Offset(0, 0);
                _l_dimemsions = Enumerable.Range(0, image.LevelCount)
                    .Select(i => image.GetLevelDimension(i))
                    .Select(l_size => new ImageDimension(l_size.width, l_size.height))
                    .ToArray();
            }
            var _l0_dimemsions = _l_dimemsions[0];
            // Deep Zoom level
            var z_size = _l0_dimemsions;
            var z_dimemsions = new List<ImageDimension>();
            z_dimemsions.Add(z_size);
            while (z_size.width > 1 || z_size.height > 1)
            {
                z_size = new ImageDimension(width: Math.Max(1, (long)Math.Ceiling(z_size.width / 2d)), height: Math.Max(1, (long)Math.Ceiling(z_size.height / 2d)));
                z_dimemsions.Add(z_size);
            }
            z_dimemsions.Reverse();
            _z_dimemsions = z_dimemsions.ToArray();
            // Tile
            int tiles(long z_lim)
            {
                return (int)Math.Ceiling(z_lim / (double)_tileSize);
            }
            _t_dimensions = _z_dimemsions.Select(z => new TileDimensions(tiles(z.width), tiles(z.height))).ToArray();
 
            // Deep Zoom level count
            _dz_levels = _z_dimemsions.Length;

            // Total downsamples for each Deep Zoom level
            var l0_z_downsamples = Enumerable.Range(0, _dz_levels).Select(dz_level => Math.Pow(2, _dz_levels - dz_level - 1)).ToArray();

            // Preferred slide levels for each Deep Zoom level
            _slide_from_dz_level = l0_z_downsamples.Select(d => image.GetBestLevelForDownsample(d)).ToArray();

            // Piecewise downsamples
            _l0_l_downsamples = Enumerable.Range(0, image.LevelCount).Select(l => image.GetLevelDownsample(l)).ToArray();
            _l_z_downsamples = Enumerable.Range(0, _dz_levels).Select(dz_level => l0_z_downsamples[dz_level] / _l0_l_downsamples[_slide_from_dz_level[dz_level]]).ToArray();
        }

        /// <summary>
        /// The number of Deep Zoom levels in the image.
        /// </summary>
        public int LevelCount => _dz_levels;

        /// <summary>
        /// A list of (tiles_x, tiles_y) tuples for each Deep Zoom level.
        /// </summary>
        public TileDimensions[] LevelTiles => _t_dimensions;

        /// <summary>
        /// A list of (pixels_x, pixels_y) tuples for each Deep Zoom level.
        /// </summary>
        public ImageDimension[] LevelDimemsions => _z_dimemsions;

        /// <summary>
        /// The total number of Deep Zoom tiles in the image.
        /// </summary>
        public int TileCount => _t_dimensions.Sum(t => t.cols * t.rows);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="level">the Deep Zoom level.</param>
        /// <param name="col"></param>
        /// <param name="row"></param>
        /// <param name="info"></param>
        /// <returns></returns>
        public byte[] GetTile(int level, int col, int row, out TileInfo info)
        {
            info = GetTileInfo(level, col, row);
            return _image.ReadRegion(info.SlideLevel, info.X, info.Y, info.Width, info.Height);
        }

        /// <summary>
        /// Return tile info.
        /// </summary>
        /// <param name="level"></param>
        /// <param name="col"></param>
        /// <param name="row"></param>
        /// <returns></returns>
        public TileInfo GetTileInfo(int level, int col, int row)
        {
            if (level < 0 || level >= _dz_levels)
            {
                throw new ArgumentOutOfRangeException(nameof(level));
            }
            var t_dim = _t_dimensions[level];
            if (col < 0 || col >= t_dim.cols)
            {
                throw new ArgumentOutOfRangeException(nameof(col));
            }
            if (row < 0 || row >= t_dim.rows)
            {
                throw new ArgumentOutOfRangeException(nameof(row));
            }

            // Get preferred slide level
            var slide_level = _slide_from_dz_level[level];

            // Calculate top/left and bottom/right overlap
            int z_overlap_l = col != 0 ? 1 : 0;
            int z_overlap_t = row != 0 ? 1 : 0;
            int z_overlap_r = col != t_dim.cols - 1 ? 1 : 0;
            int z_overlap_b = row != t_dim.rows - 1 ? 1 : 0;

            // Get final size of the tile
            var z_dim = _z_dimemsions[level];
            int z_size_x = Math.Min(_tileSize, (int)(z_dim.width - _tileSize * col)) + z_overlap_l + z_overlap_r;
            int z_size_y = Math.Min(_tileSize, (int)(z_dim.height - _tileSize * row)) + z_overlap_t + z_overlap_b;

            // Obtain the region coordinates
            var z_location_x = _z_from_t(col);
            var z_location_y = _z_from_t(row);
            var l_location_x = _l_from_z(level, z_location_x - z_overlap_l);
            var l_location_y = _l_from_z(level, z_location_y - z_overlap_t);

            // Round location down and size up, and add offset of active area
            var l0_location_x = (long)(_l0_from_l(slide_level, l_location_x) + _l0_offset.x);
            var l0_location_y = (long)(_l0_from_l(slide_level, l_location_y) + _l0_offset.y);
            var l_dim = _l_dimemsions[slide_level];
            var l_size_x = (long)Math.Min(Math.Ceiling(_l_from_z(level, z_size_x)), l_dim.width - Math.Ceiling(l_location_x));
            var l_size_y = (long)Math.Min(Math.Ceiling(_l_from_z(level, z_size_y)), l_dim.height - Math.Ceiling(l_location_y));

            return new TileInfo
            {
                X = l0_location_x,
                Y = l0_location_y,
                SlideLevel = slide_level,
                Width = l_size_x,
                Height = l_size_y,
                TileWidth = z_size_x,
                TileHeight = z_size_y,
                ResizeNeeded = l_size_x != z_size_x || l_size_y != z_size_y
            };
        }

        /// <summary>
        /// Return a string containing the XML metadata for the .dzi file.
        /// </summary>
        /// <param name="format">the format of the individual tiles ('png' or 'jpeg')</param>
        /// <returns></returns>
        public string GetDzi(string format = "jpeg")
        {
            var (width, height) = _l_dimemsions[0];
            var sb = new StringBuilder(DziTemplete);
            sb.Replace("{FORMAT}", format);
            sb.Replace("{OVERLAP}", _overlap.ToString());
            sb.Replace("{TILESIZE}", _tileSize.ToString());
            sb.Replace("{HEIGHT}", height.ToString());
            sb.Replace("{WIDTH}", width.ToString());
            return sb.ToString();
        }

        /// <summary>
        /// <see cref="GetTileInfo(int, int, int)"/>
        /// </summary>
        /// <param name="level"></param>
        /// <param name="col"></param>
        /// <param name="row"></param>
        /// <returns></returns>
        [Obsolete("Use GetTileInfo instead.")]
        public TileInfo GetTileCoordinates(int level, int col, int row) => GetTileInfo(level, col, row);


        private struct Offset
        {
            public long x;
            public long y;

            public Offset(long _x, long _y)
            {
                x = _x;
                y = _y;
            }
        }

        /// <summary>
        /// Tile position
        /// </summary>
        public struct TileDimensions
        {
            internal int cols;
            internal int rows;

            /// <summary>
            /// tile col
            /// </summary>
            public int Cols => cols;
            /// <summary>
            /// tile row
            /// </summary>
            public int Rows => rows;

            /// <summary>
            /// 
            /// </summary>
            /// <param name="_cols">cols</param>
            /// <param name="_rows">rows</param>
            public TileDimensions(int _cols, int _rows)
            {
                cols = _cols;
                rows = _rows;
            }
        }


        /// <summary>
        /// Tile info
        /// </summary>
        public class TileInfo
        {
#pragma warning disable CS1591
            public long X { get; set; }
            public long Y { get; set; }
            public int SlideLevel { get; set; }
            public long Width { get; set; }
            public long Height { get; set; }
            public int TileWidth { get; set; }
            public int TileHeight { get; set; }
            public bool ResizeNeeded { get; set; }
#pragma warning restore CS1591
        }

        private const string DziTemplete = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + "<Image xmlns=\"http://schemas.microsoft.com/deepzoom/2008\" Format=\"{FORMAT}\" Overlap=\"{OVERLAP}\" TileSize=\"{TILESIZE}\">"
            + "<Size Height=\"{HEIGHT}\" Width=\"{WIDTH}\" />"
            + "</Image>";

        private double _l0_from_l(int slide_level, double l) => _l0_l_downsamples[slide_level] * l;

        private double _l_from_z(int dz_level, int z) => _l_z_downsamples[dz_level] * z;

        private int _z_from_t(int t) => _tileSize * t;

        #region IDisposable Support

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
                    Image.Dispose();
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

        #endregion IDisposable Support
    }
}
#endif
