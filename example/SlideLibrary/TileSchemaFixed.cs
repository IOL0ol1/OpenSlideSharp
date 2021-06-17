using System;
using System.Collections.Generic;
using System.Globalization;
using BruTile;

namespace SlideLibrary
{
    public class TileSchemaFixed : ITileSchema
    {
        protected static class TileTransform
        {
            private const double Tolerance = 0.000000001;

            public static TileRange WorldToTile(Extent extent, int level, ITileSchema schema)
            {
                switch (schema.YAxis)
                {
                    case YAxis.TMS:
                        return WorldToTileNormal(extent, level, schema);
                    case YAxis.OSM:
                        return WorldToTileInvertedY(extent, level, schema);
                    default:
                        throw new Exception("YAxis type was not found");
                }
            }

            public static Extent TileToWorld(TileRange range, int level, ITileSchema schema)
            {
                switch (schema.YAxis)
                {
                    case YAxis.TMS:
                        return TileToWorldNormal(range, level, schema);
                    case YAxis.OSM:
                        return TileToWorldInvertedY(range, level, schema);
                    default:
                        throw new Exception("YAxis type was not found");
                }
            }

            private static TileRange WorldToTileNormal(Extent extent, int level, ITileSchema schema)
            {
                var resolution = schema.Resolutions[level];

                var tileWidthWorldUnits = resolution.UnitsPerPixel * schema.GetTileWidth(level);
                var tileHeightWorldUnits = resolution.UnitsPerPixel * schema.GetTileHeight(level);
                var firstCol = (int)Math.Floor((extent.MinX - schema.GetOriginX(level)) / tileWidthWorldUnits + Tolerance);
                var firstRow = (int)Math.Floor((extent.MinY - schema.GetOriginY(level)) / tileHeightWorldUnits + Tolerance);
                var lastCol = (int)Math.Ceiling((extent.MaxX - schema.GetOriginX(level)) / tileWidthWorldUnits - Tolerance);
                var lastRow = (int)Math.Ceiling((extent.MaxY - schema.GetOriginY(level)) / tileHeightWorldUnits - Tolerance);
                return new TileRange(firstCol, firstRow, lastCol - firstCol, lastRow - firstRow);
            }

            private static Extent TileToWorldNormal(TileRange range, int level, ITileSchema schema)
            {
                var resolution = schema.Resolutions[level];
                var tileWidthWorldUnits = resolution.UnitsPerPixel * schema.GetTileWidth(level);
                var tileHeightWorldUnits = resolution.UnitsPerPixel * schema.GetTileHeight(level);
                var minX = range.FirstCol * tileWidthWorldUnits + schema.GetOriginX(level);
                var minY = range.FirstRow * tileHeightWorldUnits + schema.GetOriginY(level);
                var maxX = (range.FirstCol + range.ColCount) * tileWidthWorldUnits + schema.GetOriginX(level);
                var maxY = (range.FirstRow + range.RowCount) * tileHeightWorldUnits + schema.GetOriginY(level);
                return new Extent(minX, minY, maxX, maxY);
            }

            private static TileRange WorldToTileInvertedY(Extent extent, int level, ITileSchema schema)
            {
                var resolution = schema.Resolutions[level];
                var tileWidthWorldUnits = resolution.UnitsPerPixel * schema.GetTileWidth(level);
                var tilHeightWorldUnits = resolution.UnitsPerPixel * schema.GetTileHeight(level);
                var firstCol = (int)Math.Floor((extent.MinX - schema.GetOriginX(level)) / tileWidthWorldUnits + Tolerance);
                var firstRow = (int)Math.Floor((-extent.MaxY + schema.GetOriginY(level)) / tilHeightWorldUnits + Tolerance);
                var lastCol = (int)Math.Ceiling((extent.MaxX - schema.GetOriginX(level)) / tileWidthWorldUnits - Tolerance);
                var lastRow = (int)Math.Ceiling((-extent.MinY + schema.GetOriginY(level)) / tilHeightWorldUnits - Tolerance);
                return new TileRange(firstCol, firstRow, lastCol - firstCol, lastRow - firstRow);
            }

            private static Extent TileToWorldInvertedY(TileRange range, int level, ITileSchema schema)
            {
                var resolution = schema.Resolutions[level];
                var tileWidthWorldUnits = resolution.UnitsPerPixel * schema.GetTileWidth(level);
                var tileHeightWorldUnits = resolution.UnitsPerPixel * schema.GetTileHeight(level);
                var minX = range.FirstCol * tileWidthWorldUnits + schema.GetOriginX(level);
                var minY = -(range.FirstRow + range.RowCount) * tileHeightWorldUnits + schema.GetOriginY(level);
                var maxX = (range.FirstCol + range.ColCount) * tileWidthWorldUnits + schema.GetOriginX(level);
                var maxY = -(range.FirstRow) * tileHeightWorldUnits + schema.GetOriginY(level);
                return new Extent(minX, minY, maxX, maxY);
            }
        }

        public double ProportionIgnored;

        private readonly IDictionary<int, Resolution> _resolutions;

        public double OriginX
        {
            get;
            set;
        }

        public double OriginY
        {
            get;
            set;
        }

        public string Name
        {
            get;
            set;
        }

        public string Srs
        {
            get;
            set;
        }

        public Extent Wgs84BoundingBox
        {
            get;
            set;
        }

        public string Format
        {
            get;
            set;
        }

        public Extent Extent
        {
            get;
            set;
        }

        public YAxis YAxis
        {
            get;
            set;
        }

        public IDictionary<int, Resolution> Resolutions => _resolutions;

        public TileSchemaFixed()
        {
            ProportionIgnored = 0.0001;
            _resolutions = new Dictionary<int, Resolution>();
            YAxis = YAxis.TMS;
            OriginY = double.NaN;
            OriginX = double.NaN;
        }

        public double GetOriginX(int level)
        {
            return OriginX;
        }

        public double GetOriginY(int level)
        {
            return OriginY;
        }

        public int GetTileWidth(int level)
        {
            return Resolutions[level].TileWidth;
        }

        public int GetTileHeight(int level)
        {
            return Resolutions[level].TileHeight;
        }

        public long GetMatrixWidth(int level)
        {
            return GetMatrixLastCol(level) - GetMatrixFirstCol(level) + 1;
        }

        public long GetMatrixHeight(int level)
        {
            return GetMatrixLastRow(level) - GetMatrixFirstRow(level) + 1;
        }

        public int GetMatrixFirstCol(int level)
        {
            return (int)Math.Floor(GetFirstXRelativeToOrigin(Extent, OriginX) / Resolutions[level].UnitsPerPixel / (double)GetTileWidth(level));
        }

        public int GetMatrixFirstRow(int level)
        {
            return (int)Math.Floor(GetFirstYRelativeToOrigin(YAxis, Extent, OriginY) / Resolutions[level].UnitsPerPixel / (double)GetTileHeight(level));
        }

        public IEnumerable<TileInfo> GetTileInfos(Extent extent, double unitsPerPixel)
        {
            int nearestLevel = BruTile.Utilities.GetNearestLevel(Resolutions, unitsPerPixel);
            return GetTileInfos(extent, nearestLevel);
        }

        public IEnumerable<TileInfo> GetTileInfos(Extent extent, int level)
        {
            return GetTileInfos(this, extent, level);
        }

        public Extent GetExtentOfTilesInView(Extent extent, int level)
        {
            return GetExtentOfTilesInView(this, extent, level);
        }

        private int GetMatrixLastCol(int level)
        {
            return (int)Math.Floor(GetLastXRelativeToOrigin(Extent, OriginX) / Resolutions[level].UnitsPerPixel / (double)GetTileWidth(level) - ProportionIgnored);
        }

        private int GetMatrixLastRow(int level)
        {
            return (int)Math.Floor(GetLastYRelativeToOrigin(YAxis, Extent, OriginY) / Resolutions[level].UnitsPerPixel / (double)GetTileHeight(level) - ProportionIgnored);
        }

        private static double GetLastXRelativeToOrigin(Extent extent, double originX)
        {
            return extent.MaxX - originX;
        }

        private static double GetLastYRelativeToOrigin(YAxis yAxis, Extent extent, double originY)
        {
            if (yAxis != 0)
            {
                return 0.0 - extent.MinY + originY;
            }

            return extent.MaxY - originY;
        }

        private static double GetFirstXRelativeToOrigin(Extent extent, double originX)
        {
            return extent.MinX - originX;
        }

        private static double GetFirstYRelativeToOrigin(YAxis yAxis, Extent extent, double originY)
        {
            if (yAxis != 0)
            {
                return 0.0 - extent.MaxY + originY;
            }

            return extent.MinY - originY;
        }

        internal static IEnumerable<TileInfo> GetTileInfos(ITileSchema schema, Extent extent, int level)
        {
            TileRange tileRange = TileTransform.WorldToTile(extent, level, schema);
            int num = Math.Max(tileRange.FirstCol, schema.GetMatrixFirstCol(level));
            long stopX = Math.Min(tileRange.FirstCol + tileRange.ColCount, schema.GetMatrixFirstCol(level) + schema.GetMatrixWidth(level));
            int startY = Math.Max(tileRange.FirstRow, schema.GetMatrixFirstRow(level));
            long stopY = Math.Min(tileRange.FirstRow + tileRange.RowCount, schema.GetMatrixFirstRow(level) + schema.GetMatrixHeight(level));
            for (int x = num; x < stopX; x++)
            {
                for (int y = startY; y < stopY; y++)
                {
                    yield return new TileInfo
                    {
                        Extent = TileTransform.TileToWorld(new TileRange(x, y), level, schema),
                        Index = new TileIndex(x, y, level)
                    };
                }
            }
        }

        public static Extent GetExtentOfTilesInView(ITileSchema schema, Extent extent, int level)
        {
            return TileTransform.TileToWorld(TileTransform.WorldToTile(extent, level, schema), level, schema);
        }

        public void Validate()
        {
            if (string.IsNullOrEmpty(Srs))
            {
                throw new ValidationException(string.Format(CultureInfo.InvariantCulture, "The SRS was not set for TileSchema '{0}'", new object[1]
                {
                    Name
                }));
            }

            if (Extent == default)
            {
                throw new ValidationException(string.Format(CultureInfo.InvariantCulture, "The BoundingBox was not set for TileSchema '{0}'", new object[1]
                {
                    Name
                }));
            }

            if (double.IsNaN(OriginX))
            {
                throw new ValidationException(string.Format(CultureInfo.InvariantCulture, "TileSchema {0} OriginX was 'not a number', perhaps it was not initialized.", new object[1]
                {
                    Name
                }));
            }

            if (double.IsNaN(OriginY))
            {
                throw new ValidationException(string.Format(CultureInfo.InvariantCulture, "TileSchema {0} OriginY was 'not a number', perhaps it was not initialized.", new object[1]
                {
                    Name
                }));
            }

            if (Resolutions.Count == 0)
            {
                throw new ValidationException(string.Format(CultureInfo.InvariantCulture, "No Resolutions were added for TileSchema '{0}'", new object[1]
                {
                    Name
                }));
            }

            if (string.IsNullOrEmpty(Format))
            {
                throw new ValidationException(string.Format(CultureInfo.InvariantCulture, "The Format was not set for TileSchema '{0}'", new object[1]
                {
                    Name
                }));
            }
        }
    }
}
