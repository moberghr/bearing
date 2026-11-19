using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Bearing.App.Services;
using Bearing.App.Theming;
using Bearing.App.ViewModels;
using Bearing.Updates;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Views;

/// <summary>
/// "About Bearing": the mark, the name and what it is, the build version, a Check for Updates that answers
/// in this window, and who makes it — Moberg's copyright with links to Moberg and to Bearing's repository.
/// The version is the assembly's informational version, which MinVer derives from the git tag (see
/// Directory.Build.props). One at a time so repeated menu clicks can't stack windows.
/// <para>
/// The update button is a second door to the same <see cref="UpdateViewModel"/> the menu and the strip use;
/// nothing about checking, downloading or applying is decided here. It exists because the menu's check
/// reports to the status bar, which sits in the window behind this one.
/// </para>
/// </summary>
public sealed class AboutDialog : Window
{
    /// <summary>Moberg's site.</summary>
    internal const string MobergUrl = "https://www.moberg.hr";

    /// <summary>The year is the year Bearing was first published, not the running clock's.</summary>
    internal const string Copyright = "© 2026 Moberg d.o.o.";

    private static AboutDialog? _open;

    private readonly UpdateViewModel? _updates;
    private readonly Func<string?, bool> _openUrl;

    /// <summary>The update button; null when there is no updater to ask (headless runs, tests).</summary>
    internal Button? UpdateButton { get; }

    /// <summary>What the last check said, or how a download is going. Collapsed while there is nothing to say.</summary>
    internal TextBlock UpdateStatus { get; }

    /// <param name="updates">Null where no updater was composed: the button and its line are then absent
    /// rather than present and inert.</param>
    /// <param name="openUrl">How a link leaves the app; the tests substitute a recorder.</param>
    internal AboutDialog(UpdateViewModel? updates, Func<string?, bool>? openUrl = null)
    {
        _updates = updates;
        _openUrl = openUrl ?? BrowserLaunch.Open;

        Title = "About Bearing";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // The same mark the rail shows, so it follows the light/dark scheme with it.
        var mark = new Image { Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Center };
        mark.Bind(Image.SourceProperty, mark.GetResourceObservable(ColorSchemes.RailMarkKey));

        var name = new TextBlock
        {
            Text = "Bearing",
            FontSize = 24,
            FontWeight = FontWeight.SemiBold,
            Foreground = Res("Text.Primary"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var tagline = new TextBlock
        {
            Text = "A fast SQL query tool & script manager.",
            Foreground = Res("Text.Muted"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        // Selectable, and the tooltip carries the commit: this is where a bug report's version is copied from.
        var version = new SelectableTextBlock
        {
            Text = $"Version {Version}",
            Foreground = Res("Text.Dim"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        };
        ToolTip.SetTip(version, $"Build {AppVersion.Full}");

        var body = new StackPanel
        {
            Margin = new Thickness(28, 28, 28, 22),
            Children = { mark, name, tagline, version },
        };

        UpdateStatus = new TextBlock
        {
            Foreground = Res("Text.Muted"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            IsVisible = false,
        };
        if (updates is not null)
        {
            UpdateButton = new Button
            {
                Padding = new Thickness(16, 5),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 18, 0, 0),
            };
            UpdateButton.Click += (_, _) => OnUpdateClick();
            body.Children.Add(UpdateButton);
            body.Children.Add(UpdateStatus);

            updates.PropertyChanged += OnUpdatesChanged;
            Closed += (_, _) => updates.PropertyChanged -= OnUpdatesChanged;
            SyncUpdates(checkStatus: null);
        }

        var links = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 4,
            Margin = new Thickness(0, 18, 0, 0),
            Children =
            {
                Link("moberg.hr", MobergUrl, () => _openUrl(MobergUrl)),
                Link("Bearing on GitHub", UpdateFeed.RepoUrl, () => _openUrl(UpdateFeed.RepoUrl)),
            },
        };
        if (updates is { CanShowNotes: true })
        {
            links.Children.Add(Link("What's New", null, () => updates.WhatsNewCommand.Execute(null)));
        }
        body.Children.Add(links);

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
            BorderBrush = SeparatorBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new DockPanel
            {
                LastChildFill = false,
                Children =
                {
                    new TextBlock
                    {
                        Text = Copyright,
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
            Children = { footer, body },
        };
    }

    /// <summary>The build version, without any "+&lt;git-hash&gt;" build metadata suffix.</summary>
    public static string Version => AppVersion.Display;

    /// <summary>Show the dialog (owned by <paramref name="owner"/> when available); one that is already up
    /// is brought forward instead.</summary>
    public static void Open(Window? owner, UpdateViewModel? updates = null)
    {
        if (_open is { } existing)
        {
            existing.Activate();
            return;
        }
        var dlg = new AboutDialog(updates);
        _open = dlg;
        dlg.Closed += (_, _) => _open = null;
        if (owner is not null && owner.IsVisible) dlg.Show(owner);
        else dlg.Show();
    }

    /// <summary>One button, two jobs: ask for a check, or — once an update is downloaded — take it.</summary>
    private void OnUpdateClick()
    {
        if (_updates is null) return;
        if (_updates.CanRestart) _updates.RestartCommand.Execute(null);
        else _updates.CheckNowCommand.Execute(null);
    }

    private void OnUpdatesChanged(object? sender, PropertyChangedEventArgs e)
        => SyncUpdates(e.PropertyName == nameof(UpdateViewModel.CheckStatus) ? _updates!.CheckStatus : null);

    /// <summary>
    /// The button and the line under it, from the view-model's state. A download or a waiting update outranks
    /// the check's own sentence: it is the newer fact.
    /// </summary>
    /// <param name="checkStatus">A check's sentence that arrived while this window was open. An older one is
    /// never shown — "Bearing is up to date." from this morning is not an answer to opening About now.</param>
    private void SyncUpdates(string? checkStatus)
    {
        if (_updates is null || UpdateButton is null) return;

        UpdateButton.Content = _updates.CanRestart ? "Restart to Update" : "Check for Updates";
        // Downloading, or closing to install: nothing for a click to do until that settles.
        UpdateButton.IsEnabled = !_updates.IsVisible || _updates.CanRestart;

        var text = _updates.IsVisible ? _updates.Message : checkStatus ?? UpdateStatus.Text;
        UpdateStatus.Text = text;
        UpdateStatus.IsVisible = !string.IsNullOrEmpty(text);
    }

    private static Button Link(string text, string? url, Action open)
    {
        var link = new Button
        {
            Content = text,
            Padding = new Thickness(8, 3),
            Foreground = LinkBrush,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = url,
        };
        if (url is not null) ToolTip.SetTip(link, url);
        link.Click += (_, _) => open();
        return link;
    }
}
