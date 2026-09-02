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
        Assert.Equal(
            new[] { "Host", "Port", "Database", "User", "sslmode" },
            ConnectionFieldModel.For(Pg).Fields.Select(f => f.Key));
        Assert.Equal(
            new[] { "Host", "Port", "Database", "User", "Encrypt", "TrustServerCertificate" },
            ConnectionFieldModel.For(Ms).Fields.Select(f => f.Key));
    }

    [Fact]
    public void A_new_connection_starts_on_the_providers_declared_defaults()
    {
        Assert.Equal("5432", ConnectionFieldModel.For(Pg).Get("Port"));
        Assert.Equal("1433", ConnectionFieldModel.For(Ms).Get("Port"));
        Assert.Equal("localhost", ConnectionFieldModel.For(Ms).Get("Host"));
        Assert.Equal("true", ConnectionFieldModel.For(Ms).Get("Encrypt"));
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
            Options = new Dictionary<string, string> { ["Encrypt"] = "false", ["entra.resource"] = "https://x/" },
        };

        var model = ConnectionFieldModel.For(Ms, existing);

        Assert.Equal(@"SQLPROD\SALES", model.Get("Host"));
        Assert.Equal("1444", model.Get("Port"));
        Assert.Equal("sales", model.Get("Database"));
        Assert.Equal("app", model.Get("User"));
        Assert.Equal("false", model.Get("Encrypt"));
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
        var model = ConnectionFieldModel.For(Ms);
        model.Set("Encrypt", "false");
        model.Set("TrustServerCertificate", "true");

        var options = model.Apply(Blank).Options;

        Assert.Equal("false", options["Encrypt"]);
        Assert.Equal("true", options["TrustServerCertificate"]);
    }

    [Fact]
    public void An_option_returned_to_its_default_is_removed_rather_than_written_back()
    {
        var existing = Blank with
        {
            ProviderId = SqlServerProvider.ProviderId,
            Options = new Dictionary<string, string> { ["Encrypt"] = "false" },
        };
        var model = ConnectionFieldModel.For(Ms, existing);
        model.Set("Encrypt", "true");

        // Dropping the key and writing Encrypt=true mean the same thing to the driver, and the empty form is
        // the one that keeps a project file honest about what the user actually chose.
        Assert.DoesNotContain("Encrypt", model.Apply(existing).Options.Keys);
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
        Assert.Null(switched.Get("sslmode"));
        Assert.Equal("true", switched.Get("Encrypt"));
    }

    [Fact]
    public void Switching_engine_carries_a_dropped_field_the_user_had_set()
    {
        // Switching by accident and switching back must not be how someone loses an sslmode they set.
        var model = ConnectionFieldModel.For(Pg);
        model.Set("sslmode", "Require");

        var back = model.SwitchTo(Ms).SwitchTo(Pg);

        Assert.Equal("Require", back.Apply(Blank).Options["sslmode"]);
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
}
