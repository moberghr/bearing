using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Squirrel.App.Converters;

/// <summary>
/// Resolves a resource key string (e.g. "Icon.Database") to the <see cref="Geometry"/> stored under it
/// in application resources, so a tree node can pick its icon by name. Unknown key → null (no icon).
/// </summary>
public sealed class ResourceGeometryConverter : IValueConverter
{
    public static readonly ResourceGeometryConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string key && Application.Current?.FindResource(key) is Geometry g ? g : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
