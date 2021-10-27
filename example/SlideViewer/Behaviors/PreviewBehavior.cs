using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Mapsui;
using Mapsui.Geometries;
using Mapsui.Layers;
using Mapsui.Providers;
using Mapsui.Styles;
using Mapsui.UI.Wpf;
using Microsoft.Xaml.Behaviors;
using Bitmap = System.Drawing.Bitmap;
using Point = Mapsui.Geometries.Point;

namespace SlideLibrary.Demo
{

    /// <summary>
    /// Associate hawkeye maps and main maps
    /// </summary>
    public class PreviewBehavior : Behavior<MapControl>
    {

        protected override void OnAttached()
        {
            base.OnAttached();
            if (MapControl != null)
            {
                AssociatedObject.MouseDown += MapPreviewOnMouseDown;
                AssociatedObject.MouseMove += MapPreviewOnMouseMove;
                AssociatedObject.MouseUp += MapPreviewOnMouseUp;
                AssociatedObject.Map.Layers.LayerAdded += LayersOnLayerAdded;
                AssociatedObject.Unloaded += AssociatedObject_Unloaded;
                MapControl.Viewport.ViewportChanged += ViewportOnViewportChanged;
                timer = new DispatcherTimer(TimeSpan.FromMilliseconds(RecordTimer), DispatcherPriority.Normal, TimerCallback, Dispatcher);
            }
        }

        private void AssociatedObject_Unloaded(object sender, RoutedEventArgs e)
        {
            timer?.Stop();
        }

        private void TimerCallback(object sender, EventArgs e)
        {
            try
            {
                if (MapControl != null
                    && MapControl.Viewport != null
                    && MapControl.Viewport.Resolution <= 4
                    && recordLayer != null
                    && hawkeyeLayer != null
                    && hawkeyeLayer.GetFeatures()?.FirstOrDefault() is Feature feature
                    && feature.Geometry is Polygon polygon
                   )
                    recordLayer.AddRecord(polygon.ExteriorRing.BoundingBox);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Reorder layer.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void LayersOnLayerAdded(ILayer layer)
        {
            if (layer == recordLayer || layer == hawkeyeLayer) return;
            LayerCollection layers = AssociatedObject.Map.Layers;
            // add mask layer.
            if (layers.Contains(recordLayer))
                layers.Move(layers.Count, recordLayer);
            else
                layers.Add(recordLayer = new RecordLayer(AssociatedObject.Viewport) { Enabled = IsShowRecord });

            // add or move hawkeye layer.
            if (layers.Contains(hawkeyeLayer))
                layers.Move(layers.Count, hawkeyeLayer);
            else
                layers.Add(hawkeyeLayer = CreateHawkeyeLayer());
        }


        protected override void OnDetaching()
        {
            base.OnDetaching();
            if (MapControl != null)
            {
                timer?.Stop();
                AssociatedObject.Map.Layers.Remove(recordLayer);
                AssociatedObject.Map.Layers.Remove(hawkeyeLayer);
                AssociatedObject.MouseDown -= MapPreviewOnMouseDown;
                AssociatedObject.MouseMove -= MapPreviewOnMouseMove;
                AssociatedObject.MouseUp -= MapPreviewOnMouseUp;
                AssociatedObject.Map.Layers.LayerAdded -= LayersOnLayerAdded;
                AssociatedObject.Unloaded += AssociatedObject_Unloaded;
                MapControl.Viewport.ViewportChanged -= ViewportOnViewportChanged;
            }
        }

        private bool IsVisible()
        {
            return AssociatedObject.Visibility == Visibility.Visible;
        }

        private WritableLayer hawkeyeLayer;
        /// <summary>
        /// Hawkeye layer.
        /// </summary>
        /// <returns></returns>
        private static WritableLayer CreateHawkeyeLayer()
        {
            var layer = new WritableLayer
            {
                IsMapInfoLayer = true,
                Style = new VectorStyle
                {
                    Fill = new Mapsui.Styles.Brush(new Mapsui.Styles.Color(255, 255, 255, 1)), // mapsui can't capture mouse when transparent,use 1 almost transparent.
                    Outline = new Mapsui.Styles.Pen { Color = Mapsui.Styles.Color.FromArgb(100, 255, 0, 0), Width = 2 }, // color and width for outline.
                }
            };
            layer.Add(new Feature { Geometry = new Polygon() });
            return layer;
        }


        #region Update main map viewport

        private Point _mouseOffset = new Point();

        private bool _isClickInfo;

        private void MapPreviewOnMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!IsVisible()) return;

            _isClickInfo = false;
        }

        private void MapPreviewOnMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!IsVisible()) return;
            var mousePoint = e.GetPosition(sender as IInputElement);
            // start drag
            if (e.GetMapInfo(AssociatedObject)?.Feature?.Geometry is Polygon polygon)
            {
                _isClickInfo = true;
                _mouseOffset = polygon.BoundingBox.Centroid - AssociatedObject.Viewport.ScreenToWorld(mousePoint.ToMapsui());
            }
            else
            {
                // click once goto.
                MapControl.Navigator.CenterOn(AssociatedObject.Viewport.ScreenToWorld(mousePoint.ToMapsui()));
                _isClickInfo = false;
            }
        }

        private void MapPreviewOnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!IsVisible()) return;

            if (_isClickInfo)
            {
                var mousePoint = e.GetPosition(sender as IInputElement);
                var centerWorld = AssociatedObject.Viewport.ScreenToWorld(mousePoint.ToMapsui()) + _mouseOffset;
                MapControl.Navigator.CenterOn(centerWorld);
            }
        }

        private void MapPreviewOnViewportChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!IsVisible()) return;

            if (e.PropertyName == "Resolution")
            {

                LayerCollection layers = AssociatedObject.Map.Layers;
                if (layers.Any() && layers.LastOrDefault() as WritableLayer != hawkeyeLayer)
                {
                    if (layers.Contains(hawkeyeLayer))
                        layers.Move(layers.Count - 1, hawkeyeLayer);
                    else
                        layers.Add(hawkeyeLayer);
                }
            }
        }
        #endregion



        #region Update hawkeye map
        private Point _lastCenter = new Point();
        private double _lastResolution;
        private double _lastHeight;
        private double _lastWidth;

        private void ViewportOnViewportChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!IsVisible()) return;

            timer.Interval = TimeSpan.FromMilliseconds(RecordTimer);
            if (!_lastCenter.Equals(MapControl.Viewport.Center)
                || _lastResolution != MapControl.Viewport.Resolution
                || _lastWidth != MapControl.Viewport.Width
                || _lastHeight != MapControl.Viewport.Height)
            {
                if (hawkeyeLayer != null && hawkeyeLayer.GetFeatures().FirstOrDefault() is Feature feature && feature.Geometry is Polygon polygon)
                {
                    var mapExtent = MapControl.Map.Envelope;
                    if (mapExtent != null)
                    {
                        var displayExtent = MapControl.Viewport.Extent;
                        polygon.ExteriorRing.Vertices.Clear();
                        polygon.InteriorRings.Clear();
                        polygon.ExteriorRing.Vertices.Add(displayExtent.TopLeft);
                        polygon.ExteriorRing.Vertices.Add(displayExtent.TopRight);
                        polygon.ExteriorRing.Vertices.Add(displayExtent.BottomRight);
                        polygon.ExteriorRing.Vertices.Add(displayExtent.BottomLeft);
                        polygon.ExteriorRing.Vertices.Add(displayExtent.TopLeft);
                        if (mapExtent.Left < displayExtent.Left)
                            polygon.InteriorRings.Add(new LinearRing(new[] { new Point(mapExtent.Left, displayExtent.Centroid.Y), new Point(displayExtent.Left, displayExtent.Centroid.Y) }));
                        if (mapExtent.Top > displayExtent.Top)
                            polygon.InteriorRings.Add(new LinearRing(new[] { new Point(displayExtent.Centroid.X, mapExtent.Top), new Point(displayExtent.Centroid.X, displayExtent.Top) }));
                        if (mapExtent.Right > displayExtent.Right)
                            polygon.InteriorRings.Add(new LinearRing(new[] { new Point(displayExtent.Right, displayExtent.Centroid.Y), new Point(mapExtent.Right, displayExtent.Centroid.Y) }));
                        if (mapExtent.Bottom < displayExtent.Bottom)
                            polygon.InteriorRings.Add(new LinearRing(new[] { new Point(displayExtent.Centroid.X, displayExtent.Bottom), new Point(displayExtent.Centroid.X, mapExtent.Bottom) }));
                        hawkeyeLayer.DataHasChanged();
                    }
                }
            }
            _lastCenter = MapControl.Viewport.Center;
            _lastResolution = MapControl.Viewport.Resolution;
            _lastWidth = MapControl.Viewport.Width;
            _lastHeight = MapControl.Viewport.Height;
        }
        #endregion


        /// <summary>
        /// Main map control.
        /// </summary>
        public MapControl MapControl
        {
            get { return (MapControl)GetValue(MainMapControlProperty); }
            set { SetValue(MainMapControlProperty, value); }
        }

        // Using a DependencyProperty as the backing store for MainMapControl.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty MainMapControlProperty =
            DependencyProperty.Register("MapControl", typeof(MapControl), typeof(PreviewBehavior), new PropertyMetadata(null));


        private DispatcherTimer timer;

        private RecordLayer recordLayer;

        /// <summary>
        /// Enable mask layer.
        /// </summary>
        public bool IsShowRecord
        {
            get { return (bool)GetValue(IsShowRecordProperty); }
            set { SetValue(IsShowRecordProperty, value); }
        }

        // Using a DependencyProperty as the backing store for IsRecord.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty IsShowRecordProperty =
            DependencyProperty.Register(nameof(IsShowRecord), typeof(bool), typeof(PreviewBehavior), new PropertyMetadata(false, (_s, _e) =>
            {
                if (_s is PreviewBehavior behavior && behavior.recordLayer is RecordLayer record)
                {
                    record.Enabled = (bool)_e.NewValue;
                }
            }));


        /// <summary>
        /// Mask timer(ms)
        /// </summary>
        public double RecordTimer
        {
            get { return (double)GetValue(RecordTimerProperty); }
            set { SetValue(RecordTimerProperty, value); }
        }

        // Using a DependencyProperty as the backing store for RecordTimer.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty RecordTimerProperty =
            DependencyProperty.Register(nameof(RecordTimer), typeof(double), typeof(PreviewBehavior), new PropertyMetadata(1000d, (_s, _e) =>
            {
                if (_s is PreviewBehavior behavior && behavior.timer is DispatcherTimer timer)
                {
                    timer.Interval = TimeSpan.FromMilliseconds((double)_e.NewValue);
                }
            }));

        /// <summary>
        /// Mask layer
        /// </summary>
        private class RecordLayer : BaseLayer, IDisposable
        {

            private readonly IReadOnlyViewport _viewport;
            private readonly Bitmap _background;
            private readonly byte[] _lineBytes;
            private bool disposedValue;

            public RecordLayer(IReadOnlyViewport viewport) : base(nameof(RecordLayer))
            {
                _viewport = viewport;
                _background = CreateTransparentBitmap(viewport.Width, viewport.Height);
                _lineBytes = CreateBGRARawData(System.Drawing.Color.FromArgb(128, 0, 128, 0), _background.Width);
            }

            /// <summary>
            /// Create transparent bitmap.
            /// </summary>
            /// <param name="w"></param>
            /// <param name="h"></param>
            /// <returns></returns>
            private static Bitmap CreateTransparentBitmap(double w, double h)
            {
                var _background = new Bitmap((int)w, (int)h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var gc = Graphics.FromImage(_background))
                {
                    gc.Clear(System.Drawing.Color.Transparent);
                }
                return _background;
            }

            /// <summary>
            /// Create a line BGRA data.
            /// </summary>
            /// <param name="color">color</param>
            /// <param name="pixelSize">pixel width</param>
            /// <returns></returns>
            private static byte[] CreateBGRARawData(System.Drawing.Color color, int pixelSize)
            {
                var bytes = new List<byte[]>();
                for (int i = 0; i < pixelSize; i++)
                {
                    bytes.Add(new[] { color.B, color.G, color.R, color.A });
                }
                return bytes.Aggregate<IEnumerable<byte>>((_1, _2) => _1.Concat(_2)).ToArray();
            }

            /// <summary>
            /// Add semitransparent mask
            /// </summary>
            /// <param name="box"></param>
            public void AddRecord(BoundingBox box)
            {
                if (box == null) return;
                var topLeft = _viewport.WorldToScreen(box.TopLeft);
                var bottomRight = _viewport.WorldToScreen(box.BottomRight);
                var rect = new Rectangle((int)topLeft.X, (int)topLeft.Y, (int)Math.Ceiling(Math.Abs(bottomRight.X - topLeft.X)), (int)Math.Ceiling(Math.Abs(bottomRight.Y - topLeft.Y)));
                rect.Intersect(new Rectangle(0, 0, _background.Width, _background.Height));
                var bitmapData = _background.LockBits(new Rectangle(0, 0, _background.Width, _background.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                for (int i = rect.Y; i < (rect.Y + rect.Height); i++)
                {
                    Marshal.Copy(_lineBytes, 0, bitmapData.Scan0 + (i * bitmapData.Stride) + (rect.X * 4), rect.Width * 4);
                }
                _background.UnlockBits(bitmapData);
                DataHasChanged();
            }

            public override IEnumerable<IFeature> GetFeaturesInView(BoundingBox box, double resolution)
            {
                if (!Enabled) yield break;
                var ms = new MemoryStream();
                _background.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                yield return new Feature
                {
                    Geometry = new Raster(ms, box),
                };
            }

            public override void RefreshData(BoundingBox extent, double resolution, ChangeType changeType)
            {
                OnDataChanged(new Mapsui.Fetcher.DataChangedEventArgs());
            }

            protected virtual void Dispose(bool disposing)
            {
                if (!disposedValue)
                {
                    if (disposing)
                    {
                        _background.Dispose();
                    }

                    disposedValue = true;
                }
            }

            public void Dispose()
            {
                Dispose(disposing: true);
                GC.SuppressFinalize(this);
            }
        }
    }
}
