using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Bearing.App.Connections;
using Bearing.Core.Data;
using Bearing.Persistence.Import;

namespace Bearing.App.Views;

/// <summary>What the user chose in the import dialog: which connections to take, and what to do with ones
/// that already exist. Null from <c>ShowDialog</c> means cancelled.</summary>
public sealed record ImportChoice(IReadOnlyList<ConnectionInfo> Connections, bool UpdateExisting);

/// <summary>
/// Reviews a parsed DBeaver workspace before anything is written (#72). Deliberately a review step rather
/// than a one-click import: a real workspace is mixed, most of it is usually not Postgres, and the rows that
/// do come across arrive without a user name or a password. Showing that up front is the difference between
/// an import that looks broken and one the user understands.
/// </summary>
public sealed class ImportConnectionsDialog : Window
{
    private readonly List<(CheckBox Box, ConnectionInfo Connection)> _rows = new();
    private readonly CheckBox _updateExisting;

    public ImportConnectionsDialog(DBeaverImportResult result, string sourcePath)
    {
        Title = "Import connections from DBeaver";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _updateExisting = new CheckBox
        {
            Content = "Update connections that already point at the same server",
            IsChecked = true,
            // Matching is on host+port+database+user, so unchecking this is "add only what's new" rather
            // than "make duplicates".
            [ToolTip.TipProperty] = "Matched on host, port, database and user — never on name. "
                                  + "An updated connection keeps its saved password.",
        };

        var body = new StackPanel { Spacing = 10, Margin = new Thickness(16) };
        body.Children.Add(new TextBlock
        {
            Text = sourcePath,
            FontSize = 11,
            Opacity = 0.7,
            TextTrimming = TextTrimming.CharacterEllipsis,
            [ToolTip.TipProperty] = sourcePath,
        });

        body.Children.Add(result.Connections.Count == 0
            ? (Control)Note("Nothing here can be imported — see below.")
            : BuildConnectionList(result));

        foreach (var warning in result.Warnings) body.Children.Add(Note(warning));
        if (result.Skipped.Count > 0) body.Children.Add(BuildSkipped(result));
        if (result.Connections.Count > 0) body.Children.Add(_updateExisting);

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(null);

        var import = new Button
        {
            Content = "Import",
            IsDefault = true,
            IsEnabled = result.Connections.Count > 0,
            Margin = new Thickness(8, 0, 0, 0),
        };
        import.Click += (_, _) => Close(new ImportChoice(Chosen(), _updateExisting.IsChecked == true));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(import);
        body.Children.Add(buttons);

        Content = new ScrollViewer { Content = body, MaxHeight = 560 };
    }

    private IReadOnlyList<ConnectionInfo> Chosen()
        => _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Connection).ToList();

    private Control BuildConnectionList(DBeaverImportResult result)
    {
        var list = new StackPanel { Spacing = 2 };
        list.Children.Add(Heading($"{result.Connections.Count} connection"
                                  + (result.Connections.Count == 1 ? "" : "s") + " to import"));

        foreach (var c in result.Connections)
        {
            var box = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
            _rows.Add((box, c));

            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            label.Children.Add(new TextBlock { Text = c.Name, VerticalAlignment = VerticalAlignment.Center });
            // The endpoint in the same spelling the tree, the tooltip and the failure message use (#79).
            label.Children.Add(new TextBlock
            {
                Text = ConnectionEndpoint.Address(c),
                FontSize = 11,
                Opacity = 0.65,
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (c.Folder is { } folder)
                label.Children.Add(new TextBlock
                {
                    Text = $"→ {folder}",
                    FontSize = 11,
                    Opacity = 0.5,
                    VerticalAlignment = VerticalAlignment.Center,
                });

            box.Content = label;
            list.Children.Add(box);
        }
        return list;
    }

    private static Control BuildSkipped(DBeaverImportResult result)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(Heading($"{result.Skipped.Count} not imported"));
        foreach (var s in result.Skipped)
            panel.Children.Add(new TextBlock
            {
                Text = $"{s.Name} — {s.Reason}",
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });
        return panel;
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = 11,
        FontWeight = FontWeight.Bold,
        Opacity = 0.6,
        Margin = new Thickness(0, 4, 0, 2),
    };

    private static Border Note(string text) => new()
    {
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(10, 7),
        Background = new SolidColorBrush(Color.FromArgb(0x1E, 0xD2, 0x99, 0x22)),
        Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 },
    };
}
