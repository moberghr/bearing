using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Bearing.App.Settings;
using Bearing.Core.Workspace;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Views;

/// <summary>
/// The application settings dialog. Renders itself entirely from <see cref="SettingsCatalog"/> — a
/// category list, a search box, and one row per descriptor — so a new setting needs no code here: add the
/// property and its descriptor and it shows up, searchable and resettable.
/// <para>
/// Edits apply <b>immediately</b>: a control change writes through <see cref="SettingsService"/>, which
/// persists and broadcasts. There is deliberately no Save/Cancel pair (unlike
/// <see cref="KeybindingsWindow"/>, which edits a whole keymap as one unit) — with search jumping you
/// around a long list, a half-committed working copy is the worse failure mode. Rows carrying an
/// <see cref="SettingDescriptor.AppliesNote"/> say so, so "immediately" is never overclaimed.
/// </para>
/// <para>Closes with <c>true</c> when the user asked for the keyboard-shortcuts editor instead.</para>
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly List<Row> _rows = new();
    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly TextBox _search = new()
    {
        PlaceholderText = "Search settings…",
        Margin = new Thickness(0, 0, 0, 10),
    };
    private readonly ListBox _categories = new() { Width = 150, Background = Brushes.Transparent };
    private readonly TextBlock _status = new() { Foreground = Res("Text.Faint"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Set while pushing stored values into controls, so the controls' own change events don't
    /// write straight back and fight the user.</summary>
    private bool _syncing;

    private const string AllCategories = "";

    public SettingsWindow(SettingsService settings)
    {
        _settings = settings;

        Title = "Settings";
        Width = 780;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Res("Bg.Window");

        _categories.ItemsSource = new[] { new SettingsCategory(AllCategories, "All") }
            .Concat(SettingsCatalog.Categories).ToList();
        _categories.ItemTemplate = new FuncDataTemplate<SettingsCategory>((c, _) =>
            new TextBlock { Text = c.Title, Margin = new Thickness(2, 5) }, supportsRecycling: true);
        _categories.SelectedIndex = 0;
        _categories.SelectionChanged += (_, _) => Rebuild();

        _search.TextChanged += (_, _) => Rebuild();

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(_categories, 0);
        var scroll = new ScrollViewer
        {
            Content = _list,
            Margin = new Thickness(14, 0, 0, 0),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        Grid.SetColumn(scroll, 1);
        body.Children.Add(_categories);
        body.Children.Add(scroll);

        var footer = BuildFooter();
        var root = new DockPanel { LastChildFill = true, Margin = new Thickness(14) };
        DockPanel.SetDock(_search, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(_search);
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;

        // Someone else (the file, another window) changing settings must not leave stale controls here.
        _settings.Changed += OnSettingsChanged;
        Closed += (_, _) => _settings.Changed -= OnSettingsChanged;

        Rebuild();
        Opened += (_, _) => _search.Focus();
    }

    private Control BuildFooter()
    {
        var path = new TextBlock
        {
            Text = _settings.Location,
            Foreground = Res("Text.Faint"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            [ToolTip.TipProperty] = "Settings are stored here. A few options are still file-edit only.",
        };

        var keys = new Button { Content = "Keyboard shortcuts…" };
        keys.Click += (_, _) => Close(true);

        var resetAll = new Button { Content = "Reset all" };
        resetAll.Click += (_, _) =>
        {
            _settings.ResetAll();
            _status.Text = "All settings reset to defaults.";
        };

        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close(false);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(keys);
        buttons.Children.Add(resetAll);
        buttons.Children.Add(close);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 12, 0, 0) };
        var left = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(_status);
        left.Children.Add(path);
        Grid.SetColumn(left, 0);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(left);
        footer.Children.Add(buttons);
        return footer;
    }

    // ---- list construction -------------------------------------------------------------------

    private void Rebuild()
    {
        _rows.Clear();
        _list.Children.Clear();

        var categoryId = (_categories.SelectedItem as SettingsCategory)?.Id;
        var sections = SettingsSearch.Filter(_search.Text, categoryId == AllCategories ? null : categoryId);

        if (sections.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "No settings match that search.",
                Foreground = Res("Text.Dim"),
                Margin = new Thickness(2, 18, 0, 0),
            });
            return;
        }

        foreach (var section in sections)
        {
            _list.Children.Add(SectionHeader(section.Category));
            foreach (var descriptor in section.Settings)
            {
                var row = BuildRow(descriptor);
                _rows.Add(row);
                _list.Children.Add(row.Visual);
            }
        }
        SyncRows();
    }

    private Control SectionHeader(SettingsCategory category) => new StackPanel
    {
        Margin = new Thickness(2, 14, 0, 6),
        Children =
        {
            new TextBlock
            {
                Text = category.Title.ToUpperInvariant(),
                Foreground = Res("Text.Faint"),
                FontSize = 11,
            },
        },
    };

    private Row BuildRow(SettingDescriptor descriptor)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = descriptor.Title, Foreground = Res("Text.Primary"), TextWrapping = TextWrapping.Wrap });
        if (descriptor.Description.Length > 0)
            text.Children.Add(new TextBlock
            {
                Text = descriptor.Description,
                Foreground = Res("Text.Dim"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
        if (descriptor.AppliesNote is { } note)
            text.Children.Add(new TextBlock
            {
                Text = note,
                Foreground = Res("Text.Faint"),
                FontSize = 11,
                FontStyle = FontStyle.Italic,
                TextWrapping = TextWrapping.Wrap,
            });

        var (control, push) = BuildControl(descriptor);

        var reset = new Button
        {
            Content = new TextBlock { Text = "Reset", FontSize = 11, Foreground = Res("Text.Dim") },
            Background = Brushes.Transparent,
            Padding = new Thickness(6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            [ToolTip.TipProperty] = "Back to the default",
        };
        reset.Click += (_, _) => { _settings.Reset(descriptor); _status.Text = $"{descriptor.Title} reset to default."; };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(control, 1);
        Grid.SetColumn(reset, 2);
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(16, 0, 0, 0);
        grid.Children.Add(text);
        grid.Children.Add(control);
        grid.Children.Add(reset);

        var border = new Border { Padding = new Thickness(4, 8), Child = grid };
        return new Row(descriptor, border, push, reset);
    }

    /// <summary>Maps a descriptor's kind to a control, plus the action that pushes the stored value into
    /// it. This switch is the only place the window knows value kinds — a new
    /// <see cref="SettingDescriptor"/> subclass adds one arm here and nothing else.</summary>
    private (Control Control, Action Push) BuildControl(SettingDescriptor descriptor)
    {
        switch (descriptor)
        {
            case BoolSetting b:
                {
                    var box = new CheckBox { MinWidth = 0 };
                    box.IsCheckedChanged += (_, _) =>
                    {
                        if (_syncing) return;
                        _settings.Set(b, box.IsChecked == true);
                    };
                    return (box, () => box.IsChecked = b.Get(_settings.Current));
                }

            case IntSetting i:
                {
                    // NumericUpDown commits on Enter / lost focus / spin, not per keystroke — so typing "120"
                    // doesn't write "1" then "12" to disk on the way.
                    var spin = new NumericUpDown
                    {
                        Minimum = i.Min,
                        Maximum = i.Max,
                        Increment = 1,
                        FormatString = "0",
                        Width = 110,
                    };
                    spin.ValueChanged += (_, _) =>
                    {
                        if (_syncing || spin.Value is not { } v) return;
                        _settings.Set(i, (int)v);
                    };
                    var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    panel.Children.Add(spin);
                    if (i.Unit is { } unit)
                        panel.Children.Add(new TextBlock
                        {
                            Text = unit,
                            Foreground = Res("Text.Dim"),
                            FontSize = 11,
                            VerticalAlignment = VerticalAlignment.Center,
                        });
                    return (panel, () => spin.Value = i.Get(_settings.Current));
                }

            case EnumSetting e:
                {
                    var combo = new ComboBox { ItemsSource = e.Options, Width = 210 };
                    combo.ItemTemplate = new FuncDataTemplate<SettingOption>((o, _) =>
                        new TextBlock { Text = o.Title }, supportsRecycling: true);
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (_syncing || combo.SelectedItem is not SettingOption option) return;
                        _settings.Set(e, option.Value);
                    };
                    return (combo, () => combo.SelectedItem = e.Selected(_settings.Current));
                }

            case StringSetting str:
                {
                    // Editable, not a plain dropdown: the suggestion list is a convenience, and a value from
                    // another platform (an IANA id on Windows) has to remain typeable (#77).
                    var combo = new ComboBox
                    {
                        ItemsSource = str.Suggestions?.Invoke() ?? [],
                        Width = 210,
                        IsEditable = true,
                    };
                    var note = new TextBlock { Foreground = Res("Text.Dim"), FontSize = Metric("Font.Small") };

                    void Commit(string? text)
                    {
                        if (_syncing || text is null) return;
                        // Rejected input leaves the stored value alone rather than saving something that will
                        // not resolve; the note says what is actually in force.
                        _settings.Set(str, text);
                        note.Text = str.Describe?.Invoke(str.Get(_settings.Current)) ?? "";
                    }

                    combo.SelectionChanged += (_, _) => Commit(combo.SelectedItem as string);
                    combo.LostFocus += (_, _) => Commit(combo.Text);

                    var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    panel.Children.Add(combo);
                    panel.Children.Add(note);
                    return (panel, () =>
                    {
                        var current = str.Get(_settings.Current);
                        combo.SelectedItem = current;
                        combo.Text = current;
                        note.Text = str.Describe?.Invoke(current) ?? "";
                    }
                    );
                }

            default:
                return (new TextBlock { Text = "(unsupported setting kind)", Foreground = Res("Warn.Amber") }, () => { });
        }
    }

    // ---- value sync --------------------------------------------------------------------------

    private void OnSettingsChanged(AppSettings _) => SyncRows();

    /// <summary>Push current values into every visible control and show the Reset affordance only where
    /// the setting has actually been changed from its default.</summary>
    private void SyncRows()
    {
        _syncing = true;
        try
        {
            foreach (var row in _rows)
            {
                row.Push();
                row.Reset.IsVisible = !row.Descriptor.IsDefault(_settings.Current);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private sealed record Row(SettingDescriptor Descriptor, Control Visual, Action Push, Button Reset);

}
