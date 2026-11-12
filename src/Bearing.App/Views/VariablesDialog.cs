using System.Linq;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bearing.App.Views;

/// <summary>
/// The values a run is about to bind (#103), one row per value. A row a <c>@set</c> line gives is prefilled and
/// marked with where it came from; editing it overrides that line for this run only. The other rows are seeded
/// with what was typed last time on this tab. Returns every row's text via <c>ShowDialog</c>, or null when
/// cancelled. Code-built, matching <see cref="ConfirmBackendActionDialog"/>.
/// <para>
/// Run is the default button, so Enter in any box runs — checking the values and pressing Enter is the whole
/// point. Esc and a title-bar dismissal cancel, and nothing runs.
/// </para>
/// </summary>
public sealed class VariablesDialog : Window
{
    private readonly List<TextBox> _boxes = new();
    private Button? _run;

    /// <summary>Run only once every row has a value. A blank box is a value not chosen yet, never empty text —
    /// which on a numeric column would only fail on the server, and on a text one would quietly match nothing.</summary>
    private void SyncRun()
    {
        if (_run is not null) _run.IsEnabled = _boxes.All(b => Bearing.Sql.ScriptDirectives.IsGiven(b.Text));
    }

    public VariablesDialog(IReadOnlyList<Services.VariableField> fields)
    {
        Title = fields.Count == 1 ? "Variable" : "Variables";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var layout = new StackPanel { Margin = new Thickness(18) };

        layout.Children.Add(new TextBlock
        {
            Text = "Sent as parameters, never pasted into the SQL. A number is sent as a number, anything "
                 + "else as text; 'quote' a value to force text. Every row needs a value: '' for empty text, "
                 + "null for NULL. Values from @set lines are filled in — change one to override it for this run.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
        });

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, 12, 0, 0),
        };
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var label = new TextBlock
            {
                Text = ":" + field.Name,
                FontFamily = Controls.Tokens.MonoFont,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 12, 4),
            };
            Grid.SetRow(label, i);
            grid.Children.Add(label);

            var box = new TextBox
            {
                Text = field.Initial,
                PlaceholderText = "'' = empty text · null = NULL",
                FontFamily = Controls.Tokens.MonoFont,
                Margin = new Thickness(0, 4),
            };
            box.TextChanged += (_, _) => SyncRun();
            Grid.SetRow(box, i);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            _boxes.Add(box);

            if (field.Note is { } note)
            {
                var mark = new TextBlock
                {
                    Text = note,
                    Foreground = Controls.Tokens.Res("Text.Dim"),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 4, 0, 4),
                };
                ToolTip.SetTip(mark, "Set by a @set line in the script — edit the box to use another value for this run.");
                Grid.SetRow(mark, i);
                Grid.SetColumn(mark, 2);
                grid.Children.Add(mark);
            }
        }
        layout.Children.Add(new ScrollViewer { MaxHeight = 360, Content = grid });
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        var run = new Button { Content = "Run", IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        run.Click += (_, _) => Accept();
        _run = run;
        SyncRun();

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
            // The first value still to fill in, else the first row: a preset the user only wants to check.
            var first = _boxes.FirstOrDefault(b => string.IsNullOrEmpty(b.Text)) ?? _boxes[0];
            first.SelectAll();
            first.Focus();
        };
    }

    private void Accept()
    {
        if (_run is { IsEnabled: false }) return;
        Close(_boxes.Select(b => b.Text ?? "").ToList());
    }
}
