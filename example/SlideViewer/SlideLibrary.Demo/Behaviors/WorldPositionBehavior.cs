using System.Windows;
using Mapsui.UI.Wpf;
using Microsoft.Xaml.Behaviors;

namespace SlideLibrary.Demo
{
    /// <summary>
    /// Show cursor world position.
    /// </summary>
    public class WorldPositionBehavior : Behavior<MapControl>
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

        private void ViewportViewportChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Resolution))
                Resolution = AssociatedObject.Viewport.Resolution;
        }

        private void AssociatedObjectOnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            var screenPosition = e.GetPosition(AssociatedObject);
            var worldPosition = AssociatedObject.Viewport.ScreenToWorld(screenPosition.X, screenPosition.Y);
            WorldPosition = new Point(worldPosition.X, -worldPosition.Y); // OSM coordinate system,opposite number of y-axis.
            WorldPositionString = $"{WorldPosition.X:F2},{WorldPosition.Y:F2}";
        }

        /// <summary>
        /// World position.
        /// </summary>
        public Point WorldPosition
        {
            get { return (Point)GetValue(WorldPositionProperty); }
            set { SetValue(WorldPositionProperty, value); }
        }

        // Using a DependencyProperty as the backing store for MyProperty.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty WorldPositionProperty =
            DependencyProperty.Register("WorldPosition", typeof(Point), typeof(WorldPositionBehavior), new PropertyMetadata(new Point()));

        /// <summary>
        /// World position string.
        /// </summary>
        public string WorldPositionString
        {
            get { return (string)GetValue(WorldPositionStringProperty); }
            set { SetValue(WorldPositionStringProperty, value); }
        }

        // Using a DependencyProperty as the backing store for WorldPositionString.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty WorldPositionStringProperty =
            DependencyProperty.Register("WorldPositionString", typeof(string), typeof(WorldPositionBehavior), new PropertyMetadata("0,0"));

        /// <summary>
        /// Resolution(um/pixel)
        /// </summary>
        public double Resolution
        {
            get { return (double)GetValue(ResolutionProperty); }
            set { SetValue(ResolutionProperty, value); }
        }

        // Using a DependencyProperty as the backing store for Resolution.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty ResolutionProperty =
            DependencyProperty.Register("Resolution", typeof(double), typeof(WorldPositionBehavior), new PropertyMetadata(0d));
    }
}
