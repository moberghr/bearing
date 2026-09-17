using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Bearing.App.Results;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Views;

/// <summary>
/// Picks what an audit export covers (#113): a date range, which connections and environments, and whether
/// to keep only the statements the write guard flags.
/// <para>
/// A dialog rather than a menu of presets because the question this answers is asked with specific bounds —
/// "what ran against production last week" — and a report that covered a different period than the one asked
/// for would be worse than no report. Returns null on cancel via <c>ShowDialog&lt;AuditExportRequest?&gt;</c>.
/// </para>
/// </summary>
public sealed class AuditExportDialog : Window
{
    private readonly DatePicker _from = new() { SelectedDate = DateTimeOffset.Now.AddDays(-7).Date };
    private readonly DatePicker _to = new() { SelectedDate = DateTimeOffset.Now.Date };
    private readonly CheckBox _wholeLog = new() { Content = "Everything in the log (ignore the dates)" };
    private readonly CheckBox _writesOnly = new() { Content = "Only statements the write guard flags" };
    private readonly ComboBox _format = new()
    {
        ItemsSource = new[] { ResultExport.Label(ExportFormat.Csv), ResultExport.Label(ExportFormat.Xlsx) },
        SelectedIndex = 0,
    };
    private readonly List<CheckBox> _connections = [];
    private readonly List<CheckBox> _environments = [];

    /// <param name="connectionNames">Connection names to offer, as the log records them.</param>
    /// <param name="environments">Environment labels to offer.</param>
    public AuditExportDialog(IReadOnlyList<string> connectionNames, IReadOnlyList<string> environments)
    {
        Title = "Export query history — Bearing";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var layout = new StackPanel { Margin = new Thickness(18), Spacing = 10 };

        layout.Children.Add(new TextBlock
        {
            Text = "What ran against which connection, when",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = Res("Text.Primary"),
        });
        layout.Children.Add(new TextBlock
        {
            Text = "The report carries a note saying which period it covers and whether the statements in it "
                 + "were recorded with their literals redacted.",
            Foreground = Res("Text.Muted"),
            FontSize = Metric("Font.Small"),
            TextWrapping = TextWrapping.Wrap,
        });

        layout.Children.Add(Section("Period", Dates()));
        if (connectionNames.Count > 0)
            layout.Children.Add(Section("Connections", Choices(connectionNames, _connections)));
        if (environments.Count > 0)
            layout.Children.Add(Section("Environments", Choices(environments, _environments)));
        layout.Children.Add(_writesOnly);
        layout.Children.Add(Section("Format", _format));
        layout.Children.Add(Buttons());

        Content = layout;
    }

    /// <summary>
    /// The two date pickers, on one line where they fit and on two where they do not.
    /// <para>
    /// A <see cref="WrapPanel"/> rather than a horizontal <c>StackPanel</c>, which clipped the second picker
    /// off the right edge of the window: a <c>DatePicker</c> is three fields wide and its month field is as
    /// wide as the longest month name in the user's culture, so the pair fits in 520px in some locales and
    /// not in others. Wrapping decides per locale instead of betting on one.
    /// </para>
    /// </summary>
    private Control Dates()
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        // Each label travels with its own picker, so a wrap moves "to <picker>" down as a pair rather than
        // leaving a lone "to" dangling at the end of the first line.
        row.Children.Add(Bound("from", _from));
        row.Children.Add(Bound("to", _to));

        // Both pickers stay enabled but stop being read: greying them out hides what the report *would*
        // cover if the box were cleared again, which is the thing being decided here.
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(row);
        panel.Children.Add(_wholeLog);
        return panel;

        static Control Bound(string label, DatePicker picker) => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            // Right margin for the gap between the pair, vertical for the gutter when the row wraps.
            Margin = new Thickness(0, 2, 8, 2),
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Res("Text.Dim"),
                },
                picker,
            },
        };
    }

    /// <summary>A checkbox per value. None ticked means every value — stated on screen, because an empty
    /// selection reading as "all" is the opposite of what an unticked list usually means.</summary>
    private static Control Choices(IReadOnlyList<string> values, List<CheckBox> into)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = "Tick none to include all.",
            Foreground = Res("Text.Faint"),
            FontSize = Metric("Font.Small"),
        });
        var list = new StackPanel { Spacing = 2 };
        foreach (var value in values)
        {
            var box = new CheckBox { Content = value, Tag = value };
            into.Add(box);
            list.Children.Add(box);
        }
        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 132 });
        return panel;
    }

    private static Control Section(string title, Control body)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(),
            FontSize = Metric("Font.Small"),
            FontWeight = FontWeight.Bold,
            Foreground = Res("Text.Dim"),
        });
        panel.Children.Add(body);
        return panel;
    }

    private Control Buttons()
    {
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(null);

        var export = new Button { Content = "Export…", IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        export.Click += (_, _) => Close(Build());

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { cancel, export },
        };
    }

    /// <summary>
    /// The request as the controls describe it. The day-to-instant conversion is
    /// <see cref="ReportPeriod"/>'s, not this window's — the subtle half (an end bound that includes the
    /// whole of its day) belongs where it can be tested without a window (§2.5).
    /// </summary>
    private AuditExportRequest Build()
    {
        var whole = _wholeLog.IsChecked == true;
        // Swapped if inverted: "from the 8th to the 1st" means the week, and a range that matches nothing
        // would be reported as "nothing ran" — the false negative this dialog exists to avoid.
        var (from, to) = whole
            ? (null, null)
            : ReportPeriod.Ordered(_from.SelectedDate, _to.SelectedDate);
        return new AuditExportRequest(
            new QueryLogReportFilter(
                whole ? null : ReportPeriod.StartOfDay(from),
                whole ? null : ReportPeriod.EndOfDay(to),
                Ticked(_connections),
                Ticked(_environments),
                _writesOnly.IsChecked == true),
            _format.SelectedIndex == 1 ? ExportFormat.Xlsx : ExportFormat.Csv);
    }

    private static IReadOnlyList<string>? Ticked(List<CheckBox> boxes)
    {
        var chosen = boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag!).ToList();
        return chosen.Count == 0 ? null : chosen;   // none ticked = no filter
    }
}
