using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.App.Connections;
using Bearing.Core.Data;
using Bearing.Data.Postgres;
using Bearing.Data.SqlServer;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The connection editor's field model. This is the whole reason it is not in the dialog's code-behind:
/// which fields exist, what they default to, what survives an engine switch, what counts as invalid, and how
/// any of it maps to <see cref="ConnectionInfo"/> are all decisions, and none of them could be tested from a
/// window this repo cannot drive headlessly (§0.5, §2.5, §4.3).
/// <para>
/// The real providers are used rather than a fake: the field lists are the thing under test, and a fake one
/// would only assert that the model can read a list nobody ships.
/// </para>
/// </summary>
public class ConnectionFieldModelTests
{
    private static readonly PostgresProvider Pg = new();
    private static readonly SqlServerProvider Ms = new();

    /// <summary>
    /// A provider that declares <em>option</em> fields, which neither shipped engine does any more: sslmode
    /// and Encrypt/TrustServerCertificate all became the typed <see cref="ConnectionInfo.Tls"/> (#23). The
    /// model still has to carry option fields to and from <see cref="ConnectionInfo.Options"/>, so that
    /// contract is exercised here rather than against whichever keyword a real engine happens to want
    /// configured this month (§4.1 — hand-rolled, no mocking library).
    /// </summary>
    private sealed class OptionProvider : IDbProvider
    {
        public string Id => "optiontest";
        public string DisplayName => "Option Test";
        public bool SupportsIntegratedAuth => false;
        public bool SupportsEntraToken => false;
        public DbErrorKind Classify(QueryError error) => DbErrorKind.Unknown;
        public DbErrorKind ClassifyException(Exception exception) => DbErrorKind.Unknown;

        public IReadOnlyList<ConnectionField> ConnectionFields { get; } = new[]
        {
            new ConnectionField("Host", "Host", ConnectionFieldKind.Text, Required: true, Default: "localhost"),
            new ConnectionField("Port", "Port", ConnectionFieldKind.Number, Required: true, Default: "9999"),
            new ConnectionField("Database", "Database", ConnectionFieldKind.Text, Required: true),
            new ConnectionField("User", "User", ConnectionFieldKind.Text, Required: true),
            new ConnectionField("Password", "Password", ConnectionFieldKind.Password, Required: false),
            new ConnectionField("compression", "Compression", ConnectionFieldKind.Boolean, Required: false, Default: "true"),
            new ConnectionField("search_path", "Search path", ConnectionFieldKind.Text, Required: false),
        };

        public IDbConnectionFactory CreateConnectionFactory(ConnectionInfo info, string? password)
            => throw new NotSupportedException("declares fields only");
        public IMetadataReader CreateMetadataReader(IDbConnectionFactory factory)
            => throw new NotSupportedException("declares fields only");
        public IQueryExecutor CreateQueryExecutor(IDbConnectionFactory factory)
            => throw new NotSupportedException("declares fields only");
    }

    private static readonly OptionProvider Opt = new();

    /// <summary>
    /// A provider declaring the two shapes of <see cref="ConnectionFieldKind.Choice"/>: one with candidates
    /// and one without.
    /// <para>
    /// Hand-rolled because <b>no shipped provider declares a Choice field</b> — sslmode and
    /// Encrypt/TrustServerCertificate all became the typed <see cref="ConnectionInfo.Tls"/> (#23), so the
    /// kind has no production user. Inventing one on a real engine to justify the capability would be
    /// worse than testing it here: it would put a second source of truth for a setting next to the typed
    /// field that owns it, which is exactly what #23 removed.
    /// </para>
    /// </summary>
    private sealed class ChoiceProvider : IDbProvider
    {
        public string Id => "choicetest";
        public string DisplayName => "Choice Test";
        public bool SupportsIntegratedAuth => false;
        public bool SupportsEntraToken => false;
        public DbErrorKind Classify(QueryError error) => DbErrorKind.Unknown;
        public DbErrorKind ClassifyException(Exception exception) => DbErrorKind.Unknown;

        public IReadOnlyList<ConnectionField> ConnectionFields { get; } = new[]
        {
            new ConnectionField("Host", "Host", ConnectionFieldKind.Text, Required: true, Default: "localhost"),
            new ConnectionField("Port", "Port", ConnectionFieldKind.Number, Required: true, Default: "1234"),
            new ConnectionField("Database", "Database", ConnectionFieldKind.Text, Required: true),
            new ConnectionField("User", "User", ConnectionFieldKind.Text, Required: true),
            new ConnectionField("mode", "Mode", ConnectionFieldKind.Choice, Required: false, Default: "fast",
                Choices: new[] { "fast", "safe", "paranoid" }),
            // The degenerate case a provider can still write, and the reason the dialog keeps its text-box
            // fallback: a Choice with nothing to choose from.
            new ConnectionField("flavour", "Flavour", ConnectionFieldKind.Choice, Required: false),
        };

        public IDbConnectionFactory CreateConnectionFactory(ConnectionInfo info, string? password)
            => throw new NotSupportedException("declares fields only");
        public IMetadataReader CreateMetadataReader(IDbConnectionFactory factory)
            => throw new NotSupportedException("declares fields only");
        public IQueryExecutor CreateQueryExecutor(IDbConnectionFactory factory)
            => throw new NotSupportedException("declares fields only");
    }

    private static readonly ChoiceProvider Choice = new();

    private static ConnectionFieldState Field(ConnectionFieldModel model, string key)
        => model.Fields.Single(f => f.Key == key);

    private static ConnectionInfo Blank => new()
    {
        Id = Guid.NewGuid(),
        Name = "c",
        ProviderId = PostgresProvider.ProviderId,
    };

    // ---- Which fields exist -----------------------------------------------------------------------

    [Fact]
    public void Fields_are_the_providers_own_in_declared_order_minus_the_password()
    {
        // The password is the secret store's, so it must not appear as a mappable field at all (§1.1) —
        // the dialog's own box owns it.
        // Both shipped engines declare only the intrinsic four (plus the password, excluded here):
        // transport security is the typed ConnectionInfo.Tls now, not a declared field (#23).
        Assert.Equal(
            new[] { "Host", "Port", "Database", "User" },
            ConnectionFieldModel.For(Pg).Fields.Select(f => f.Key));
        Assert.Equal(
            new[] { "Host", "Port", "Database", "User" },
            ConnectionFieldModel.For(Ms).Fields.Select(f => f.Key));
        // ...and an engine that does declare options keeps them, in declared order, after the intrinsics.
        Assert.Equal(
            new[] { "Host", "Port", "Database", "User", "compression", "search_path" },
            ConnectionFieldModel.For(Opt).Fields.Select(f => f.Key));
    }

    [Fact]
    public void A_new_connection_starts_on_the_providers_declared_defaults()
    {
        Assert.Equal("5432", ConnectionFieldModel.For(Pg).Get("Port"));
        Assert.Equal("1433", ConnectionFieldModel.For(Ms).Get("Port"));
        Assert.Equal("localhost", ConnectionFieldModel.For(Ms).Get("Host"));
        Assert.Equal("true", ConnectionFieldModel.For(Opt).Get("compression"));
    }

    // ---- Mapping in ------------------------------------------------------------------------------

    [Fact]
    public void Editing_a_connection_keeps_every_persisted_value()
    {
        var existing = Blank with
        {
            ProviderId = SqlServerProvider.ProviderId,
            Host = @"SQLPROD\SALES",
            Port = 1444,
            Database = "sales",
            User = "app",
            Options = new Dictionary<string, string> { ["compression"] = "false", ["entra.resource"] = "https://x/" },
        };

        var model = ConnectionFieldModel.For(Opt, existing);

        Assert.Equal(@"SQLPROD\SALES", model.Get("Host"));
        Assert.Equal("1444", model.Get("Port"));
        Assert.Equal("sales", model.Get("Database"));
        Assert.Equal("app", model.Get("User"));
        Assert.Equal("false", model.Get("compression"));
        // A key no provider declares is not shown, but it is not lost either.
        Assert.Null(model.Get("entra.resource"));
        Assert.Equal("https://x/", model.Apply(existing).Options["entra.resource"]);
    }

    // ---- Mapping out -----------------------------------------------------------------------------

    [Fact]
    public void Apply_writes_the_four_intrinsic_fields_onto_the_connection()
    {
        var model = ConnectionFieldModel.For(Ms);
        model.Set("Host", " sqlprod ");
        model.Set("Port", "1444");
        model.Set("Database", "sales");
        model.Set("User", "app");

        var info = model.Apply(Blank);

        Assert.Equal(SqlServerProvider.ProviderId, info.ProviderId);
        Assert.Equal("sqlprod", info.Host);          // trimmed
        Assert.Equal(1444, info.Port);
        Assert.Equal("sales", info.Database);
        Assert.Equal("app", info.User);
        Assert.DoesNotContain("Host", info.Options.Keys);   // intrinsic, never duplicated into Options
    }

    [Fact]
    public void A_field_left_at_its_default_is_not_written_to_options()
    {
        // §1.4 leans on this: sslmode is set only when the user sets it. Both providers' declared defaults
        // restate their driver's own, so omitting them changes nothing — and it stops a routine edit from
        // rewriting the options of every connection in the project.
        var info = ConnectionFieldModel.For(Ms).Apply(Blank);
        Assert.Empty(info.Options);
        Assert.Empty(ConnectionFieldModel.For(Pg).Apply(Blank).Options);
    }

    [Fact]
    public void A_changed_option_lands_under_the_providers_own_key()
    {
        // The factory forwards these to the driver verbatim, so the key has to be exactly what the provider
        // declared — anything else silently does nothing.
        var model = ConnectionFieldModel.For(Opt);
        model.Set("compression", "false");
        model.Set("search_path", "app,public");

        var options = model.Apply(Blank).Options;

        Assert.Equal("false", options["compression"]);
        Assert.Equal("app,public", options["search_path"]);
    }

    [Fact]
    public void An_option_returned_to_its_default_is_removed_rather_than_written_back()
    {
        var existing = Blank with
        {
            ProviderId = SqlServerProvider.ProviderId,
            Options = new Dictionary<string, string> { ["compression"] = "false" },
        };
        var model = ConnectionFieldModel.For(Opt, existing);
        model.Set("compression", "true");

        // Dropping the key and writing Encrypt=true mean the same thing to the driver, and the empty form is
        // the one that keeps a project file honest about what the user actually chose.
        Assert.DoesNotContain("compression", model.Apply(existing).Options.Keys);
    }

    // ---- Switching engine ------------------------------------------------------------------------

    [Fact]
    public void Switching_engine_applies_the_new_default_port()
    {
        var switched = ConnectionFieldModel.For(Pg).SwitchTo(Ms);
        Assert.Equal("1433", switched.Get("Port"));
        Assert.Equal(SqlServerProvider.ProviderId, switched.ProviderId);
    }

    [Fact]
    public void Switching_engine_keeps_what_the_user_typed()
    {
        var model = ConnectionFieldModel.For(Pg);
        model.Set("Host", "db.example.com");
        model.Set("Database", "sales");
        model.Set("User", "app");
        model.Set("Port", "6543");          // deliberately not the Postgres default

        var switched = model.SwitchTo(Ms);

        Assert.Equal("db.example.com", switched.Get("Host"));
        Assert.Equal("sales", switched.Get("Database"));
        Assert.Equal("app", switched.Get("User"));
        // A port the user chose is a port they meant, engine change or not.
        Assert.Equal("6543", switched.Get("Port"));
    }

    [Fact]
    public void Switching_engine_drops_the_other_engines_fields_from_the_form()
    {
        var switched = ConnectionFieldModel.For(Pg).SwitchTo(Ms);
        // The other engine's option fields are gone from the form; the intrinsics survive.
        Assert.Null(switched.Get("compression"));
        Assert.Null(switched.Get("search_path"));
    }

    [Fact]
    public void Switching_engine_carries_a_dropped_field_the_user_had_set()
    {
        // Switching by accident and switching back must not be how someone loses an option they set.
        var model = ConnectionFieldModel.For(Opt);
        model.Set("search_path", "app,public");

        var back = model.SwitchTo(Pg).SwitchTo(Opt);

        Assert.Equal("app,public", back.Apply(Blank).Options["search_path"]);
    }

    // ---- Validation ------------------------------------------------------------------------------

    [Fact]
    public void A_blank_required_field_is_reported_by_its_label()
    {
        var model = ConnectionFieldModel.For(Ms);   // Database and User start empty
        var problems = model.Validate(CredentialKind.StoredPassword);

        Assert.Contains("Database is required.", problems);
        Assert.Contains("User is required.", problems);
        Assert.DoesNotContain("Host is required.", problems);   // defaults to localhost
    }

    [Fact]
    public void Integrated_authentication_does_not_demand_a_user()
    {
        // The OS identity *is* the login, so requiring the User box would make Windows authentication
        // impossible to save.
        var model = ConnectionFieldModel.For(Ms);
        model.Set("Database", "sales");

        Assert.Empty(model.Validate(CredentialKind.Integrated));
        Assert.Contains("User is required.", model.Validate(CredentialKind.StoredPassword));
    }

    [Fact]
    public void A_number_field_holding_something_else_is_reported()
    {
        var model = ConnectionFieldModel.For(Pg);
        model.Set("Port", "five thousand");
        model.Set("Database", "d");
        model.Set("User", "u");

        Assert.Equal(new[] { "Port must be a whole number." }, model.Validate(CredentialKind.StoredPassword));
    }

    [Fact]
    public void An_unparseable_port_falls_back_to_this_providers_default_not_to_5432()
    {
        // The bug this pins: the dialog used to fall back to a hardcoded 5432, so a SQL Server connection
        // with a fumbled port was saved pointing at a PostgreSQL port.
        var model = ConnectionFieldModel.For(Ms);
        model.Set("Port", "");
        Assert.Equal(1433, model.Port);
        Assert.Equal(1433, model.Apply(Blank).Port);
    }

    // ---- What the dialog asks it about the engine -------------------------------------------------

    [Fact]
    public void The_model_answers_the_engines_capability_questions_so_the_dialog_does_not_guess()
    {
        Assert.True(ConnectionFieldModel.For(Ms).SupportsIntegratedAuth);
        Assert.False(ConnectionFieldModel.For(Pg).SupportsIntegratedAuth);

        // A named instance makes the Port box a no-op, which the user has no way of knowing.
        Assert.Contains("named instance", ConnectionFieldModel.For(Ms).EndpointHint);
        Assert.Null(ConnectionFieldModel.For(Pg).EndpointHint);
    }

    // ---- Choice fields ---------------------------------------------------------------------------

    [Fact]
    public void A_choice_field_offers_the_providers_declared_candidates_in_order()
    {
        var model = ConnectionFieldModel.For(Choice);

        Assert.Equal(new[] { "fast", "safe", "paranoid" }, Field(model, "mode").Candidates);
        // And the default is one of them, so the dropdown opens on a real selection rather than blank.
        Assert.Equal("fast", model.Get("mode"));
    }

    [Fact]
    public void A_choice_field_with_no_candidates_offers_none()
    {
        // Emptiness is the signal, not a special kind: it is what makes the dialog fall back to a text box
        // instead of rendering a dropdown the user cannot pick anything from.
        Assert.Empty(Field(ConnectionFieldModel.For(Choice), "flavour").Candidates);
    }

    [Fact]
    public void Only_a_choice_field_offers_candidates()
    {
        // A Text or Number field must not acquire a dropdown because someone passed Choices by mistake —
        // the Kind decides the control, and this keeps the two from disagreeing.
        var model = ConnectionFieldModel.For(Choice);
        Assert.Empty(Field(model, "Host").Candidates);
        Assert.Empty(Field(model, "Port").Candidates);
    }

    [Fact]
    public void A_value_the_provider_never_declared_is_still_offered_first()
    {
        // Hand-written into project.json, set by an older build, or imported from another tool. A dropdown
        // that cannot represent it would blank it on the next save, silently — the same loss _carried
        // exists to prevent for undeclared option keys.
        var existing = Blank with
        {
            ProviderId = "choicetest",
            Options = new Dictionary<string, string> { ["mode"] = "reckless" },
        };

        var field = Field(ConnectionFieldModel.For(Choice, existing), "mode");

        // First, not appended: it is the value the connection actually holds, so it must be visible without
        // scrolling a long list.
        Assert.Equal(new[] { "reckless", "fast", "safe", "paranoid" }, field.Candidates);
    }

    [Fact]
    public void A_declared_value_is_not_duplicated_however_it_is_cased()
    {
        // The dialog matches the selection case-insensitively, so a second entry differing only in case
        // would be an option that looks distinct and selects the same thing.
        var existing = Blank with
        {
            ProviderId = "choicetest",
            Options = new Dictionary<string, string> { ["mode"] = "  SAFE  " },
        };

        Assert.Equal(new[] { "fast", "safe", "paranoid" },
            Field(ConnectionFieldModel.For(Choice, existing), "mode").Candidates);
    }

    [Fact]
    public void A_chosen_value_round_trips_through_options()
    {
        // A Choice field is not intrinsic, so it travels in Options like every other declared field — and,
        // like them, a value still at the declared default is not written back.
        var model = ConnectionFieldModel.For(Choice);
        model.Set("mode", "paranoid");
        Assert.Equal("paranoid", model.Apply(Blank).Options["mode"]);

        model.Set("mode", "fast");
        Assert.DoesNotContain("mode", model.Apply(Blank).Options.Keys);
    }

    [Fact]
    public void Candidates_follow_the_value_rather_than_being_fixed_at_construction()
    {
        // The dropdown is rebuilt per render, and Value is mutable — a snapshot taken in the constructor
        // would offer the wrong extra entry after a provider switch carried a value in.
        var field = Field(ConnectionFieldModel.For(Choice), "mode");
        Assert.Equal(3, field.Candidates.Count);

        field.Value = "reckless";
        Assert.Equal("reckless", field.Candidates[0]);
        Assert.Equal(4, field.Candidates.Count);
    }

    // ---- The legacy TLS key is the one option a save must not preserve (§1.4) -----------------

    /// <summary>
    /// A pre-#23 project (or a DBeaver import) carries <c>sslmode</c> in the bag and no <c>Tls</c> field.
    /// <see cref="TlsPolicy.Resolve"/> falls back to that entry whenever the field is still at its default,
    /// so carrying it through a save let a stale <c>disable</c> outrank the mode the user had just picked:
    /// the dialog and the stored record both read Prefer while the connection ran unencrypted, and the write
    /// guard could not expose it because nothing about the SQL was different.
    /// </summary>
    [Fact]
    public void Saving_drops_a_legacy_sslmode_entry_rather_than_carrying_it()
    {
        var existing = Blank with
        {
            Host = "db.example.com",
            Options = new Dictionary<string, string>
            {
                ["sslmode"] = "disable",
                ["search_path"] = "app",
            },
        };

        var saved = ConnectionFieldModel.For(Opt, existing).Apply(existing with { Tls = TlsMode.Prefer });

        Assert.DoesNotContain("sslmode", saved.Options.Keys);
        Assert.Equal(TlsMode.Prefer, TlsPolicy.Resolve(saved));   // the field, not the bag
        Assert.Equal("app", saved.Options["search_path"]);        // every other key still carried
    }

    /// <summary>However it is spelled: the bag is read case-insensitively, so dropping only the lower-case
    /// spelling would leave the same override in place under a different one.</summary>
    [Theory]
    [InlineData("SSLMode")]
    [InlineData("SslMode")]
    public void The_legacy_key_is_dropped_whatever_its_case(string key)
    {
        var existing = Blank with
        {
            Options = new Dictionary<string, string> { [key] = "disable" },
        };

        var saved = ConnectionFieldModel.For(Opt, existing).Apply(existing with { Tls = TlsMode.Require });

        Assert.Empty(saved.Options);
        Assert.Equal(TlsMode.Require, TlsPolicy.Resolve(saved));
    }

    // ---- Case, and providers that declare nothing -------------------------------------------------

    /// <summary>
    /// <c>Carry</c> excludes a declared key case-insensitively, so the load has to read one the same way.
    /// A persisted <c>Search_Path</c> against a field keyed <c>search_path</c> was excluded from
    /// <c>_carried</c> as a match and then missed by an ordinal load — so it was neither carried nor loaded,
    /// and disappeared on the next save, which is precisely what the model's own doc says it cannot do.
    /// (A key that differs by more than case, <c>SearchPath</c>, is a different key: it is carried
    /// verbatim, which is the other half of the same promise.)
    /// </summary>
    [Fact]
    public void An_option_key_in_another_case_is_loaded_rather_than_dropped()
    {
        var existing = Blank with
        {
            Options = new Dictionary<string, string> { ["Search_Path"] = "reporting" },
        };

        var model = ConnectionFieldModel.For(Opt, existing);

        Assert.Equal("reporting", model.Get("search_path"));
        Assert.Equal("reporting", model.Apply(existing).Options["search_path"]);
    }

    /// <summary>
    /// A provider that declares no fields must leave the template's endpoint alone. <c>Get</c> returning
    /// null means "this engine has no such box", which is not an empty box — coercing it wrote
    /// <c>Host = ""</c> and <c>Port = 0</c> over a perfectly good template, which is what a save in a demo
    /// session did (DemoProvider declares none).
    /// </summary>
    [Fact]
    public void A_provider_that_declares_no_fields_leaves_the_endpoint_alone()
    {
        var template = Blank with { Host = "kept", Port = 4242, Database = "keptdb", User = "keptuser" };

        var saved = ConnectionFieldModel.For(new FieldlessProvider(), template).Apply(template);

        Assert.Equal("kept", saved.Host);
        Assert.Equal(4242, saved.Port);
        Assert.Equal("keptdb", saved.Database);
        Assert.Equal("keptuser", saved.User);
        Assert.Equal("fieldless", saved.ProviderId);
    }

    /// <summary>An engine with no connection fields at all — what <c>DemoProvider</c> is.</summary>
    private sealed class FieldlessProvider : IDbProvider
    {
        public string Id => "fieldless";
        public string DisplayName => "Fieldless";
        public bool SupportsIntegratedAuth => false;
        public bool SupportsEntraToken => false;
        public DbErrorKind Classify(QueryError error) => DbErrorKind.Unknown;
        public DbErrorKind ClassifyException(Exception exception) => DbErrorKind.Unknown;

        public IReadOnlyList<ConnectionField> ConnectionFields { get; } = Array.Empty<ConnectionField>();

        public IDbConnectionFactory CreateConnectionFactory(ConnectionInfo info, string? password)
            => throw new NotSupportedException("declares fields only");
        public IMetadataReader CreateMetadataReader(IDbConnectionFactory factory)
            => throw new NotSupportedException("declares fields only");
        public IQueryExecutor CreateQueryExecutor(IDbConnectionFactory factory)
            => throw new NotSupportedException("declares fields only");
    }
}
