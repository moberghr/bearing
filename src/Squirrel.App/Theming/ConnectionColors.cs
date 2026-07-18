using Avalonia.Media;

namespace Squirrel.App.Theming;

/// <summary>
/// Resolves a connection's environment hex color to the app-wide accent used by
/// <c>ConnectionBrush</c> (tab accent, dots, results accent, status-bar line). An untagged or
/// unparseable connection falls back to a neutral slate so the accent still renders subtly.
/// </summary>
public static class ConnectionColors
{
    /// <summary>Neutral accent for connections with no (or an invalid) environment color.</summary>
    public static readonly Color Neutral = Color.FromRgb(0x54, 0x54, 0x6D); // Text.Faint

    /// <summary>Parse an environment hex string ("#E46876") to a <see cref="Color"/>; neutral on null/blank/invalid.</summary>
    public static Color Resolve(string? hex)
        => !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var color) ? color : Neutral;
}
