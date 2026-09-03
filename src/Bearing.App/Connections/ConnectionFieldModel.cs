using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bearing.Core.Data;

namespace Bearing.App.Connections;

/// <summary>One field of the connection editor: what the provider declared, and what is currently in the
/// box. Mutable in <see cref="Value"/> only — the dialog writes the typed text straight back here, so the
/// model is the single copy of the form's state rather than a snapshot of it.</summary>
public sealed class ConnectionFieldState
{
    internal ConnectionFieldState(ConnectionField field, string value)
    {
        Field = field;
        Value = value;
    }

    public ConnectionField Field { get; }

    public string Key => Field.Key;
    public string Label => Field.Label;
    public ConnectionFieldKind Kind => Field.Kind;
    public bool Required => Field.Required;

    /// <summary>The current text. Empty means "not set" for every kind; a Boolean field holds
    /// <c>"true"</c>/<c>"false"</c>.</summary>
    public string Value { get; set; }

    /// <summary>True when the box still holds the provider's own default, i.e. nothing the user chose.
    /// This is what makes a provider switch able to replace <c>5432</c> with <c>1433</c> while keeping a
    /// port the user actually typed.</summary>
    public bool IsDefault
        => string.Equals(Value.Trim(), (Field.Default ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The values a <see cref="ConnectionFieldKind.Choice"/> dropdown should list: the provider's own
    /// <see cref="ConnectionField.Choices"/>, plus <see cref="Value"/> when it is set and is not one of
    /// them. Empty for every other kind, and for a Choice field the provider gave no candidates — the
    /// dialog reads emptiness as "render a text box".
    /// <para>
    /// The extra entry is why this is a decision rather than a pass-through of the declared list, and it is
    /// the same rule <c>_carried</c> follows for undeclared option keys: a value that arrived on the edited
    /// connection — hand-written into project.json, set by an older build, or imported from another tool —
    /// must survive being looked at. A dropdown that simply cannot represent it would blank it on the next
    /// save, and silently.
    /// </para>
    /// <para>
    /// Computed on read, not cached: <see cref="Value"/> is mutable, so a snapshot taken at construction
    /// would offer the wrong extra entry after a provider switch carried a value in.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Candidates
    {
        get
        {
            if (Kind != ConnectionFieldKind.Choice || Field.Choices is not { Count: > 0 } declared)
                return Array.Empty<string>();

            var current = Value.Trim();
            if (current.Length == 0
                || declared.Any(c => string.Equals(c, current, StringComparison.OrdinalIgnoreCase)))
                return declared;

            // Ahead of the declared values: it is the one the connection actually holds, so it is what the
            // box has to show, and appending it would put it out of sight in a long list.
            var withCurrent = new List<string>(declared.Count + 1) { current };
            withCurrent.AddRange(declared);
            return withCurrent;
        }
    }
}

/// <summary>
/// The connection editor's field model: which fields an engine has, what they default to, what is wrong
/// with what is in them, and how they map to and from <see cref="ConnectionInfo"/> (including
/// <see cref="ConnectionInfo.Options"/>).
/// <para>
/// It lives here rather than in <c>Views/ConnectionDialog.axaml.cs</c> because all of it is decisions, and
/// a decision belongs in a pure helper where it can be tested by itself (§0.5, §2.3, §2.5). Not because
/// the dialog is untestable — since #62 it can be realized headlessly and is
/// (<c>Ui/ConnectionEditorTests</c>, <c>Ui/ChoiceFieldTests</c>) — but a UI test that had to spell out
/// every field, default, carry-over and validation rule through a control tree would be slower, serialized
/// and worse at saying which rule broke (§4.3/§4.5). The code-behind's remaining job is to build a row per
/// <see cref="Fields"/> entry and copy text in and out of <see cref="ConnectionFieldState.Value"/>; the UI
/// tests assert that it does, and this file's tests assert what it copies.
/// </para>
/// <para>
/// <b>Four of the keys are not options.</b> <c>Host</c>, <c>Port</c>, <c>Database</c> and <c>User</c> are
/// columns on <see cref="ConnectionInfo"/> itself; every other field a provider declares round-trips
/// through <c>Options</c>, which is exactly what the driver-specific keys (<c>sslmode</c>, <c>Encrypt</c>,
/// <c>TrustServerCertificate</c>) need. <c>Password</c> is declared by both providers and handled by
/// neither path: it belongs to the secret store (§1.1), so it is excluded here and the dialog keeps its own
/// password box.
/// </para>
/// </summary>
public sealed class ConnectionFieldModel
{
    /// <summary>Keys that map to a <see cref="ConnectionInfo"/> property rather than to
    /// <see cref="ConnectionInfo.Options"/>. Matched case-insensitively: the key is a provider's own
    /// spelling, and two engines need not agree on its case.</summary>
    private static readonly HashSet<string> Intrinsic =
        new(StringComparer.OrdinalIgnoreCase) { "Host", "Port", "Database", "User" };

    /// <summary>The password is the secret store's, never <c>ConnectionInfo</c>'s and never
    /// <c>Options</c>'s — a provider declaring the field must not cause one to be written anywhere
    /// (§1.1).</summary>
    private const string PasswordKey = "Password";

    private ConnectionFieldModel(IDbProvider provider, List<ConnectionFieldState> fields)
    {
        Provider = provider;
        Fields = fields;
    }

    /// <summary>The engine these fields belong to.</summary>
    public IDbProvider Provider { get; }

    public string ProviderId => Provider.Id;

    /// <summary>Whether this engine can authenticate as the OS identity, i.e. whether the dialog offers
    /// <see cref="CredentialKind.Integrated"/> at all. Asked through here so the dialog never has to know
    /// which engines have it.</summary>
    public bool SupportsIntegratedAuth => Provider.SupportsIntegratedAuth;

    /// <summary>Extra guidance about this engine's endpoint, or null when there is nothing to say. The
    /// named-instance rule is the case that exists: it makes the Port box a no-op, which the user cannot
    /// otherwise know.</summary>
    public string? EndpointHint => ProviderTraits.For(ProviderId).EndpointHint;

    /// <summary>The fields to render, in the order the provider declared them, minus the password.</summary>
    public IReadOnlyList<ConnectionFieldState> Fields { get; }

    /// <summary>True when this engine has a password field at all — the dialog's own password box is only
    /// meaningful then.</summary>
    public bool HasPasswordField
        => Provider.ConnectionFields.Any(f => string.Equals(f.Key, PasswordKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A fresh model for <paramref name="provider"/>, filled from <paramref name="existing"/> when there is
    /// one and from the provider's defaults when there is not. An existing connection keeps <b>every</b>
    /// persisted value, including an option key this provider does not declare — those are carried in
    /// <see cref="_carried"/> and written back untouched, so editing a connection in this dialog can never
    /// be the thing that silently drops a hand-added option.
    /// </summary>
    public static ConnectionFieldModel For(IDbProvider provider, ConnectionInfo? existing = null)
    {
        var fields = new List<ConnectionFieldState>();
        foreach (var field in provider.ConnectionFields)
        {
            if (string.Equals(field.Key, PasswordKey, StringComparison.OrdinalIgnoreCase)) continue;
            fields.Add(new ConnectionFieldState(field, ValueFor(field, existing)));
        }

        var model = new ConnectionFieldModel(provider, fields);
        if (existing is not null) model.Carry(existing);
        return model;
    }

    /// <summary>Option keys that came in on the edited connection and that this provider does not declare.
    /// Preserved verbatim through <see cref="Apply"/>.</summary>
    private readonly Dictionary<string, string> _carried = new(StringComparer.Ordinal);

    private void Carry(ConnectionInfo existing)
    {
        foreach (var (key, value) in existing.Options)
            if (!Fields.Any(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase)))
                _carried[key] = value;
    }

    private static string ValueFor(ConnectionField field, ConnectionInfo? existing)
    {
        if (existing is null) return field.Default ?? "";

        if (string.Equals(field.Key, "Host", StringComparison.OrdinalIgnoreCase)) return existing.Host;
        if (string.Equals(field.Key, "Port", StringComparison.OrdinalIgnoreCase))
            return existing.Port.ToString(CultureInfo.InvariantCulture);
        if (string.Equals(field.Key, "Database", StringComparison.OrdinalIgnoreCase)) return existing.Database;
        if (string.Equals(field.Key, "User", StringComparison.OrdinalIgnoreCase)) return existing.User;

        // Everything else is an option. A key the connection doesn't carry falls back to the declared
        // default, which is also what Apply then declines to write back — see there.
        return existing.Options.TryGetValue(field.Key, out var option) ? option : field.Default ?? "";
    }

    /// <summary>Read a field's current value, or null when this provider has no such field.</summary>
    public string? Get(string key)
        => Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>Set a field's value; ignored when this provider has no such field.</summary>
    public void Set(string key, string value)
    {
        if (Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase)) is { } field)
            field.Value = value;
    }

    /// <summary>
    /// The same form, re-shaped for a different engine. Per field of the <em>new</em> provider:
    /// <list type="bullet">
    ///   <item>a value the user typed (one that differs from the old provider's default for that key) is
    ///     carried over — retyping the host and database because the engine changed is exactly the wiping
    ///     this rule exists to prevent;</item>
    ///   <item>a value still sitting at the old provider's default is replaced by the new one's. This is
    ///     what turns 5432 into 1433, and it is the only rule that can: the port is a field both engines
    ///     have, so "keep everything the new provider also has" would keep Postgres' port and quietly send
    ///     the user at a closed one.</item>
    /// </list>
    /// A field the new provider does not declare is dropped from the form, but if it arrived as an option on
    /// the edited connection it is still carried (see <see cref="_carried"/>) — so switching engine by
    /// accident and switching back loses nothing.
    /// </summary>
    public ConnectionFieldModel SwitchTo(IDbProvider provider)
    {
        var next = For(provider);

        // Everything the old form knew that the user chose: the non-default fields it showed, plus the
        // options it was already carrying for engines other than its own. Case-insensitive because the key
        // is each provider's own spelling and two engines need not agree on its case.
        var chosen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in _carried) chosen[key] = value;
        foreach (var f in Fields) if (!f.IsDefault) chosen[f.Key] = f.Value;

        // A key the new engine shows becomes its field's value — this is also what makes the round trip
        // work: switch away and back, and the sslmode you set is in the sslmode box again, not lost.
        foreach (var field in next.Fields)
            if (chosen.Remove(field.Key, out var carried)) field.Value = carried;

        // Whatever is left belongs to neither engine's form; it rides along untouched.
        foreach (var (key, value) in chosen) next._carried[key] = value;
        return next;
    }

    /// <summary>
    /// What is wrong with the form, one message per problem, empty when it is good to save. Two rules:
    /// a required field may not be blank, and a Number field must hold a number.
    /// <para>
    /// <paramref name="credentialKind"/> is taken because it decides who the login is:
    /// <see cref="CredentialKind.Integrated"/> authenticates as the OS identity, so the User box is not
    /// merely optional but meaningless, and demanding it would make Windows authentication impossible to
    /// save.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Validate(CredentialKind credentialKind)
    {
        var problems = new List<string>();
        foreach (var field in Fields)
        {
            var value = field.Value.Trim();

            if (field.Required && value.Length == 0)
            {
                if (credentialKind == CredentialKind.Integrated
                    && string.Equals(field.Key, "User", StringComparison.OrdinalIgnoreCase))
                    continue;
                problems.Add($"{field.Label} is required.");
                continue;
            }

            if (field.Kind == ConnectionFieldKind.Number && value.Length > 0
                && !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                problems.Add($"{field.Label} must be a whole number.");
        }
        return problems;
    }

    /// <summary>The port to persist: the typed number, or this provider's declared default when the box is
    /// empty or unparseable. The default has to come from the provider — falling back to a hardcoded 5432
    /// is how a SQL Server connection ends up pointed at a Postgres port.</summary>
    public int Port
    {
        get
        {
            // Named `port`, not `field`: in C# 14 `field` is a contextual keyword inside a property
            // accessor and binds to a synthesized backing field instead of the local.
            var port = Fields.FirstOrDefault(f => string.Equals(f.Key, "Port", StringComparison.OrdinalIgnoreCase));
            if (port is not null
                && int.TryParse(port.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var typed))
                return typed;
            return int.TryParse(port?.Field.Default, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)
                ? d
                : 0;
        }
    }

    /// <summary>
    /// <paramref name="template"/> with this form's engine, endpoint and options written onto it. Everything
    /// the dialog owns itself (name, environment, credential kind, write guard) stays as the caller set it.
    /// <para>
    /// <b>A field still holding its declared default is not written to <c>Options</c>.</b> Both providers'
    /// declared defaults restate their driver's own, so omitting them changes no behaviour — and it keeps
    /// <c>Options</c> empty until the user actually asks for something, which is what §1.4 relies on
    /// (sslmode is set only when the user sets it) and what stops a routine edit from rewriting the
    /// options of every connection in the project.
    /// </para>
    /// </summary>
    public ConnectionInfo Apply(ConnectionInfo template)
    {
        var options = new Dictionary<string, string>(_carried, StringComparer.Ordinal);
        foreach (var field in Fields)
        {
            if (Intrinsic.Contains(field.Key)) continue;
            var value = field.Value.Trim();
            if (value.Length == 0 || field.IsDefault) { options.Remove(field.Key); continue; }
            options[field.Key] = value;
        }

        return template with
        {
            ProviderId = ProviderId,
            Host = (Get("Host") ?? "").Trim(),
            Port = Port,
            Database = (Get("Database") ?? "").Trim(),
            User = (Get("User") ?? "").Trim(),
            Options = options,
        };
    }
}
