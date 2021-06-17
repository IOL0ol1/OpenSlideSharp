using System.Windows;
using System.Windows.Controls;
using Mapsui.UI.Wpf;
using Microsoft.Xaml.Behaviors;

namespace SlideLibrary.Test
{
    /// <summary>
    /// Zoom slider behavior.
    /// </summary>
    public class ZoomBehavior : Behavior<Slider>
    {

        protected override void OnAttached()
        {
            base.OnAttached();
            AssociatedObject.ValueChanged += AssociatedObject_ValueChanged;
        }


        private bool isSliderChanged = false;
        private bool isMapControlChanged = false;
        private double lastResolution;
        private void Viewport_ViewportChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            var curResolution = MapControl.Viewport.Resolution;
            if (lastResolution != curResolution && !isSliderChanged)
            {
                isMapControlChanged = true;
                AssociatedObject.Value = curResolution;
                isMapControlChanged = false;
            }
            lastResolution = curResolution;
        }

        private void AssociatedObject_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!isMapControlChanged)
            {
                isSliderChanged = true;
                MapControl.Navigator.ZoomTo(AssociatedObject.Value);
                isSliderChanged = false;
            }
        }

        protected override void OnDetaching()
        {
            AssociatedObject.ValueChanged -= AssociatedObject_ValueChanged;
            if (MapControl != null)
                MapControl.Viewport.ViewportChanged -= Viewport_ViewportChanged;
            base.OnDetaching();
        }

        /// <summary>
        /// Main map control.
        /// </summary>
        public MapControl MapControl
        {
            get { return (MapControl)GetValue(MapControlProperty); }
            set { SetValue(MapControlProperty, value); }
        }

        // Using a DependencyProperty as the backing store for MyProperty.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty MapControlProperty =
            DependencyProperty.Register("MapControl", typeof(MapControl), typeof(ZoomBehavior), new PropertyMetadata(default, (_1, _2) =>
             {
                 if (_1 is ZoomBehavior behavior)
                 {
                     if (_2.OldValue is MapControl map)
                         map.Viewport.ViewportChanged -= behavior.Viewport_ViewportChanged;
                     if (_2.NewValue is MapControl map1)
                         map1.Viewport.ViewportChanged += behavior.Viewport_ViewportChanged;
                 }
             }));

    }
}
