using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Squirrel.App.Input;
using KeyBinding = Squirrel.App.Input.KeyBinding; // disambiguate from Avalonia.Input.KeyBinding
using Path = Avalonia.Controls.Shapes.Path;       // vector icons (the app font clips glyphs like ✕)

namespace Squirrel.App.Views;

/// <summary>
/// Settings dialog for keyboard shortcuts. Lists every registered command grouped by scope, shows each
/// command's gestures as removable chips, and captures a new gesture on demand ("press keys…"). Returns
/// the edited <see cref="Keymap"/> on Save (null on Cancel); the caller diffs it against the defaults and
/// persists the result. Built in code (like <c>ResultView</c>) since the row list is dynamic.
/// </summary>
public sealed class KeybindingsWindow : Window
{
    private readonly Keymap _defaults;
    private readonly List<KeyCommand> _commands;
    private readonly List<KeyBinding> _edited;
    private string? _capturing;   // id of the command currently waiting for a keystroke
    private string? _note;        // transient status message (e.g. a reassignment)

    private readonly StackPanel _list = new() { Spacing = 1 };
    private readonly TextBlock _status = new() { Foreground = Brush("Text.Dim"), VerticalAlignment = VerticalAlignment.Center };

    public KeybindingsWindow(Keymap current, Keymap defaults, IEnumerable<KeyCommand> commands)
    {
        _defaults = defaults;
        _commands = commands.OrderBy(c => c.Scope).ThenBy(c => c.Group).ThenBy(c => c.Title).ToList();
        _edited = current.Bindings.ToList();

        Title = "Keyboard Shortcuts";
        Width = 640;
        Height = 660;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("Bg.Window");

        var reset = new Button { Content = "Reset all to defaults" };
        reset.Click += (_, _) => { _edited.Clear(); _edited.AddRange(_defaults.Bindings); _capturing = null; _note = "Reset to defaults."; Refresh(); };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        var save = new Button { Content = "Save", IsDefault = true };
        save.Click += (_, _) => Close(new Keymap(_edited));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 10, 0, 0) };
        Grid.SetColumn(reset, 0);
        Grid.SetColumn(_status, 1);
        _status.Margin = new Thickness(12, 0, 12, 0);
        Grid.SetColumn(buttons, 2);
        footer.Children.Add(reset);
        footer.Children.Add(_status);
        footer.Children.Add(buttons);

        var root = new DockPanel { LastChildFill = true, Margin = new Thickness(12) };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = _list });
        Content = root;

        // Tunnel so a captured Enter/Space is claimed before the default (Save) button acts on it.
        AddHandler(KeyDownEvent, OnCaptureKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Refresh();
    }

    private void Refresh()
    {
        _list.Children.Clear();
        KeyScope? group = null;
        foreach (var cmd in _commands)
        {
            if (group != cmd.Scope) { group = cmd.Scope; _list.Children.Add(ScopeHeader(cmd.Scope)); }
            _list.Children.Add(BuildRow(cmd));
        }
        _status.Text = _capturing is not null ? "Press a shortcut…  (Esc to cancel)" : (_note ?? "");
    }

    private Control ScopeHeader(KeyScope scope) => new TextBlock
    {
        Text = scope.ToString().ToUpperInvariant(),
        Foreground = Brush("Text.Faint"),
        FontSize = 11,
        Margin = new Thickness(2, 12, 0, 4),
    };

    private Control BuildRow(KeyCommand cmd)
    {
        var title = new TextBlock { Text = cmd.Title, Width = 260, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

        var chips = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var b in _edited.Where(b => b.Scope == cmd.Scope && b.CommandId == cmd.Id))
            chips.Children.Add(BuildChip(cmd, b.Gesture));

        var capturing = _capturing == cmd.Id;
        var add = new Button
        {
            Content = capturing ? "press keys…" : "+ Add",
            Padding = new Thickness(8, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        add.Click += (_, _) => { _capturing = capturing ? null : cmd.Id; _note = null; Refresh(); if (_capturing is not null) Focus(); };

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(title, Dock.Left);
        DockPanel.SetDock(add, Dock.Right);
        dock.Children.Add(title);
        dock.Children.Add(add);
        dock.Children.Add(chips);

        return new Border
        {
            Padding = new Thickness(6, 4),
            Background = capturing ? Tint("Accent.Orange", 0x22) : Brushes.Transparent,
            Child = dock,
        };
    }

    private Control BuildChip(KeyCommand cmd, Gesture g)
    {
        var text = new TextBlock { Text = GestureParser.Format(g), VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("Text.Primary") };
        var remove = new Button
        {
            Content = new Path
            {
                Data = Geometry.Parse("M0,0 L7,7 M0,7 L7,0"), // drawn ✕ (glyph clips in the app font)
                Stroke = Brush("Text.Dim"),
                StrokeThickness = 1.4,
                Width = 7,
                Height = 7,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Padding = new Thickness(4, 2),
            Margin = new Thickness(6, 0, 0, 0),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
        };
        remove.Click += (_, _) =>
        {
            _edited.RemoveAll(b => b.Scope == cmd.Scope && b.CommandId == cmd.Id && b.Gesture == g);
            _note = null;
            Refresh();
        };

        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(text);
        sp.Children.Add(remove);
        return new Border
        {
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(6, 1),
            CornerRadius = new CornerRadius(4),
            Background = Brush("Bg.Chrome"),
            BorderBrush = Brush("Border"),
            BorderThickness = new Thickness(1),
            Child = sp,
        };
    }

    private void OnCaptureKey(object? sender, KeyEventArgs e)
    {
        if (_capturing is not { } id) return;
        if (IsModifierKey(e.Key)) return;              // wait for a real (non-modifier) key
        e.Handled = true;
        if (e.Key == Key.Escape) { _capturing = null; Refresh(); return; }
        AddBinding(id, Gesture.ForKey(e.KeyModifiers, e.Key));
        _capturing = null;
        Refresh();
    }

    private void AddBinding(string commandId, Gesture g)
    {
        var cmd = _commands.First(c => c.Id == commandId);
        var displaced = _edited
            .Where(b => b.Scope == cmd.Scope && b.Gesture == g && b.CommandId != commandId)
            .Select(b => CommandTitle(b.CommandId)).Distinct().ToList();
        _edited.RemoveAll(b => b.Scope == cmd.Scope && b.Gesture == g);   // one command per (scope, gesture)
        _edited.Add(new KeyBinding(cmd.Scope, g, commandId));
        _note = displaced.Count > 0 ? $"Reassigned {GestureParser.Format(g)} from {string.Join(", ", displaced)}." : null;
    }

    private string CommandTitle(string id) => _commands.FirstOrDefault(c => c.Id == id)?.Title ?? id;

    private static bool IsModifierKey(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift
        or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System;

    private static IBrush Brush(string key) => (Application.Current?.FindResource(key) as IBrush) ?? Brushes.Transparent;

    private static IBrush Tint(string key, byte alpha)
    {
        var c = (Brush(key) as ISolidColorBrush)?.Color ?? Colors.Transparent;
        return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
    }
}
