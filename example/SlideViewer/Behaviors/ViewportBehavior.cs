using Mapsui.UI.Wpf;

/* 项目“SlideViewer (netcoreapp3.1)”的未合并的更改
在此之前:
using Microsoft.Xaml.Behaviors;
在此之后:
using Microsoft.Xaml.Behaviors;
using System;
using System.Windows;
*/
using Microsoft.Xaml.Behaviors;
using System.Windows;

namespace SlideLibrary.Demo
{
    /// <summary>
    /// Viewport info.
    /// </summary>
    public class ViewportBehavior : Behavior<MapControl>
    {
        protected override void OnAttached()
        {
            base.OnAttached();
            AssociatedObject.Viewport.ViewportChanged += ViewportViewportChanged;
            AssociatedObject.MouseMove += AssociatedObjectOnMouseMove;
        }
        protected override void OnDetaching()
        {
            base.OnDetaching();
            AssociatedObject.Viewport.ViewportChanged -= ViewportViewportChanged;
            AssociatedObject.MouseMove -= AssociatedObjectOnMouseMove;
        }

        private bool isViewportChanged = true;

        private void ViewportViewportChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Resolution))
            {
                isViewportChanged = true;
                Resolution = AssociatedObject.Viewport.Resolution;
                isViewportChanged = false;
            }
        }

        private void AssociatedObjectOnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            var screenPosition = e.GetPosition(AssociatedObject);
            var worldPosition = AssociatedObject.Viewport.ScreenToWorld(screenPosition.X, screenPosition.Y);
            WorldPosition = new Point(worldPosition.X, -worldPosition.Y); // OSM coordinate system,opposite number of y-axis.
            PixelPosition = new Point(worldPosition.X / Resolution, -worldPosition.Y / Resolution);
        }

        /// <summary>
        /// World position(um).
        /// </summary>
        public Point WorldPosition
        {
            get { return (Point)GetValue(WorldPositionProperty); }
            set { SetValue(WorldPositionProperty, value); }
        }

        // Using a DependencyProperty as the backing store for MyProperty.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty WorldPositionProperty =
            DependencyProperty.Register("WorldPosition", typeof(Point), typeof(ViewportBehavior), new PropertyMetadata(new Point()));


        /// <summary>
        /// Pixel postion(pixel).
        /// </summary>
        public Point PixelPosition
        {
            get { return (Point)GetValue(PixelPositionProperty); }
            set { SetValue(PixelPositionProperty, value); }
        }

        // Using a DependencyProperty as the backing store for PixelPosition.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty PixelPositionProperty =
            DependencyProperty.Register("PixelPosition", typeof(Point), typeof(ViewportBehavior), new PropertyMetadata(new Point()));

        /// <summary>
        /// Resolution(um/pixel).
        /// </summary>
        public double Resolution
        {
            get { return (double)GetValue(ResolutionProperty); }
            set { SetValue(ResolutionProperty, value); }
        }

        // Using a DependencyProperty as the backing store for Resolution.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty ResolutionProperty =
            DependencyProperty.Register("Resolution", typeof(double), typeof(ViewportBehavior), new PropertyMetadata(1d, (_1, _2) =>
             {
                 if (_1 is ViewportBehavior behavior && behavior.isViewportChanged == false && _2.NewValue is double value && value > 0)
                 {
                     behavior.AssociatedObject.Navigator.ZoomTo(value);
                 }
             }));
    }
}
