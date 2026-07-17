using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Squirrel.Core.Data;

namespace Squirrel.App.Views;

/// <summary>Result of the connection editor: the edited connection + password, or a delete request.</summary>
public sealed record ConnectionDialogResult(ConnectionInfo Connection, string Password, bool Delete);

/// <summary>
/// Add/edit a named connection. Returns a <see cref="ConnectionDialogResult"/> via
/// <c>ShowDialog&lt;ConnectionDialogResult?&gt;</c> (null = cancelled). "Test" builds a throwaway
/// connection through the supplied delegate without persisting anything.
/// </summary>
public partial class ConnectionDialog : Window
{
    private readonly Guid _id;
    private readonly Func<ConnectionInfo, string?, CancellationToken, Task<bool>> _test;

    // Parameterless ctor for the XAML designer/loader.
    public ConnectionDialog() : this(null, null, (_, _, _) => Task.FromResult(false)) { }

    public ConnectionDialog(
        ConnectionInfo? existing,
        string? existingPassword,
        Func<ConnectionInfo, string?, CancellationToken, Task<bool>> test)
    {
        InitializeComponent();
        _test = test;
        _id = existing?.Id ?? Guid.NewGuid();

        if (existing is not null)
        {
            Title = $"Edit connection — {existing.Name}";
            NameBox.Text = existing.Name;
            HostBox.Text = existing.Host;
            PortBox.Text = existing.Port.ToString();
            DatabaseBox.Text = existing.Database;
            UserBox.Text = existing.User;
            PasswordBox.Text = existingPassword ?? "";
            EnvBox.Text = existing.Environment ?? "";
            EnvColorBox.Text = existing.EnvironmentColor ?? "";
            DeleteButton.IsVisible = true;
        }
        else
        {
            Title = "New connection";
            PortBox.Text = "5432";
            HostBox.Text = "localhost";
        }
    }

    private void OnPresetColorClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex } b)
        {
            EnvColorBox.Text = hex;
            if (string.IsNullOrWhiteSpace(EnvBox.Text)) EnvBox.Text = b.Content?.ToString()?.ToLowerInvariant();
        }
    }

    private ConnectionInfo BuildConnection() => new()
    {
        Id = _id,
        Name = string.IsNullOrWhiteSpace(NameBox.Text) ? BuildFallbackName() : NameBox.Text!.Trim(),
        ProviderId = "postgres",
        Host = (HostBox.Text ?? "").Trim(),
        Port = int.TryParse(PortBox.Text, out var p) ? p : 5432,
        Database = (DatabaseBox.Text ?? "").Trim(),
        User = (UserBox.Text ?? "").Trim(),
        Environment = string.IsNullOrWhiteSpace(EnvBox.Text) ? null : EnvBox.Text!.Trim(),
        EnvironmentColor = string.IsNullOrWhiteSpace(EnvColorBox.Text) ? null : EnvColorBox.Text!.Trim(),
    };

    private string BuildFallbackName()
    {
        var db = (DatabaseBox.Text ?? "").Trim();
        var host = (HostBox.Text ?? "").Trim();
        return string.IsNullOrEmpty(db) ? (string.IsNullOrEmpty(host) ? "Connection" : host) : $"{host}/{db}";
    }

    private async void OnTestClick(object? sender, RoutedEventArgs e)
    {
        TestResult.Text = "Testing…";
        try
        {
            var ok = await _test(BuildConnection(), PasswordBox.Text ?? "", CancellationToken.None);
            TestResult.Text = ok ? "✓ Connection succeeded." : "✗ Connection failed.";
        }
        catch (Exception ex)
        {
            TestResult.Text = "✗ " + ex.Message;
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
        => Close(new ConnectionDialogResult(BuildConnection(), PasswordBox.Text ?? "", Delete: false));

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
        => Close(new ConnectionDialogResult(BuildConnection(), PasswordBox.Text ?? "", Delete: true));

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
