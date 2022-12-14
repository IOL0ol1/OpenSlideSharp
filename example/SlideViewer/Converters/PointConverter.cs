using Mapsui.Geometries;
using System;
using System.Globalization;
using System.Windows.Data;

namespace SlideLibrary.Demo
{
    public class PointConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Point point)
            {
                return $"{point.X},{point.Y}";
            }
            return "0,0";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string str && str.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries) is string[] strs && strs.Length == 2)
            {
                return new Point(double.TryParse(strs[0], out var r1) ? r1 : 0, double.TryParse(strs[1], out var r2) ? r2 : 0);

            }
            return new Point(0, 0);
        }
    }
}
