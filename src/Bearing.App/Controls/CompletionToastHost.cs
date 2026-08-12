using System;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Bearing.App.ViewModels;

namespace Bearing.App.Controls;

/// <summary>
/// Shows a toast when a query finishes on a tab the user wasn't watching. Wraps Avalonia's
/// <see cref="WindowNotificationManager"/> so the window keeps no notification plumbing of its own (§9.1),
/// and so the one decision this makes — how a completion reads — lives in one place.
/// <para>
/// Construct only once the window is on screen: the manager attaches to the top level's overlay layer.
/// The app has exactly one notification sink, and this is it.
/// </para>
/// </summary>
internal sealed class CompletionToastHost
{
    private readonly WindowNotificationManager _manager;
    private readonly Action<BackgroundCompletion> _activate;

    /// <param name="activate">Invoked when the user clicks a toast whose tab still exists — brings that
    /// tab back on screen (switching project if it lives in another one).</param>
    public CompletionToastHost(TopLevel host, Action<BackgroundCompletion> activate)
    {
        _activate = activate;
        _manager = new WindowNotificationManager(host)
        {
            Position = NotificationPosition.BottomRight,
            // Toasts never expire on their own now, so several can be waiting at once — a batch of tabs
            // finishing together must not push the earliest one off before it has been read.
            MaxItems = 5,
        };
        // The manager collects notifications into a throwaway list until its template is applied, and then
        // swaps that list for the real PART_Items panel — so anything shown before the first layout pass is
        // silently dropped. Constructing and showing in one dispatcher turn (which is what happens when the
        // host is built on the first completion) hit exactly that, losing the first toast of the session.
        // Applying the template here makes the very first Show land in the panel.
        _manager.ApplyTemplate();
    }

    /// <summary>Post a completion. Must be called on the UI thread.</summary>
    public void Show(BackgroundCompletion completion)
    {
        // A run whose tab is gone left nothing behind to go back to, so it says so — the toast is the only
        // trace of it, and there is nowhere to click through to. (Switching projects no longer closes tabs,
        // so this now means a genuine tab close.) A run on a still-open tab just points at the tab.
        var message = completion.TabStillOpen
            ? completion.Message
            : $"{completion.Message} — the tab was closed, so the results were discarded.";

        var clickable = completion.TabStillOpen && completion.Tab is not null;

        _manager.Show(new Notification(
            completion.TabName,
            clickable ? $"{message}\nClick to open the tab." : message,
            NotificationType.Information,
            // TimeSpan.Zero = stays until the user dismisses it. A background run's result is the whole
            // point of the notification; timing out means the user can miss it while looking elsewhere.
            expiration: TimeSpan.Zero,
            onClick: clickable ? () => _activate(completion) : null));
    }

    /// <summary>Post an arbitrary notification (a finished export, and anything else that has a follow-up
    /// action a status line can't carry). Must be called on the UI thread.</summary>
    /// <param name="clickHint">Appended on its own line when <paramref name="onClick"/> is set — a toast that
    /// does something on click has to say so, or nobody clicks it.</param>
    /// <param name="onClick">Invoked if the user clicks it; null makes the toast inert.</param>
    /// <param name="expiration">Null keeps it until dismissed, as completions do.</param>
    public void Show(
        string title, string message, string? clickHint = null, Action? onClick = null, TimeSpan? expiration = null)
        => _manager.Show(new Notification(
            title,
            onClick is not null && clickHint is not null ? $"{message}\n{clickHint}" : message,
            NotificationType.Information,
            expiration: expiration ?? TimeSpan.Zero,
            onClick: onClick));
}
