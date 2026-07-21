using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Squirrel.App.Formatting;
using Squirrel.App.Input;
using Squirrel.App.Results;
using Squirrel.App.ViewModels;
using Squirrel.Core.Workspace;
using Path = Avalonia.Controls.Shapes.Path;

namespace Squirrel.App.Controls;

public sealed partial class ResultView
{
    // ---- Cell inspector (large-value / JSON viewer, design RESULTS_GRID §6) -------------------

    private void ShowInspector(ResultSetViewModel result, int index, object?[] row)
    {
        _inspect = (result, index, row);
        RenderInspector();
    }

    private void HideInspector()
    {
        _inspect = null;
        RenderInspector();
    }

    /// <summary>Populate (or clear) the live inspector pane without a full Rebuild — keeps grid scroll.</summary>
    private void RenderInspector()
    {
        if (_inspectorHost is null || _inspectorCol is null) return;
        if (_inspect is { } ins)
        {
            _inspectorHost.Content = BuildInspector(ins.Result, ins.Index, ins.Row);
            _inspectorCol.Width = new GridLength(_inspectorWidth, GridUnitType.Pixel);
            _inspectorCol.MinWidth = 240;
            if (_inspectorSplitter is not null) _inspectorSplitter.IsVisible = true;
        }
        else
        {
            // Remember the dragged width before collapsing so re-opening keeps it.
            if (_inspectorCol.Width.IsAbsolute && _inspectorCol.Width.Value > 0) _inspectorWidth = _inspectorCol.Width.Value;
            _inspectorHost.Content = null;
            _inspectorCol.MinWidth = 0;
            _inspectorCol.Width = new GridLength(0);
            if (_inspectorSplitter is not null) _inspectorSplitter.IsVisible = false;
        }
    }

    private Control BuildInspector(ResultSetViewModel result, int index, object?[] row)
    {
        var raw = CellText(row, index);
        var colName = result.Columns[index].Name;
        var typeName = result.Columns[index].DataTypeName;
        var parsed = JsonTree.Parse(raw);
        var isJson = parsed is not null && (IsJsonType(typeName) || LooksJson(raw));

        // Header: film[<id>].<column> + type badge + copy + close.
        var title = new TextBlock
        {
            Text = $"{result.EditTarget?.Table ?? "row"}[{KeyDisplay(result, row)}].{colName}",
            Foreground = Res("Text.Primary"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var typeBadge = Badge(isJson ? (IsJsonType(typeName) ? typeName.ToLowerInvariant() : "json") : "text",
            isJson ? "Syntax.Table" : "Text.Dim");

        var copy = IconTextButton("⧉", "Copy value");
        copy.Click += (_, _) => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(isJson ? JsonTree.Prettify(raw) : raw);
        var close = IconTextButton("✕", "Close");
        close.Click += (_, _) => HideInspector();

        var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
        var titleWrap = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleWrap.Children.Add(title);
        titleWrap.Children.Add(typeBadge);
        Grid.SetColumn(titleWrap, 0);
        Grid.SetColumn(copy, 2);
        Grid.SetColumn(close, 3);
        headerRow.Children.Add(titleWrap);
        headerRow.Children.Add(copy);
        headerRow.Children.Add(close);
        var header = new Border
        {
            Background = Res("Bg.Window"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = Separator,
            Padding = new Thickness(10, 6),
            Child = headerRow,
        };
        DockPanel.SetDock(header, Dock.Top);

        var bodyHost = new ContentControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        var formatted = true;
        var find = "";

        void RenderBody()
        {
            if (isJson && formatted && parsed is not null)
            {
                JsonTree.ApplyFind(parsed, find);
                bodyHost.Content = new ScrollViewer { Content = BuildJsonTreeView(parsed), Padding = new Thickness(8) };
            }
            else
            {
                var box = new TextBox
                {
                    Text = raw,
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Foreground = Res("Text.Code"),
                    Margin = new Thickness(8),
                };
                bodyHost.Content = new ScrollViewer { Content = box };
            }
        }

        // Toolbar (JSON only): Formatted/Raw toggle + collapse/expand all + find.
        Control? toolbar = null;
        if (isJson && parsed is not null)
        {
            var fmtToggle = IconTextButton("Formatted", "Show as tree");
            var rawToggle = IconTextButton("Raw", "Show raw text");
            void SyncToggles()
            {
                fmtToggle.Foreground = formatted ? Res("Accent.Orange") : Res("Text.Dim");
                rawToggle.Foreground = formatted ? Res("Text.Dim") : Res("Accent.Orange");
            }
            fmtToggle.Click += (_, _) => { formatted = true; SyncToggles(); RenderBody(); };
            rawToggle.Click += (_, _) => { formatted = false; SyncToggles(); RenderBody(); };
            SyncToggles();

            var collapseAll = IconTextButton("⊟", "Collapse all");
            collapseAll.Click += (_, _) => { JsonTree.SetExpandedAll(parsed, false); RenderBody(); };
            var expandAll = IconTextButton("⊞", "Expand all");
            expandAll.Click += (_, _) => { JsonTree.SetExpandedAll(parsed, true); RenderBody(); };

            var findBox = new TextBox { PlaceholderText = "Find in value…", Width = 150, Margin = new Thickness(8, 0, 0, 0) };
            findBox.TextChanged += (_, _) => { find = findBox.Text ?? ""; if (formatted) RenderBody(); };

            var tb = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            tb.Children.Add(fmtToggle);
            tb.Children.Add(rawToggle);
            tb.Children.Add(collapseAll);
            tb.Children.Add(expandAll);
            tb.Children.Add(findBox);
            toolbar = new Border
            {
                Background = Res("Bg.Chrome"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = Separator,
                Padding = new Thickness(8, 4),
                Child = tb,
            };
            DockPanel.SetDock(toolbar, Dock.Top);
        }

        RenderBody();

        var panel = new DockPanel { LastChildFill = true };
        panel.Children.Add(header);
        if (toolbar is not null) panel.Children.Add(toolbar);
        panel.Children.Add(bodyHost);

        return new Border
        {
            Background = Res("Bg.Editor"),
            BorderThickness = new Thickness(1, 0, 0, 0),
            BorderBrush = Separator,
            Child = panel, // width comes from the resizable grid column
        };
    }

    private static bool LooksJson(string raw)
    {
        var t = raw.TrimStart();
        return t.StartsWith('{') || t.StartsWith('[');
    }

    /// <summary>The first primary-key value in the row (for the inspector header), or "?" if none.</summary>
    private static string KeyDisplay(ResultSetViewModel result, object?[] row)
    {
        foreach (var i in result.PrimaryKeyColumns)
            if (i < row.Length && row[i] is not null) return CellFormat.Display(row[i]);
        return "?";
    }

    private TreeView BuildJsonTreeView(JsonTreeNode root)
    {
        var tree = new TreeView { ItemsSource = new[] { root }, Background = Brushes.Transparent };
        tree.ItemTemplate = new FuncTreeDataTemplate<JsonTreeNode>(
            (n, _) => BuildJsonNodeVisual(n), n => n.Children);
        // Reflect each node's fold state (find/collapse-all drive it from the model).
        var style = new Style(x => x.OfType<TreeViewItem>());
        style.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty,
            new Binding(nameof(JsonTreeNode.IsExpanded)) { Mode = BindingMode.TwoWay }));
        tree.Styles.Add(style);
        return tree;
    }

    private static Control BuildJsonNodeVisual(JsonTreeNode node)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (node.Key is not null)
        {
            line.Children.Add(new TextBlock { Text = node.Key, Foreground = Res("Syntax.Func") });      // key: blue
            line.Children.Add(new TextBlock { Text = ": ", Foreground = Res("Text.Dim") });
        }
        if (node.IsContainer)
            line.Children.Add(new TextBlock { Text = node.CollapsedSummary, Foreground = Res("Text.Dim") });
        else
        {
            var disp = node.Kind == JsonNodeKind.String ? $"\"{node.Value}\"" : node.Value ?? "null";
            line.Children.Add(new TextBlock { Text = disp, Foreground = Res(ColorKeyForKind(node.Kind)) });
        }

        var wrap = new Border { Child = line, CornerRadius = new CornerRadius(3), Padding = new Thickness(2, 0) };
        wrap.Bind(Border.BackgroundProperty, new Binding(nameof(JsonTreeNode.IsMatch)) { Converter = MatchHighlight });
        return wrap;
    }

    private static string ColorKeyForKind(JsonNodeKind kind) => kind switch
    {
        JsonNodeKind.String => "Ok.Green",
        JsonNodeKind.Number => "Syntax.Number",
        JsonNodeKind.Boolean or JsonNodeKind.Null => "Syntax.Keyword",
        _ => "Text.Primary",
    };

    /// <summary>A borderless text/glyph button used for inspector controls (copy, close, toggles).</summary>
    private static Button IconTextButton(string content, string tip)
    {
        var b = new Button
        {
            Content = content,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2),
            Foreground = Res("Text.Dim"),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(b, tip);
        return b;
    }

}
