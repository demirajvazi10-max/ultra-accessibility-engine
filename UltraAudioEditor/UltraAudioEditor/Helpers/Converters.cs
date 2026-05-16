using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace UltraAudioEditor.Helpers
{
    public class BoolToMuteBrushConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
            => (bool)value ? new SolidColorBrush(Color.FromRgb(186, 117, 23)) : new SolidColorBrush(Color.FromRgb(49, 49, 69));
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => DependencyProperty.UnsetValue;
    }

    public class BoolToSoloBrushConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
            => (bool)value ? new SolidColorBrush(Color.FromRgb(29, 158, 117)) : new SolidColorBrush(Color.FromRgb(49, 49, 69));
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => DependencyProperty.UnsetValue;
    }

    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
            => (bool)value ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => DependencyProperty.UnsetValue;
    }

    public class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) => !(bool)value;
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => !(bool)v;
    }

    /// Pretvara int 0 u Visible, sve ostalo u Collapsed (za prikaz poruke prazne trake)
    public class ZeroToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
            => (value is int i && i == 0) ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => DependencyProperty.UnsetValue;
    }
}
