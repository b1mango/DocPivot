using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DocPivot.App.Controls;

/// <summary>Turns an icon key such as "PhFilePdfL" into its geometry from Themes/Icons.xaml.</summary>
public sealed class ResourceGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key ? Application.Current?.TryFindResource(key) as Geometry : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
