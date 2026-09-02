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
/// <see cref="ConnectionFieldModel"/>, because none of it can be tested from here (§0.5, §2.3, §2.5, §4.3).
/// What is left in this file is labels, boxes and visibility.
/// </para>
/// </summary>
public partial class ConnectionDialog : Window
{
    private readonly Guid _id;
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

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("90,*"),
        };
        for (var i = 0; i < _model.Fields.Count; i++)
        {
            var field = _model.Fields[i];
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var editor = Editor(field);
            Grid.SetRow(editor, i);

            // A checkbox carries its own label as Content and spans both columns, the way this dialog's
            // hand-written ConfirmWritesBox does. It must not sit in the 90px label column: "Encrypt
            // connection" and "Trust server certificate" are both far wider than that, so a separate label
            // was clipped mid-word with the box overlapping it.
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

    /// <summary>The control for one field. A Boolean is a checkbox; everything else is a text box.
    /// <see cref="ConnectionFieldKind.Choice"/> included: <see cref="ConnectionField"/> carries no candidate
    /// list, so there is nothing to populate a dropdown with — the declared default becomes the placeholder
    /// instead of a fake set of options. Passwords never reach here (the model excludes them; the dialog's
    /// own box owns the secret).</summary>
    private Control Editor(ConnectionFieldState field)
    {
        if (field.Kind == ConnectionFieldKind.Boolean)
        {
            var check = new CheckBox
            {
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
            Text = field.Value,
            Margin = new Thickness(0, 6, 0, 0),
            PlaceholderText = field.Field.Default ?? "",
        };
        box.TextChanged += (_, _) =>
        {
            field.Value = box.Text ?? "";
            RefreshValidation();
        };
        return box;
    }

    // ---- Credential kind -------------------------------------------------------------------------

    /// <summary>Refill the Credential dropdown for the selected engine, keeping <paramref name="preferred"/>
    /// when this engine offers it. It may not: switching away from SQL Server with Windows authentication
    /// selected has to land somewhere, and the stored password is the kind every engine has.</summary>
    private void RebuildCredentialKinds(CredentialKind preferred)
    {
        _credentialKinds = CredentialKindOptions.For(_model.Provider);
        var index = _credentialKinds.ToList().FindIndex(o => o.Kind == preferred);

        _loading = true;
        CredentialKindBox.Items.Clear();
        foreach (var option in _credentialKinds)
            CredentialKindBox.Items.Add(new ComboBoxItem { Content = option.Label });
        CredentialKindBox.SelectedIndex = index >= 0 ? index : 0;
        _loading = false;

        UpdateCredentialVisibility();
    }

    private CredentialKind SelectedCredentialKind()
    {
        var i = CredentialKindBox.SelectedIndex;
        return i >= 0 && i < _credentialKinds.Count ? _credentialKinds[i].Kind : CredentialKind.StoredPassword;
    }

    private void OnCredentialKindChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        UpdateCredentialVisibility();
    }

    /// <summary>Only the stored-password kind shows the password box + the no-keychain warning; prompt,
    /// Entra and integrated authentication never persist a secret (nothing to store), so warning any of
    /// them about an unreachable keychain would be warning about something they do not touch — integrated
    /// authentication reads no secret at all. Entra and integrated each show their own hint instead. With no
    /// reachable keychain a password can't be saved at all: the warning says so and, where the probe's
    /// reason allows, what to do about it.</summary>
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

    // ---- Environment -----------------------------------------------------------------------------

    private void OnPresetColorClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex } b)
        {
            EnvColorBox.Text = hex;
            var label = b.Content?.ToString()?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(EnvBox.Text)) EnvBox.Text = label;
            // Production defaults to guarded; the user can still uncheck it.
            if (label == "production") ConfirmWritesBox.IsChecked = true;
        }
    }

    // ---- Save / test / delete --------------------------------------------------------------------

    private ConnectionInfo BuildConnection() => _model.Apply(new ConnectionInfo
    {
        Id = _id,
        Name = string.IsNullOrWhiteSpace(NameBox.Text) ? BuildFallbackName() : NameBox.Text!.Trim(),
        ProviderId = _model.ProviderId,
        Environment = string.IsNullOrWhiteSpace(EnvBox.Text) ? null : EnvBox.Text!.Trim(),
        EnvironmentColor = string.IsNullOrWhiteSpace(EnvColorBox.Text) ? null : EnvColorBox.Text!.Trim(),
        RequireWriteConfirmation = ConfirmWritesBox.IsChecked == true,
        CredentialKind = SelectedCredentialKind(),
    });

    /// <summary>The password to persist: the typed value for a stored-password connection, or empty for
    /// prompt / Entra / integrated (nothing is stored — an empty value deletes any existing secret on
    /// save).</summary>
    private string SecretToStore()
        => CredentialKindOptions.KeepsAStoredPassword(SelectedCredentialKind()) ? (PasswordBox.Text ?? "") : "";

    private string BuildFallbackName()
    {
        var db = (_model.Get("Database") ?? "").Trim();
        var host = (_model.Get("Host") ?? "").Trim();
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

    private void OnSaveClick(object? sender, RoutedEventArgs e)
        => Close(new ConnectionDialogResult(BuildConnection(), SecretToStore(), Delete: false));

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
        => Close(new ConnectionDialogResult(BuildConnection(), SecretToStore(), Delete: true));

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
