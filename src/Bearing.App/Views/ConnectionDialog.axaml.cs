using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Bearing.App.Connections;
using Bearing.App.Services;
using Bearing.Core.Data;
using Bearing.Data;

namespace Bearing.App.Views;

/// <summary>Result of the connection editor: the edited connection + password, or a delete request.</summary>
public sealed record ConnectionDialogResult(ConnectionInfo Connection, string Password, bool Delete);

/// <summary>
/// Add/edit a named connection. Returns a <see cref="ConnectionDialogResult"/> via
/// <c>ShowDialog&lt;ConnectionDialogResult?&gt;</c> (null = cancelled). "Test" builds a throwaway
/// connection through the supplied delegate without persisting anything.
/// <para>
/// <b>Engine-driven, and deliberately thin.</b> The engine picker is the registry's providers; the
/// Host/Port/Database/User/options rows are built from the selected provider's
/// <see cref="IDbProvider.ConnectionFields"/>; the Credential dropdown's entries come from
/// <see cref="CredentialKindOptions"/>. All of the deciding — which fields exist, what they default to,
/// what a provider switch keeps, what is invalid, and how any of it maps to
/// <see cref="ConnectionInfo"/> and its <see cref="ConnectionInfo.Options"/> — lives in
/// <see cref="ConnectionFieldModel"/> and <see cref="CredentialKindOptions"/>, where it is tested as pure
/// logic rather than through a control tree (§0.5, §2.3, §2.5). What is left in this file is labels, boxes
/// and visibility — and that the wiring between the two really is attached is what
/// <c>Ui/ConnectionEditorTests</c> holds.
/// </para>
/// </summary>
public partial class ConnectionDialog : Window
{
    private readonly Guid _id;

    /// <summary>The connection being edited, or null for a new one. Held so <see cref="BuildConnection"/>
    /// can carry forward the fields this dialog does not show — the folder it is filed in (#80). It builds
    /// a fresh record, so anything not re-stated is silently dropped on save.</summary>
    private readonly ConnectionInfo? _existing;

    private readonly Func<ConnectionInfo, string?, CancellationToken, Task<bool>> _test;
    private readonly SecretStoragePosture _storage;
    private readonly List<IDbProvider> _providers;

    /// <summary>The form's state for the currently selected engine. Replaced (not mutated) on a provider
    /// switch — <see cref="ConnectionFieldModel.SwitchTo"/> is what carries typed values across.</summary>
    private ConnectionFieldModel _model;

    private IReadOnlyList<CredentialKindOption> _credentialKinds = Array.Empty<CredentialKindOption>();

    /// <summary>Set while the code is writing the combo boxes, so the handlers those writes raise don't
    /// re-enter the rebuild they are part of.</summary>
    private bool _loading;

    // Parameterless ctor for the XAML designer/loader.
    public ConnectionDialog() : this(null, null, (_, _, _) => Task.FromResult(false)) { }

    public ConnectionDialog(
        ConnectionInfo? existing,
        string? existingPassword,
        Func<ConnectionInfo, string?, CancellationToken, Task<bool>> test,
        SecretStoragePosture? storage = null,
        IProviderRegistry? providers = null)
    {
        InitializeComponent();
        _test = test;
        _existing = existing;
        _id = existing?.Id ?? Guid.NewGuid();
        _storage = storage ?? SecretStoragePosture.Keychain;
        // The designer and the no-argument path get the default registry; every real caller passes the
        // app's own, so the picker lists exactly the engines this build composed.
        _providers = (providers ?? new ProviderRegistry()).All.ToList();

        // An edited connection selects its own engine; a new one starts on the first registered provider
        // (PostgreSQL — see ProviderRegistry, whose order is this dropdown's order).
        var provider = existing is not null ? Resolve(existing.ProviderId) : _providers[0];
        _model = ConnectionFieldModel.For(provider, existing);

        _loading = true;
        foreach (var p in _providers) ProviderBox.Items.Add(new ComboBoxItem { Content = p.DisplayName });
        ProviderBox.SelectedIndex = _providers.IndexOf(provider);
        _loading = false;

        if (existing is not null)
        {
            Title = $"Edit connection — {existing.Name}";
            NameBox.Text = existing.Name;
            PasswordBox.Text = existingPassword ?? "";
            EnvBox.Text = existing.Environment ?? "";
            EnvColorBox.Text = existing.EnvironmentColor ?? "";
            ConfirmWritesBox.IsChecked = existing.RequireWriteConfirmation;
            DeleteButton.IsVisible = true;
        }
        else
        {
            Title = "New connection";
        }

        RenderFields();
        // Resolve rather than read the field: a project written before the field existed, or a DBeaver
        // import, still carries the mode in the options bag (#23). A new connection's default is computed
        // from the host the provider declared, not from a hardcoded "localhost".
        InitTls(existing is not null ? TlsPolicy.Resolve(existing) : TlsPolicy.DefaultFor(HostValue()));
        // With nowhere to keep a password, a new connection starts on "prompt each time" rather than on a
        // stored password it would silently fail to store. An edited one keeps whatever it was saved as.
        RebuildCredentialKinds(existing?.CredentialKind
            ?? (_storage.CanStore ? CredentialKind.StoredPassword : CredentialKind.Prompt));
    }

    /// <summary>The provider for a persisted id, or the first registered one when this build no longer ships
    /// it. Falling back rather than throwing: a project file naming an engine this build dropped must still
    /// open in the editor so the user can repoint or delete the connection.</summary>
    private IDbProvider Resolve(string providerId)
        => _providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase))
           ?? _providers[0];

    // ---- Engine picker ---------------------------------------------------------------------------

    private void OnProviderChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProviderBox.SelectedIndex < 0 || ProviderBox.SelectedIndex >= _providers.Count) return;
        var next = _providers[ProviderBox.SelectedIndex];
        if (string.Equals(next.Id, _model.ProviderId, StringComparison.OrdinalIgnoreCase)) return;

        // Values first, then the boxes: SwitchTo decides what survives the change (a typed host stays, a
        // port still at the old engine's default becomes the new engine's).
        _model = _model.SwitchTo(next);
        RenderFields();
        RebuildCredentialKinds(SelectedCredentialKind());
    }

    // ---- Provider-declared fields ----------------------------------------------------------------

    /// <summary>Build one label + editor row per field the model exposes, replacing whatever was there.
    /// Pure visual work — every value read or written goes through the model.</summary>
    private void RenderFields()
    {
        FieldsHost.Children.Clear();

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*") };
        for (var i = 0; i < _model.Fields.Count; i++)
        {
            var field = _model.Fields[i];
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var editor = Editor(field);
            Grid.SetRow(editor, i);

            // A checkbox carries its own label as Content and spans both columns, the way this dialog's
            // hand-written ConfirmWritesBox does. It must not sit in the 90px label column: a label like
            // "Trust server certificate" is far wider than that, so a separate label was clipped mid-word
            // with the box overlapping it.
            if (field.Kind == ConnectionFieldKind.Boolean)
            {
                Grid.SetColumn(editor, 0);
                Grid.SetColumnSpan(editor, 2);
                grid.Children.Add(editor);
                continue;
            }

            var label = new TextBlock
            {
                Text = field.Label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0),
                // A label longer than the column wraps onto a second line rather than losing its tail;
                // the row is Auto-height, so it grows to fit.
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetRow(label, i);
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            Grid.SetColumn(editor, 1);
            grid.Children.Add(editor);
        }
        FieldsHost.Children.Add(grid);

        RefreshValidation();
        EndpointHint.Text = _model.EndpointHint ?? "";
        EndpointHint.IsVisible = _model.EndpointHint is not null;
        // The password row's visibility is UpdateCredentialVisibility's alone (it depends on the credential
        // kind as well as on the provider), and every path here ends up calling it.
    }

    /// <summary>The control for one field: a Boolean is a checkbox, a
    /// <see cref="ConnectionFieldKind.Choice"/> with candidates is a dropdown, everything else — a
    /// candidate-less Choice included — is a text box, with the declared default as its placeholder.
    /// Which values the dropdown lists is <see cref="ConnectionFieldState.Candidates"/>' decision, not this
    /// method's (§2.3). Passwords never reach here (the model excludes them; the dialog's own box owns the
    /// secret).</summary>
    private Control Editor(ConnectionFieldState field)
    {
        if (field.Kind == ConnectionFieldKind.Choice && field.Candidates is { Count: > 0 } candidates)
        {
            var combo = new ComboBox
            {
                // Same {Key}Box naming as the boxes below, for the same reason: it is what makes a
                // code-built row findable from a headless UI test (§4.5).
                Name = field.Key + "Box",
                ItemsSource = candidates,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 6, 0, 0),
            };
            // Candidates always contains the current value when there is one (it prepends it if the provider
            // did not declare it), so this only fails to find a match when the field is genuinely unset —
            // which leaves the box empty rather than silently adopting the first option as the user's choice.
            combo.SelectedIndex = IndexOf(candidates, field.Value);
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedIndex < 0 || combo.SelectedIndex >= candidates.Count) return;
                field.Value = candidates[combo.SelectedIndex];
                RefreshValidation();
            };
            return combo;
        }

        if (field.Kind == ConnectionFieldKind.Boolean)
        {
            var check = new CheckBox
            {
                Name = field.Key + "Box",
                // The label is the checkbox's Content, not a separate cell — see RenderFields.
                Content = field.Label,
                IsChecked = string.Equals(field.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase),
                Margin = new Thickness(0, 6, 0, 0),
            };
            check.IsCheckedChanged += (_, _) =>
            {
                field.Value = check.IsChecked == true ? "true" : "false";
                RefreshValidation();
            };
            return check;
        }

        var box = new TextBox
        {
            // Named as the hand-written rows were ("Host" -> HostBox), so a headless UI test can still find
            // this box by name (§4.5) and so the code-built rows are no less discoverable than the XAML
            // ones they replaced.
            Name = field.Key + "Box",
            Text = field.Value,
            Margin = new Thickness(0, 6, 0, 0),
            PlaceholderText = field.Field.Default ?? "",
        };
        var isHost = string.Equals(field.Key, "Host", StringComparison.OrdinalIgnoreCase);
        box.TextChanged += (_, _) =>
        {
            field.Value = box.Text ?? "";
            RefreshValidation();
            // A new connection's encryption default follows the host as it is typed (#23). The host is a
            // provider-declared field now, so this hangs off its editor rather than off a named HostBox.
            if (isHost && _existing is null && !_tlsChosen) SelectTls(TlsPolicy.DefaultFor(field.Value));
        };
        return box;
    }

    /// <summary>Where <paramref name="value"/> sits in <paramref name="candidates"/>, or -1. Trimmed and
    /// case-insensitive, matching how <see cref="ConnectionFieldState.Candidates"/> decides a value is
    /// already among the declared ones — the two must agree, or a value it judged present would select
    /// nothing here.</summary>
    private static int IndexOf(IReadOnlyList<string> candidates, string value)
    {
        var wanted = value.Trim();
        if (wanted.Length == 0) return -1;
        for (var i = 0; i < candidates.Count; i++)
            if (string.Equals(candidates[i], wanted, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>The host as the model currently holds it — what the encryption default is computed from.</summary>
    private string HostValue()
        => _model.Fields
            .FirstOrDefault(f => string.Equals(f.Key, "Host", StringComparison.OrdinalIgnoreCase))
            ?.Value ?? "";

    /// <summary>
    /// Show, live, which of the selected provider's required fields are still empty (and which numbers
    /// don't parse). Advisory only — <see cref="OnSaveClick"/> does not consult it.
    /// <para>
    /// It is not a gate because this dialog is the only way to edit a connection that already exists, and a
    /// connection saved before these fields were validated at all may be missing one. Blocking Save would
    /// make those uneditable — you could not even correct the field being complained about without first
    /// filling in every other one. So it warns while you type and clears as you fill it in.
    /// </para>
    /// </summary>
    private void RefreshValidation()
    {
        var problems = _model.Validate(SelectedCredentialKind());
        ValidationHint.Text = problems.Count > 0 ? "⚠ " + string.Join(" ", problems) : "";
        ValidationHint.IsVisible = problems.Count > 0;
    }

    // ---- Credential kind -------------------------------------------------------------------------

    /// <summary>Refill the Credential dropdown for the selected engine, keeping <paramref name="preferred"/>
    /// when this engine offers it. It may not: switching away from SQL Server with Windows authentication
    /// selected has to land somewhere, and the stored password is the kind every engine has.</summary>
    private void RebuildCredentialKinds(CredentialKind preferred)
    {
        _credentialKinds = CredentialKindOptions.For(_providers[Math.Max(0, ProviderBox.SelectedIndex)]);
        _loading = true;
        try
        {
            CredentialKindBox.Items.Clear();
            foreach (var option in _credentialKinds)
                CredentialKindBox.Items.Add(new ComboBoxItem { Content = option.Label });
            var index = _credentialKinds.ToList().FindIndex(o => o.Kind == preferred);
            CredentialKindBox.SelectedIndex = index >= 0 ? index : 0;
        }
        finally { _loading = false; }
        UpdateCredentialVisibility();
    }

    private CredentialKind SelectedCredentialKind()
        => CredentialKindBox.SelectedIndex >= 0 && CredentialKindBox.SelectedIndex < _credentialKinds.Count
            ? _credentialKinds[CredentialKindBox.SelectedIndex].Kind
            : CredentialKind.StoredPassword;

    private void OnCredentialKindChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_loading) UpdateCredentialVisibility();
    }

    /// <summary>Only the stored-password kind shows the password box + the no-keychain warning; prompt, Entra
    /// and integrated authentication never persist a secret, and integrated authentication reads no secret at
    /// all. Entra and integrated each show their own hint instead. With no reachable keychain a password
    /// can't be saved at all: the warning says so and, where the probe's reason allows, what to do about it.</summary>
    private void UpdateCredentialVisibility()
    {
        var kind = SelectedCredentialKind();
        var stored = CredentialKindOptions.KeepsAStoredPassword(kind) && _model.HasPasswordField;
        PasswordLabel.IsVisible = stored;
        PasswordBox.IsVisible = stored;
        NoStorageWarning.IsVisible = stored && !_storage.CanStore;
        if (NoStorageWarning.IsVisible)
            NoStorageWarningText.Text = SecretStorageAdvice.NoStorageWarning(_storage.Reason);
        EntraHint.IsVisible = kind == CredentialKind.EntraToken;
        IntegratedHint.IsVisible = kind == CredentialKind.Integrated;
        // The credential kind changes what counts as missing — integrated auth needs no user name — so the
        // advisory hint is recomputed here too, not only when a field is typed into.
        RefreshValidation();
    }

    // ---- Transport security (#23) ----------------------------------------------------------------

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

    // ---- Environment -----------------------------------------------------------------------------

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

    // ---- Result ----------------------------------------------------------------------------------

    /// <summary>
    /// The edited record. The engine-declared half — provider id, host, port, database, user and the driver
    /// options — is the model's (<see cref="ConnectionFieldModel.Apply"/>, which also carries forward the
    /// options this dialog does not show). Everything else is this dialog's own boxes, or is carried from
    /// the record being edited.
    /// </summary>
    private ConnectionInfo BuildConnection() => _model.Apply(new ConnectionInfo
    {
        Id = _id,
        Name = string.IsNullOrWhiteSpace(NameBox.Text) ? BuildFallbackName() : NameBox.Text!.Trim(),
        // Restated because the record requires it; the model sets the real value.
        ProviderId = _model.ProviderId,
        Environment = string.IsNullOrWhiteSpace(EnvBox.Text) ? null : EnvBox.Text!.Trim(),
        EnvironmentColor = string.IsNullOrWhiteSpace(EnvColorBox.Text) ? null : EnvColorBox.Text!.Trim(),
        RequireWriteConfirmation = ConfirmWritesBox.IsChecked == true,
        CredentialKind = SelectedCredentialKind(),
        Tls = SelectedTls(),
        // Not editable here, so carried rather than rebuilt: filing lives in the tree. Omitting it would
        // quietly discard it on every save.
        Folder = _existing?.Folder,
    });

    /// <summary>The password to persist: the typed value for a stored-password connection, or empty for
    /// prompt / Entra / integrated (nothing is stored — an empty value deletes any existing secret on
    /// save).</summary>
    private string SecretToStore()
        => SelectedCredentialKind() == CredentialKind.StoredPassword ? (PasswordBox.Text ?? "") : "";

    private string BuildFallbackName()
    {
        var host = HostValue().Trim();
        var db = (_model.Fields
            .FirstOrDefault(f => string.Equals(f.Key, "Database", StringComparison.OrdinalIgnoreCase))
            ?.Value ?? "").Trim();
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
