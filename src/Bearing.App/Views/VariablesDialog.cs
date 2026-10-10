using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bearing.App.Views;

/// <summary>
/// Asks for the value of every <c>:name</c> variable in a script about to run (DBeaver style), one box
/// per name, seeded with what was typed last time on this tab. Returns the values via
/// <c>ShowDialog</c>, or null when cancelled. Code-built, matching <see cref="ConfirmBackendActionDialog"/>.
/// <para>
/// Run is the default button, so Enter in any box runs — filling in a value and pressing Enter is the
/// whole point. Esc and a title-bar dismissal cancel, and nothing runs.
/// </para>
/// </summary>
public sealed class VariablesDialog : Window
{
    private readonly List<(string Name, TextBox Box)> _boxes = new();

    public VariablesDialog(IReadOnlyList<string> names, IReadOnlyDictionary<string, string> remembered)
    {
        Title = names.Count == 1 ? "Variable" : "Variables";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var layout = new StackPanel { Margin = new Thickness(18) };

        layout.Children.Add(new TextBlock
        {
            Text = "Values are inserted as typed — quote text yourself ('abc'), or write :'name' in the "
                 + "script to have it quoted.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
        });

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(0, 12, 0, 0),
        };
        for (var i = 0; i < names.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var label = new TextBlock
            {
                Text = ":" + names[i],
                FontFamily = Controls.Tokens.MonoFont,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 12, 4),
            };
            Grid.SetRow(label, i);
            grid.Children.Add(label);

            var box = new TextBox
            {
                Text = remembered.TryGetValue(names[i], out var last) ? last : "",
                FontFamily = Controls.Tokens.MonoFont,
                Margin = new Thickness(0, 4),
            };
            Grid.SetRow(box, i);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            _boxes.Add((names[i], box));
        }
        layout.Children.Add(new ScrollViewer { MaxHeight = 360, Content = grid });

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        var run = new Button { Content = "Run", IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        run.Click += (_, _) => Accept();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(run);
        layout.Children.Add(buttons);

        Content = layout;

        // Enter is claimed here rather than left to IsDefault so it runs from inside a TextBox too.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Accept(); }
        };
        Opened += (_, _) =>
        {
            if (_boxes.Count == 0) return;
            _boxes[0].Box.SelectAll();
            _boxes[0].Box.Focus();
        };
    }

    private void Accept()
    {
        var values = new Dictionary<string, string>();
        foreach (var (name, box) in _boxes) values[name] = box.Text ?? "";
        Close(values);
    }
}
