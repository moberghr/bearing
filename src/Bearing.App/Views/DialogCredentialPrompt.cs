using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Bearing.App.Connections;
using Bearing.Core.Data;

namespace Bearing.App.Views;

/// <summary>
/// UI implementation of <see cref="ICredentialPrompt"/>: a small modal that asks for a connection's
/// password at connect time (for <see cref="CredentialKind.Prompt"/> connections), parented to the main
/// window. Marshals to the UI thread — the connect path runs in the background. Returns null when the user
/// cancels or when there is no window (headless / tests), which the resolver treats as "cancelled".
/// </summary>
public sealed class DialogCredentialPrompt : ICredentialPrompt
{
    private static Window? Owner =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public Task<string?> RequestPasswordAsync(ConnectionInfo info, string? message, CancellationToken ct)
        => Dispatcher.UIThread.InvokeAsync(() => ShowAsync(info, message));

    private static Task<string?> ShowAsync(ConnectionInfo info, string? message)
    {
        if (Owner is not { } owner) return Task.FromResult<string?>(null);

        var prompt = new TextBlock
        {
            Text = message ?? $"Password for {ConnectionEndpoint.Full(info)}  ·  {info.Name}",
            TextWrapping = TextWrapping.Wrap,
        };
        var password = new TextBox { PasswordChar = '•', PlaceholderText = "Password", Margin = new Thickness(0, 8, 0, 0) };

        var ok = new Button { Content = "Connect", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(prompt);
        root.Children.Add(password);
        root.Children.Add(buttons);

        var win = new Window
        {
            Title = "Enter password",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root,
        };

        ok.Click += (_, _) => win.Close(password.Text ?? "");
        cancel.Click += (_, _) => win.Close(null);
        password.KeyDown += (_, e) => { if (e.Key == Key.Enter) win.Close(password.Text ?? ""); };
        win.Opened += (_, _) => password.Focus();

        return win.ShowDialog<string?>(owner);
    }
}
