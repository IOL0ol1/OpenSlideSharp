using BruTile;
using Mapsui;
using Mapsui.Fetcher;
using Mapsui.Geometries;
using Mapsui.Layers;
using Mapsui.Providers;
using Mapsui.Styles;
using OpenSlideSharp.BruTile;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlideLibrary.Demo
{
    /// <summary>
    /// Slide slice layer
    /// </summary>
    public class SlideSliceLayer : BaseLayer
    {
        private ISlideSource _slideSource;
        private double _lastResolution = 0;
        private IEnumerable<IFeature> _lastFeatures = new Features(new[] { new Feature() });
        private Extent _lastExtent;

        public SlideSliceLayer(ISlideSource slideSource) : base()
        {
            _slideSource = slideSource;
            Name = "SliceLayer";
            Envelope = slideSource.Schema.Extent.ToBoundingBox();
        }

        public override IEnumerable<IFeature> GetFeaturesInView(BoundingBox box, double resolution)
        {
            if (box is null) return Enumerable.Empty<IFeature>();
            // Repaint on debouncing, resolution changed(zoom map) or box changed(resize control) .
            if (_lastExtent.ToBoundingBox().Centroid.Distance(box.Centroid) > 2 * resolution || _lastResolution != resolution || _lastExtent.Width != box.Width || _lastExtent.Height != box.Height)
            {
                _lastExtent = box.ToExtent();
                _lastResolution = resolution;
                BoundingBox box2 = box.Grow(SymbolStyle.DefaultWidth * 2.0 * resolution, SymbolStyle.DefaultHeight * 2.0 * resolution);
                var sliceInfo = new SliceInfo() { Extent = box2.ToExtent(), Resolution = resolution };
                var bytes = _slideSource.GetSlice(sliceInfo);
                if (bytes != null && _lastFeatures.FirstOrDefault() is IFeature feature)
                {
                    feature.Geometry = new Raster(new MemoryStream(bytes), box2);
                }
            }
            return _lastFeatures;
        }

        public override void RefreshData(BoundingBox extent, double resolution, ChangeType changeType)
        {
            OnDataChanged(new DataChangedEventArgs());
        }
    }
}
