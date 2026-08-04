using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Bearing.App.Converters;

/// <summary>
/// Two-way maps an enum value to a bool by comparing it to a parameter name — for binding a group of
/// RadioButtons (rail tiles, filter pills) to a single enum property. <c>Convert</c> returns true when
/// the value's name equals the parameter; <c>ConvertBack</c> returns the parsed enum when checked, and
/// <see cref="BindingOperations.DoNothing"/> when unchecked (so only the newly-checked button writes).
/// </summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public static readonly EnumToBoolConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && parameter is not null
           && string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is not null)
        {
            var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (enumType.IsEnum)
                return Enum.Parse(enumType, parameter.ToString()!);
        }
        return BindingOperations.DoNothing;
    }
}
