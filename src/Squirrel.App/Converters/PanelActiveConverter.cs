using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Squirrel.App.Converters;

/// <summary>
/// Rail-tile highlight: true only when the active panel equals the parameter <b>and</b> the side pane
/// is open — so collapsing the pane leaves every tile unselected. Inputs: [ActivePanel, SidePaneOpen].
/// One-way (the tile's click drives state via ActivateOrTogglePanel).
/// </summary>
public sealed class PanelActiveConverter : IMultiValueConverter
{
    public static readonly PanelActiveConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => values.Count == 2
           && values[0] is not null
           && string.Equals(values[0]!.ToString(), parameter?.ToString(), StringComparison.Ordinal)
           && values[1] is true;
}
