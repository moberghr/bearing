using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using static Bearing.App.Controls.Tokens;
using Avalonia.Input.Platform;

namespace Bearing.App.Controls;

/// <summary>
/// The cell inspector: one cell's full value, shown either as a foldable JSON tree or as raw text
/// (design RESULTS_GRID §6). Header names the cell and offers copy + close; the JSON toolbar adds a
/// Formatted/Raw toggle, collapse/expand-all, and a find box that highlights matching nodes.
/// <para>
/// Self-contained — it takes the value and a close callback, and never reaches back into the grid. The
/// collapsible pane it lives in is <see cref="InspectorPane"/>.
/// </para>
/// </summary>
public sealed class CellInspectorView : UserControl
{
    /// <summary>Highlight brush for JSON nodes matching the find query.</summary>
    private static readonly FuncValueConverter<bool, IBrush> MatchHighlight =
        new(m => m ? Tint("Accent.Brand", 0x55) : Brushes.Transparent);

    private readonly string _raw;
    private readonly JsonTreeNode? _parsed;
    private readonly bool _isJson;
    private readonly ContentControl _bodyHost = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private bool _formatted = true;
    private string _find = "";

    /// <summary>Build an inspector for a result's cell.</summary>
    public static CellInspectorView For(ResultSetViewModel result, int index, object?[] row, Action onClose)
        => new(
            title: ResultMetaText.InspectorTitle(result, index, row),
            raw: GridSelectionOps.CellText(row, index),
            typeName: result.Columns[index].DataTypeName,
            onClose: onClose);

    public CellInspectorView(string title, string raw, string typeName, Action onClose)
    {
        _raw = raw;
        _parsed = JsonTree.Parse(raw);
        // A declared json/jsonb column, or any value whose text opens like a document.
        _isJson = _parsed is not null && (ColumnKinds.IsJson(typeName) || ColumnKinds.LooksJson(raw));

        var panel = new DockPanel { LastChildFill = true };
        panel.Children.Add(BuildHeader(title, typeName, onClose));
        if (BuildJsonToolbar() is { } toolbar) panel.Children.Add(toolbar);
        panel.Children.Add(_bodyHost);
        RenderBody();

        Content = new Border
        {
            Background = Res("Bg.Editor"),
            BorderThickness = new Thickness(1, 0, 0, 0),
            BorderBrush = SeparatorBrush,
            Child = panel, // width comes from the resizable grid column
        };
    }

    /// <summary>Header: <c>film[42].description</c> + a type badge, then copy and close.</summary>
    private Control BuildHeader(string title, string typeName, Action onClose)
    {
        var caption = new TextBlock
        {
            Text = title,
            Foreground = Res("Text.Primary"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var badge = ResultChrome.Badge(
            _isJson ? (ColumnKinds.IsJson(typeName) ? typeName.ToLowerInvariant() : "json") : "text",
            _isJson ? "Syntax.Table" : "Text.Dim");

        var copy = ResultChrome.IconTextButton("⧉", "Copy value");
        copy.Click += (_, _) => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(
            _isJson ? JsonTree.Prettify(_raw) : _raw);
        var close = ResultChrome.IconTextButton("✕", "Close");
        close.Click += (_, _) => onClose();

        var titleWrap = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleWrap.Children.Add(caption);
        titleWrap.Children.Add(badge);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
        Grid.SetColumn(titleWrap, 0);
        Grid.SetColumn(copy, 2);
        Grid.SetColumn(close, 3);
        row.Children.Add(titleWrap);
        row.Children.Add(copy);
        row.Children.Add(close);

        var header = new Border
        {
            Background = Res("Bg.Window"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = SeparatorBrush,
            Padding = new Thickness(10, 6),
            Child = row,
        };
        DockPanel.SetDock(header, Dock.Top);
        return header;
    }

    /// <summary>Formatted/Raw toggle + collapse/expand all + find. Null for a non-JSON value.</summary>
    private Control? BuildJsonToolbar()
    {
        if (!_isJson || _parsed is null) return null;

        var fmtToggle = ResultChrome.IconTextButton("Formatted", "Show as tree");
        var rawToggle = ResultChrome.IconTextButton("Raw", "Show raw text");
        void SyncToggles()
        {
            fmtToggle.Foreground = _formatted ? Res("Accent.Brand") : Res("Text.Dim");
            rawToggle.Foreground = _formatted ? Res("Text.Dim") : Res("Accent.Brand");
        }
        fmtToggle.Click += (_, _) => { _formatted = true; SyncToggles(); RenderBody(); };
        rawToggle.Click += (_, _) => { _formatted = false; SyncToggles(); RenderBody(); };
        SyncToggles();

        var collapseAll = ResultChrome.IconTextButton("⊟", "Collapse all");
        collapseAll.Click += (_, _) => { JsonTree.SetExpandedAll(_parsed, false); RenderBody(); };
        var expandAll = ResultChrome.IconTextButton("⊞", "Expand all");
        expandAll.Click += (_, _) => { JsonTree.SetExpandedAll(_parsed, true); RenderBody(); };

        var findBox = new TextBox { PlaceholderText = "Find in value…", Width = 150, Margin = new Thickness(8, 0, 0, 0) };
        findBox.TextChanged += (_, _) => { _find = findBox.Text ?? ""; if (_formatted) RenderBody(); };

        var tb = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        tb.Children.Add(fmtToggle);
        tb.Children.Add(rawToggle);
        tb.Children.Add(collapseAll);
        tb.Children.Add(expandAll);
        tb.Children.Add(findBox);

        var toolbar = new Border
        {
            Background = Res("Bg.Chrome"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = SeparatorBrush,
            Padding = new Thickness(8, 4),
            Child = tb,
        };
        DockPanel.SetDock(toolbar, Dock.Top);
        return toolbar;
    }

    private void RenderBody()
    {
        if (_isJson && _formatted && _parsed is not null)
        {
            JsonTree.ApplyFind(_parsed, _find);
            _bodyHost.Content = new ScrollViewer { Content = BuildJsonTreeView(_parsed), Padding = new Thickness(8) };
            return;
        }
        var box = new TextBox
        {
            Text = _raw,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Res("Text.Code"),
            Margin = new Thickness(8),
        };
        _bodyHost.Content = new ScrollViewer { Content = box };
    }

    private static TreeView BuildJsonTreeView(JsonTreeNode root)
    {
        var tree = new TreeView { ItemsSource = new[] { root }, Background = Brushes.Transparent };
        tree.ItemTemplate = new FuncTreeDataTemplate<JsonTreeNode>(
            (n, _) => BuildJsonNodeVisual(n), n => n.Children);
        // Reflect each node's fold state (find / collapse-all drive it from the model).
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
}
