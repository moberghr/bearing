using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Bearing.App.ViewModels;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Controls;

/// <summary>
/// Paints a results row's background: the design zebra stripe normally, overridden by a pending-edit tint
/// plus a 2px left status bar when the row has an unsaved change. Kept in one place because the tint is
/// applied from four unrelated moments — a row realizing on scroll, a cell edit committing, a delete being
/// marked, and an in-place save clearing everything — and they must agree on the colors.
/// </summary>
public static class ResultRowPainter
{
    /// <summary>Subtle in-grid cell separator (design §Results grid: 1px #232A33 row + column dividers).</summary>
    public static readonly IBrush GridLine = new SolidColorBrush(Color.FromRgb(0x23, 0x2A, 0x33));

    /// <summary>Design row striping: a subtle neutral lift on alternate rows over the flat Bg.Editor body
    /// (the handoff's rgba(255,255,255,.022) zebra tint, flattened over ink-700).</summary>
    private static readonly IBrush RowStripe = new SolidColorBrush(Color.FromRgb(0x1E, 0x24, 0x2B));

    /// <summary>Striped background per row parity — odd (0-based) rows lift, even rows stay transparent
    /// so the grid's flat Bg.Editor surface shows through.</summary>
    public static IBrush RowBackground(int rowIndex) => rowIndex % 2 == 1 ? RowStripe : Brushes.Transparent;

    /// <summary>Pending-edit visuals for a row: a faint tint + a 2px left status bar
    /// (amber edited / green new / red deleted). Transparent when the row has no pending change.</summary>
    public static (IBrush Tint, IBrush Bar) RowStatus(ResultSetViewModel result, object?[]? row)
    {
        if (row is null) return (Brushes.Transparent, Brushes.Transparent);
        if (result.IsRowDeleted(row)) return (Tint("Error.Red", 0x2E), Res("Error.Red"));
        if (result.IsNewRow(row)) return (Tint("Ok.Green", 0x2E), Res("Ok.Green"));
        if (result.IsRowEdited(row)) return (Tint("Accent.Brand", 0x24), Res("Accent.Brand"));
        return (Brushes.Transparent, Brushes.Transparent);
    }

    /// <summary>Apply one row's stripe-or-pending-tint and its left status bar.</summary>
    public static void ApplyRowStatus(DataGridRow dgr, ResultSetViewModel result)
    {
        var (tint, bar) = RowStatus(result, dgr.DataContext as object?[]);
        // No pending change → the design row stripe; a pending edit/new/delete overrides with its tint.
        dgr.Background = ReferenceEquals(tint, Brushes.Transparent) ? RowBackground(dgr.Index) : tint;
        dgr.BorderBrush = bar;
        dgr.BorderThickness = new Thickness(2, 0, 0, 0);
    }

    /// <summary>Re-tint the currently-realized rows to reflect pending edit/new/delete state. Only realized
    /// rows exist to paint; the rest pick their color up from <c>LoadingRow</c> as they scroll in.</summary>
    public static void RefreshRowColors(DataGrid grid, ResultSetViewModel result)
    {
        foreach (var dgr in grid.GetVisualDescendants().OfType<DataGridRow>())
            ApplyRowStatus(dgr, result);
    }
}
