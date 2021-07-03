using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Animation;

namespace SlideLibrary.Demo
{
    /// <summary>
    /// Delay binding converter.
    /// </summary>
    /// <remarks>
    /// some property in xaml need binding other control's property before the control is created.
    /// </remarks>
    public class DelayLoadConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is DiscreteObjectKeyFrame tmp)
                return tmp.Value;
            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
