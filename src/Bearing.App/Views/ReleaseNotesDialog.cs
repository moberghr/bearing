using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Bearing.App.Formatting;
using Bearing.App.Services;
using Bearing.Core.Updates;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Views;

/// <summary>
/// "What's New": the published release history, newest first, rendered from the notes GitHub carries.
/// Opened from Help, from the update strip, and once by itself on the first launch after an update.
/// <para>
/// A window rather than a panel because it is read once and dismissed — it owns no app state, nothing binds
/// to it, and it must be able to appear over a workspace mid-query without displacing anything. One at a
/// time, as <see cref="AboutDialog"/> and <see cref="ErrorDialog"/> are: repeated menu clicks re-focus the
/// window that is already up instead of stacking copies of the same text.
/// </para>
/// </summary>
public sealed class ReleaseNotesDialog : Window
{
    private static ReleaseNotesDialog? _open;

    /// <summary>The card for each version, so a re-open can scroll to a different one without rebuilding.</summary>
    private readonly Dictionary<string, Control> _cards = new(StringComparer.OrdinalIgnoreCase);

    private ReleaseNotesDialog(IReadOnlyList<ReleaseNote> notes, string? focusVersion)
    {
        Title = "What's New — Bearing";
        Width = 660;
        Height = 620;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var body = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        for (var i = 0; i < notes.Count; i++)
        {
            var card = BuildCard(notes[i], isFirst: i == 0);
            _cards[notes[i].Version] = card;
            body.Children.Add(card);
        }

        var scroller = new ScrollViewer
        {
            Content = body,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        // Own column rather than an overlay. This is the dialog shown once after every update, and the notes
        // are wrapped prose across the full width — a bar over the last characters of each line is the worst
        // place to have one.
        ScrollViewer.SetAllowAutoHide(scroller, false);

        var close = new Button
        {
            Content = "Close",
            IsDefault = true,
            IsCancel = true,
            Padding = new Thickness(14, 4),
            [DockPanel.DockProperty] = Dock.Right,
        };
        close.Click += (_, _) => Close();

        var footer = new Border
        {
            [DockPanel.DockProperty] = Dock.Bottom,
            Padding = new Thickness(22, 10),
            Background = Res("Bg.Chrome"),
            BorderBrush = Res("Border"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new DockPanel
            {
                LastChildFill = false,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"You're running {AppVersion.Label}.",
                        Foreground = Res("Text.Dim"),
                        VerticalAlignment = VerticalAlignment.Center,
                        [DockPanel.DockProperty] = Dock.Left,
                    },
                    close,
                },
            },
        };

        Content = new DockPanel
        {
            LastChildFill = true,
            Background = Res("Bg.Window"),
            Children = { footer, scroller },
        };

        // Scrolling has to wait for a layout pass — BringIntoView on a control with no bounds yet does
        // nothing at all, which is how a "focus this version" request silently becomes "open at the top".
        if (focusVersion is not null)
            Opened += (_, _) => Dispatcher.UIThread.Post(() => ScrollTo(focusVersion), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Show the notes, scrolled to <paramref name="focusVersion"/> when one is named. A dialog that is
    /// already up is re-focused and re-scrolled rather than replaced: the content is identical, and closing
    /// a window the user is reading to put an identical one in its place is pure flicker.
    /// </summary>
    public static void Open(Window? owner, IReadOnlyList<ReleaseNote> notes, string? focusVersion = null)
    {
        if (_open is { } existing)
        {
            existing.Activate();
            if (focusVersion is not null) existing.ScrollTo(focusVersion);
            return;
        }

        var dlg = new ReleaseNotesDialog(notes, focusVersion);
        _open = dlg;
        dlg.Closed += (_, _) => _open = null;
        if (owner is not null && owner.IsVisible) dlg.Show(owner);
        else dlg.Show();
    }

    private void ScrollTo(string version)
    {
        if (_cards.TryGetValue(version, out var card)) card.BringIntoView();
    }

    /// <summary>
    /// One release: a header carrying the version, its date and a way out to the GitHub page, then the notes.
    /// The link out matters — the parser renders issue refs and links as text it cannot make clickable
    /// (see <see cref="MarkdownSpan"/>), so this button is how a reader actually reaches them.
    /// </summary>
    private static Control BuildCard(ReleaseNote note, bool isFirst)
    {
        var header = new DockPanel { LastChildFill = false };

        header.Children.Add(new TextBlock
        {
            Text = note.Title,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            [DockPanel.DockProperty] = Dock.Left,
        });

        if (note.Published is { } published)
        {
            header.Children.Add(new TextBlock
            {
                Text = published.LocalDateTime.ToString("dd.MM.yyyy"),
                Foreground = Res("Text.Faint"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                [DockPanel.DockProperty] = Dock.Left,
            });
        }

        if (note.Url is { Length: > 0 } url)
        {
            var link = new Button
            {
                Content = "Open on GitHub",
                Padding = new Thickness(8, 2),
                Foreground = LinkBrush,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                [DockPanel.DockProperty] = Dock.Right,
            };
            link.Click += (_, _) => BrowserLaunch.Open(url);
            header.Children.Add(link);
        }

        var content = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var blocks = MarkdownBlocks.Parse(note.Markdown);
        if (blocks.Count == 0)
        {
            // Published with an empty body. Say that, rather than leaving a version heading over blank space
            // that reads like a rendering failure.
            content.Children.Add(new TextBlock
            {
                Text = "No notes were published for this release.",
                Foreground = Res("Text.Faint"),
                FontStyle = FontStyle.Italic,
            });
        }
        else
        {
            foreach (var block in blocks) content.Children.Add(Render(block));
        }

        var card = new StackPanel
        {
            // The first card carries no top rule; every later one is separated from the release above it.
            Margin = new Thickness(0, isFirst ? 0 : 22, 0, 0),
            Children = { header, content },
        };

        if (isFirst) return card;

        return new StackPanel
        {
            Children =
            {
                new Border
                {
                    Height = 1,
                    Background = SeparatorBrush,
                    Margin = new Thickness(0, 20, 0, 0),
                },
                card,
            },
        };
    }

    /// <summary>Turn one parsed block into the control that draws it, using the app's tokens throughout.</summary>
    private static Control Render(MarkdownBlock block) => block.Kind switch
    {
        MarkdownBlockKind.Rule => new Border
        {
            Height = 1,
            Background = SeparatorBrush,
            Margin = new Thickness(0, 10, 0, 10),
        },

        MarkdownBlockKind.Heading => Text(block, new Thickness(0, 14, 0, 4), size: HeadingSize(block.Level),
            weight: FontWeight.SemiBold),

        MarkdownBlockKind.Code => new Border
        {
            Background = Res("Bg.Chrome"),
            BorderBrush = Res("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 4, 0, 8),
            // Its own scroller: the page's ScrollViewer disables horizontal scrolling (the prose must never
            // scroll sideways), which would otherwise clip a long line — a connection string, a vpk command —
            // at the right edge with no way to reveal it. Wrapping the code instead would break the alignment
            // that is the reason it is a code block.
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new SelectableTextBlock
                {
                    Text = block.Text,
                    FontFamily = MonoFont,
                    FontSize = 12.5,
                    Foreground = Res("Text.Code"),
                    TextWrapping = TextWrapping.NoWrap,
                },
            },
        },

        MarkdownBlockKind.Bullet => new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(block.Level * 14, 0, 0, 3),
            Children =
            {
                new TextBlock
                {
                    Text = block.Marker,
                    Width = 20,
                    Foreground = Res("Text.Dim"),
                    [DockPanel.DockProperty] = Dock.Left,
                },
                Text(block, default),
            },
        },

        _ => Text(block, new Thickness(0, 0, 0, 8)),
    };

    /// <summary>
    /// A wrapping text block built from the block's spans. Selectable, deliberately — a release note names
    /// settings, keys and issue numbers people want to copy, and a plain TextBlock silently refuses.
    /// </summary>
    private static Control Text(
        MarkdownBlock block,
        Thickness margin,
        double size = 13.5,
        FontWeight weight = FontWeight.Normal)
    {
        var text = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = margin,
            FontSize = size,
            FontWeight = weight,
            Foreground = Res("Text.Primary"),
        };

        var inlines = new InlineCollection();
        foreach (var span in block.Spans) inlines.Add(Inline(span, size));
        text.Inlines = inlines;
        return text;
    }

    private static Run Inline(MarkdownSpan span, double size)
    {
        var run = new Run(span.Text);
        if (span.Bold) run.FontWeight = FontWeight.SemiBold;
        if (span.Code)
        {
            run.FontFamily = MonoFont;
            run.FontSize = size - 1;
            run.Foreground = Res("Text.Code");
        }

        // Coloured, not underlined: see MarkdownSpan — nothing here is clickable, and the card's
        // "Open on GitHub" button is the honest route to a page where these resolve.
        if (span.Link) run.Foreground = LinkBrush;
        return run;
    }

    private static double HeadingSize(int level) => level switch
    {
        1 => 16.5,
        2 => 15,
        _ => 14,
    };
}
