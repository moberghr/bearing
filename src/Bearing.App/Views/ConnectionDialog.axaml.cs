using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Bearing.App.Connections;
using Bearing.App.Services;
using Bearing.Core.Data;

namespace Bearing.App.Views;

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

    /// <summary>The connection being edited, or null for a new one. Held so <see cref="BuildConnection"/>
    /// can carry forward the fields this dialog does not show — the folder it is filed in (#80) and the
    /// provider options (sslmode, search_path). It builds a fresh record from the boxes, so anything not
    /// re-stated here is silently dropped on save.</summary>
    private readonly ConnectionInfo? _existing;

    private readonly Func<ConnectionInfo, string?, CancellationToken, Task<bool>> _test;
    private readonly SecretStoragePosture _storage;

    // Parameterless ctor for the XAML designer/loader.
    public ConnectionDialog() : this(null, null, (_, _, _) => Task.FromResult(false)) { }

    public ConnectionDialog(
        ConnectionInfo? existing,
        string? existingPassword,
        Func<ConnectionInfo, string?, CancellationToken, Task<bool>> test,
        SecretStoragePosture? storage = null)
    {
        InitializeComponent();
        _test = test;
        _existing = existing;
        _id = existing?.Id ?? Guid.NewGuid();
        _storage = storage ?? SecretStoragePosture.Keychain;

        if (existing is not null)
        {
            Title = $"Edit connection — {existing.Name}";
            NameBox.Text = existing.Name;
            HostBox.Text = existing.Host;
            PortBox.Text = existing.Port.ToString();
            DatabaseBox.Text = existing.Database;
            UserBox.Text = existing.User;
            PasswordBox.Text = existingPassword ?? "";
            CredentialKindBox.SelectedIndex = (int)existing.CredentialKind;
            EnvBox.Text = existing.Environment ?? "";
            EnvColorBox.Text = existing.EnvironmentColor ?? "";
            ConfirmWritesBox.IsChecked = existing.RequireWriteConfirmation;
            // Resolve rather than read the field: a project written before the field existed, or a DBeaver
            // import, still carries the mode in the options bag (#23).
            InitTls(TlsPolicy.Resolve(existing));
            DeleteButton.IsVisible = true;
        }
        else
        {
            Title = "New connection";
            PortBox.Text = "5432";
            HostBox.Text = "localhost";
            // With nowhere to keep a password, a new connection starts on "prompt each time" rather than on
            // a stored password it would silently fail to store.
            CredentialKindBox.SelectedIndex = _storage.CanStore ? 0 : 1;
            InitTls(TlsPolicy.DefaultFor(HostBox.Text));
        }
        UpdateCredentialVisibility();
    }

    private CredentialKind SelectedCredentialKind() => CredentialKindBox.SelectedIndex switch
    {
        1 => CredentialKind.Prompt,
        2 => CredentialKind.EntraToken,
        _ => CredentialKind.StoredPassword,
    };

    private void OnCredentialKindChanged(object? sender, SelectionChangedEventArgs e) => UpdateCredentialVisibility();

    /// <summary>Only the stored-password kind shows the password box + the no-keychain warning; prompt and
    /// Entra never persist a secret (nothing to store), and Entra shows the az hint instead. With no reachable
    /// keychain a password can't be saved at all — the warning says so and, where the probe's reason allows,
    /// what to do about it.</summary>
    private void UpdateCredentialVisibility()
    {
        var kind = SelectedCredentialKind();
        var stored = kind == CredentialKind.StoredPassword;
        PasswordLabel.IsVisible = stored;
        PasswordBox.IsVisible = stored;
        NoStorageWarning.IsVisible = stored && !_storage.CanStore;
        if (NoStorageWarning.IsVisible)
            NoStorageWarningText.Text = SecretStorageAdvice.NoStorageWarning(_storage.Reason);
        EntraHint.IsVisible = kind == CredentialKind.EntraToken;
    }

    /// <summary>
    /// Fill the encryption picker, strongest first, and show what the current choice leaves open.
    /// <para>
    /// Strongest first on purpose: listed in the enum's own order the safe choice sits at the bottom under the
    /// familiar default, and the default is the one that silently may or may not encrypt.
    /// </para>
    /// </summary>
    private void InitTls(TlsMode mode)
    {
        TlsBox.ItemsSource = TlsPolicy.Choices.Select(TlsPolicy.Label).ToList();
        SelectTls(mode);
    }

    private TlsMode SelectedTls()
        => TlsBox.SelectedIndex >= 0 && TlsBox.SelectedIndex < TlsPolicy.Choices.Count
            ? TlsPolicy.Choices[TlsBox.SelectedIndex]
            : TlsPolicy.Default;

    /// <summary>
    /// Whether the user has chosen an encryption mode themselves. Until they have, a new connection's mode
    /// follows the host as it is typed — the default is only defensible if it is computed from the host that
    /// ends up in the record, not from the "localhost" the box is pre-filled with.
    /// </summary>
    private bool _tlsChosen;

    /// <summary>Set while the picker is being moved by code, so that does not count as a choice.</summary>
    private bool _settingTls;

    private void OnTlsChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_settingTls) _tlsChosen = true;
        UpdateTlsWarning();
    }

    /// <summary>A new connection's default follows the host until the user picks a mode (#23).</summary>
    private void OnHostChanged(object? sender, TextChangedEventArgs e)
    {
        if (_existing is not null || _tlsChosen) return;
        SelectTls(TlsPolicy.DefaultFor(HostBox.Text));
    }

    private void SelectTls(TlsMode mode)
    {
        _settingTls = true;
        try { TlsBox.SelectedIndex = Math.Max(0, TlsPolicy.Choices.ToList().IndexOf(mode)); }
        finally { _settingTls = false; }
        UpdateTlsWarning();
    }

    private void UpdateTlsWarning()
    {
        var mode = SelectedTls();
        TlsWarning.IsVisible = TlsPolicy.NeedsWarning(mode);
        TlsWarningText.Text = TlsPolicy.Advice(mode);
    }

    /// <summary>
    /// The options bag, minus the <c>sslmode</c> the typed field now owns (#23). Stripped rather than left
    /// alongside: two sources of truth for a security setting is how one of them ends up ignored, and the bag
    /// is the one that travels in a shared project file.
    /// </summary>
    private static Dictionary<string, string> CarriedOptions(ConnectionInfo? existing)
        => (existing?.Options ?? new Dictionary<string, string>())
            .Where(kv => !string.Equals(kv.Key, TlsPolicy.LegacyOptionKey, StringComparison.OrdinalIgnoreCase))
            // Case-insensitively, as the bag is read: the documented `entra.*` keys are looked up that way.
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>The preset buttons' own labels — the set this dialog considers its own to overwrite.</summary>
    private static readonly string[] PresetLabels = ["local", "staging", "production"];

    private void OnPresetColorClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hex } b) return;

        EnvColorBox.Text = hex;
        var label = b.Content?.ToString()?.ToLowerInvariant();
        // Re-label when the box is empty or still holds another preset's label — clicking Local and then
        // Production has to end up saying "production". A hand-typed label ("staging-eu") is the user's, and
        // survives.
        var current = EnvBox.Text?.Trim() ?? "";
        if (current.Length == 0 ||
            Array.Exists(PresetLabels, l => string.Equals(l, current, StringComparison.OrdinalIgnoreCase)))
        {
            EnvBox.Text = label;
        }
        // Production defaults to guarded; the user can still uncheck it. Deliberately not unset when moving
        // back to a lesser preset — dropping a write guard is the user's call, not a side effect of a click.
        if (label == "production") ConfirmWritesBox.IsChecked = true;
    }

    // The hex box is what gets saved; the picker is a second way to fill it. Each pushes into the other, so
    // the guard is what stops a round trip from re-entering and overwriting what the user is typing.
    private bool _syncingColor;

    private void OnEnvColorTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncingColor) return;
        if (Color.TryParse(EnvColorBox.Text, out var color)) SyncPickerColor(color);
    }

    private void OnEnvColorPicked(object? sender, ColorChangedEventArgs e)
    {
        if (_syncingColor) return;
        _syncingColor = true;
        // #RRGGBB: alpha is off on the picker, and an environment colour is a hue, not a translucency.
        try { EnvColorBox.Text = $"#{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}"; }
        finally { _syncingColor = false; }
    }

    private void SyncPickerColor(Color color)
    {
        _syncingColor = true;
        try { EnvColorPicker.Color = color; }
        finally { _syncingColor = false; }
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
        RequireWriteConfirmation = ConfirmWritesBox.IsChecked == true,
        CredentialKind = SelectedCredentialKind(),
        Tls = SelectedTls(),
        // Not editable here, so carried rather than rebuilt: filing lives in the tree, and Options is
        // file-edit only. Omitting either would quietly discard it on every save.
        Folder = _existing?.Folder,
        Options = CarriedOptions(_existing),
    };

    /// <summary>The password to persist: the typed value for a stored-password connection, or empty for
    /// prompt / Entra (nothing is stored — an empty value deletes any existing secret on save).</summary>
    private string SecretToStore()
        => SelectedCredentialKind() == CredentialKind.StoredPassword ? (PasswordBox.Text ?? "") : "";

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
            // Redacted, not raw: this dialog holds a user-typed, not-yet-saved password, and a connect-time
            // driver failure can quote the whole connection string back at us. Same helper as the executor
            // and connect paths — this one was missed.
            TestResult.Text = "✗ " + SafeErrorText.Of(ex);
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
        => Close(new ConnectionDialogResult(BuildConnection(), SecretToStore(), Delete: false));

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
        => Close(new ConnectionDialogResult(BuildConnection(), SecretToStore(), Delete: true));

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
