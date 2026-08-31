using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Bearing.App.Connections;
using Bearing.Core.Data;

namespace Bearing.App.Converters;

/// <summary>
/// Renders a <see cref="ConnectionInfo"/> as its endpoint for a tooltip, through the one shared spelling
/// (<see cref="ConnectionEndpoint"/>, #79). A converter rather than a display property because
/// <c>ConnectionInfo</c> lives in the dependency-free <c>Core</c> (§2.1) and cannot carry UI text.
/// <para><c>ConverterParameter="HostPort"</c> or <c>"Address"</c> selects a shorter form; the default is the
/// fullest, <c>user@host:port/database</c>.</para>
/// </summary>
public sealed class ConnectionEndpointConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ConnectionInfo info) return null;
        return (parameter as string) switch
        {
            "HostPort" => ConnectionEndpoint.HostPort(info),
            "Address" => ConnectionEndpoint.Address(info),
            _ => ConnectionEndpoint.Full(info),
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
