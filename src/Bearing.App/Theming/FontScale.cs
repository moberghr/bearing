using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace Bearing.App.Theming;

/// <summary>
/// The app's type scale, as runtime-mutable resources (#52).
/// <para>
/// Font sizes used to be literals — roughly thirty of them across seven files — which is why they could not
/// be made settable: a setting plumbed into a dozen call sites is a setting the next control added to a panel
/// quietly ignores. They are tokens now, the way colour already is, and this moves them. The precedent is
/// <c>App.SetConnectionAccent</c>: mutate the resource, and every <c>{DynamicResource}</c> consumer follows
/// without being told.
/// </para>
/// <para>
/// Two dials rather than one per panel: <b>Ui</b> for the chrome and <b>Grid</b> for the data. The grid is
/// the surface you stare at longest and wants its own size; splitting the chrome five ways would ship five
/// settings before anyone knows which are used, and per-panel overrides can be added later without moving
/// anything that reads a token.
/// </para>
/// </summary>
public static class FontScale
{
    /// <summary>Bounds for both dials. Below the floor the chrome stops being legible and the grid's row
    /// metric collapses; above the ceiling a panel's fixed widths start truncating labels.</summary>
    public const int MinSize = 9;

    public const int MaxSize = 22;

    /// <summary>Default chrome size — what the literals it replaced mostly were.</summary>
    public const int DefaultUiSize = 12;

    /// <summary>Default grid size. 13 since #30, which sized the columns for it.</summary>
    public const int DefaultGridSize = 13;

    /// <summary>
    /// Rewrite the chrome sizes from one dial.
    /// <para>
    /// Relative, not absolute: caption and small sit one and two points under body, so raising the dial keeps
    /// the hierarchy rather than flattening everything to one size. Each is floored, or a small dial would
    /// drive the caption to nothing.
    /// </para>
    /// </summary>
    public static void ApplyUi(int size)
    {
        var body = Clamp(size);
        Set("Font.Body", body);
        Set("Font.Small", Math.Max(MinSize - 2, body - 1));
        Set("Font.Caption", Math.Max(MinSize - 3, body - 3));
    }

    /// <summary>
    /// Rewrite the grid sizes and the row height together.
    /// <para>
    /// The row floor has to move with the font: a grid font above ~15 clips against a fixed 26px row, and one
    /// below ~11 leaves the rows looking padded. So density is derived here rather than being a second
    /// setting that fights this one (#30).
    /// </para>
    /// </summary>
    public static void ApplyGrid(int size)
    {
        var grid = Clamp(size);
        Set("Font.Grid", grid);
        // The header sits a point under the values, as it did when both were literals.
        Set("Font.GridHeader", Math.Max(MinSize - 2, grid - 1));
        Set("Height.GridRow", RowHeightFor(grid));
    }

    /// <summary>
    /// The row floor for a grid font size: the text plus the vertical breathing room a row needs to read as a
    /// row rather than as a wall. Twice the size is what the original 26-at-13 amounted to, so the default is
    /// unchanged and every other size is consistent with it.
    /// </summary>
    public static double RowHeightFor(int gridSize) => Math.Round(Clamp(gridSize) * 2.0);

    /// <summary>Both dials at once, from settings, on startup and on every change.</summary>
    public static void Apply(int uiSize, int gridSize)
    {
        ApplyUi(uiSize);
        ApplyGrid(gridSize);
    }

    public static int Clamp(int size) => Math.Clamp(size, MinSize, MaxSize);

    /// <summary>
    /// Resolved sizes, so a lookup is not a resource-dictionary walk. These are read <b>per cell</b> while a
    /// grid is built — the column-width pass measures every sampled value at
    /// <c>ResultGridChrome.CellFontSize</c> — so an uncached <c>FindResource</c> here is a dictionary walk per
    /// cell on a wide result.
    /// <para>
    /// Keyed by the owning <see cref="Application"/>, not merely cached: the value depends on which
    /// application's resources answered, so an unconditional static would let whichever test ran first decide
    /// it for every later one. That is the same trap <c>ThemeBrush.AtAlphaCached</c> exists for, and it made
    /// an earlier suite pass or fail on test order (§4.5).
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, double> Cache = new();

    private static Application? _cachedFor;

    private static readonly object Gate = new();

    /// <summary>
    /// The current value of a size token, or <paramref name="fallback"/> when there is no application (a unit
    /// test, a designer) or the key is missing. Never throws: a missing token must not take a window down
    /// while it is being built.
    /// </summary>
    public static double Get(string key, double fallback)
    {
        var app = Application.Current;
        if (app is null) return fallback;

        lock (Gate)
        {
            if (!ReferenceEquals(_cachedFor, app))
            {
                // A different application (a new headless test, a restarted shell) resolves its own values.
                Cache.Clear();
                _cachedFor = app;
            }
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var value = app.FindResource(key) is double found ? found : fallback;
            Cache[key] = value;
            return value;
        }
    }

    private static void Set(string key, double value)
    {
        if (Application.Current is not { } app) return;
        app.Resources[key] = value;
        lock (Gate)
        {
            // Written through rather than invalidated wholesale: the only writer is this class, so the cache
            // is never stale in a way a full clear would fix and a partial one would not.
            Cache[key] = value;
            _cachedFor = app;
        }
    }
}
