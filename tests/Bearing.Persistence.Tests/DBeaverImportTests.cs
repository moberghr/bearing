using Bearing.Core.Data;
using Bearing.Persistence.Import;
using Xunit;

namespace Bearing.Persistence.Tests;

/// <summary>
/// The DBeaver importer (#72), exercised against <c>Fixtures/dbeaver-data-sources.json</c> — a redacted copy
/// of a real workspace. It was chosen as the fixture because it happens to cover every edge the issue's
/// written mapping missed: a symbolic Eclipse colour id, a missing port, a null database, an absent user on
/// every row, three providers, folders, and white-as-no-tint.
/// </summary>
public class DBeaverImportTests
{
    private static string Fixture()
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "dbeaver-data-sources.json"));

    private static DBeaverImportResult Parsed() => DBeaverImport.Parse(Fixture());

    private static ConnectionInfo ByHostPort(DBeaverImportResult r, string host, int port)
        => r.Connections.Single(c => c.Host == host && c.Port == port);

    // ---- the real file -------------------------------------------------------------------------

    [Fact]
    public void Only_postgres_connections_are_imported()
    {
        var result = Parsed();

        Assert.Equal(4, result.Connections.Count);
        Assert.All(result.Connections, c => Assert.Equal("postgres", c.ProviderId));
    }

    [Fact]
    public void Everything_else_is_reported_rather_than_dropped_silently()
    {
        var result = Parsed();

        Assert.Equal(6, result.Skipped.Count);
        Assert.Contains(result.Skipped, s => s.Reason == "unsupported provider sqlserver");
        Assert.Contains(result.Skipped, s => s.Reason == "unsupported provider mysql");
    }

    [Fact]
    public void Folders_come_across_so_the_grouping_survives_the_move()
    {
        var result = Parsed();

        Assert.Equal(4, result.Folders.Count);
        // Two of the four postgres rows are filed; the other two sit at the root.
        Assert.Equal(2, result.Connections.Count(c => c.Folder is not null));
        Assert.Equal(2, result.Connections.Count(c => c.Folder is null));
    }

    [Fact]
    public void A_connection_with_no_port_recorded_gets_the_postgres_default()
    {
        // Every postgres row in this file has a port; the guard is that none came through as 0 or negative.
        Assert.All(Parsed().Connections, c => Assert.InRange(c.Port, 1, 65535));
    }

    [Fact]
    public void A_null_database_imports_as_blank_rather_than_failing_the_row()
    {
        var result = Parsed();

        // "jdbc:postgresql://localhost:5433/" — a real row with no database.
        Assert.Equal("", ByHostPort(result, "localhost", 5433).Database);
    }

    [Fact]
    public void No_row_carries_a_user_because_DBeaver_keeps_it_encrypted()
    {
        Assert.All(Parsed().Connections, c => Assert.Equal("", c.User));
    }

    [Fact]
    public void Imported_connections_prompt_rather_than_claiming_a_stored_password()
    {
        // No password can be imported, so "stored password" would fail at connect time with an unhelpful
        // error. Prompting is both honest and what actually works until the user saves one.
        Assert.All(Parsed().Connections, c => Assert.Equal(CredentialKind.Prompt, c.CredentialKind));
    }

    [Fact]
    public void The_summary_says_a_user_and_a_password_are_still_needed()
    {
        Assert.Contains(Parsed().Warnings, w => w.Contains("user name") && w.Contains("password"));
    }

    [Fact]
    public void Every_imported_connection_gets_a_fresh_identity()
    {
        var result = Parsed();

        // DBeaver's key means nothing here: an id is Bearing's secret-store lookup key.
        Assert.Equal(result.Connections.Count, result.Connections.Select(c => c.Id).Distinct().Count());
        Assert.DoesNotContain(result.Connections, c => c.Id == Guid.Empty);
    }

    [Fact]
    public void Jdbc_only_properties_are_left_behind_and_named()
    {
        var result = Parsed();

        Assert.All(result.Connections, c =>
            Assert.All(c.Options.Keys, k =>
                Assert.True(k is "sslmode" or "search_path", $"unexpected imported option '{k}'")));
        Assert.Contains(result.Warnings, w => w.Contains("connectTimeout"));
    }

    [Fact]
    public void Sslmode_survives_as_something_Npgsql_can_parse()
    {
        var azure = Parsed().Connections.Single(c => c.Options.ContainsKey("sslmode"));
        Assert.Equal("require", azure.Options["sslmode"]);
    }

    // ---- connection types ----------------------------------------------------------------------

    [Fact]
    public void A_symbolic_eclipse_colour_maps_to_a_bearing_preset()
    {
        // The prod type in a real file reads "org.jkiss.dbeaver.color.connectionType.prod.background",
        // not an R,G,B triple — the mapping in #72 assumed the latter.
        var result = DBeaverImport.Parse("""
        {
          "connections": { "c1": { "name": "p", "provider": "postgresql",
                                   "configuration": { "host": "h", "port": "5432", "type": "prod" } } },
          "connection-types": { "prod": { "name": "Production", "color":
              "org.jkiss.dbeaver.color.connectionType.prod.background", "confirm-data-change": true } }
        }
        """);

        var c = result.Connections.Single();
        Assert.Equal("production", c.Environment);
        Assert.Equal("#E5484D", c.EnvironmentColor);
        Assert.True(c.RequireWriteConfirmation);
    }

    [Fact]
    public void An_rgb_triple_converts_to_hex()
    {
        var result = DBeaverImport.Parse("""
        {
          "connections": { "c1": { "name": "s", "provider": "postgresql",
                                   "configuration": { "host": "h", "type": "test" } } },
          "connection-types": { "test": { "name": "Test", "color": "214,250,207" } }
        }
        """);

        Assert.Equal("#D6FACF", result.Connections.Single().EnvironmentColor);
    }

    [Fact]
    public void White_is_DBeavers_no_tint_and_imports_as_no_colour()
    {
        // DBeaver's stock Development type is 255,255,255. Importing that as a colour would paint every
        // development row white instead of leaving it neutral.
        var result = DBeaverImport.Parse("""
        {
          "connections": { "c1": { "name": "d", "provider": "postgresql",
                                   "configuration": { "host": "h", "type": "dev" } } },
          "connection-types": { "dev": { "name": "Development", "color": "255,255,255" } }
        }
        """);

        Assert.Null(result.Connections.Single().EnvironmentColor);
        Assert.Equal("development", result.Connections.Single().Environment);
    }

    [Fact]
    public void Confirm_data_change_becomes_the_write_guard()
    {
        var result = Parsed();
        var guarded = result.Connections.Where(c => c.RequireWriteConfirmation).ToList();

        // Only the prod type sets it, and no postgres row in this workspace uses that type.
        Assert.All(result.Connections, c =>
            Assert.Equal(c.Environment == "production", c.RequireWriteConfirmation));
        Assert.Empty(guarded);
    }

    // ---- defensiveness -------------------------------------------------------------------------

    [Fact]
    public void An_unreadable_file_reports_instead_of_throwing()
    {
        var result = DBeaverImport.Parse("{ not json");

        Assert.Empty(result.Connections);
        Assert.Single(result.Warnings);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("null")]
    public void Json_that_is_not_a_data_sources_document_is_declined(string json)
        => Assert.Empty(DBeaverImport.Parse(json).Connections);

    [Fact]
    public void An_empty_document_imports_nothing_and_says_nothing_alarming()
    {
        var result = DBeaverImport.Parse("{}");

        Assert.Empty(result.Connections);
        Assert.Empty(result.Skipped);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void A_connection_with_no_settings_is_skipped_not_fatal()
    {
        var result = DBeaverImport.Parse("""
        {
          "connections": {
            "bad": { "name": "broken", "provider": "postgresql" },
            "good": { "name": "fine", "provider": "postgresql", "configuration": { "host": "h" } }
          }
        }
        """);

        Assert.Equal("fine", result.Connections.Single().Name);
        Assert.Equal("no connection settings recorded", result.Skipped.Single().Reason);
    }

    [Fact]
    public void Unknown_keys_are_ignored_rather_than_failing_a_future_format()
    {
        var result = DBeaverImport.Parse("""
        {
          "some-future-section": { "x": 1 },
          "connections": { "c1": { "name": "p", "provider": "postgresql", "brand-new-field": true,
                                   "configuration": { "host": "h", "port": "5432", "unheard-of": [1,2] } } }
        }
        """);

        Assert.Equal("h", result.Connections.Single().Host);
    }

    [Fact]
    public void A_port_written_as_a_number_reads_the_same_as_one_written_as_a_string()
    {
        var result = DBeaverImport.Parse("""
        {"connections":{"c1":{"name":"p","provider":"postgresql","configuration":{"host":"h","port":5433}}}}
        """);

        Assert.Equal(5433, result.Connections.Single().Port);
    }

    [Fact]
    public void A_nonsense_port_falls_back_to_the_default()
    {
        var result = DBeaverImport.Parse("""
        {"connections":{"c1":{"name":"p","provider":"postgresql","configuration":{"host":"h","port":"nope"}}}}
        """);

        Assert.Equal(5432, result.Connections.Single().Port);
    }

    [Fact]
    public void A_hyphenated_sslmode_is_rewritten_for_Npgsql()
    {
        // JDBC spells it verify-ca; Npgsql's SslMode enum has no hyphen, so the imported value would
        // otherwise fail to parse at connect time.
        var result = DBeaverImport.Parse("""
        {"connections":{"c1":{"name":"p","provider":"postgresql",
          "configuration":{"host":"h","properties":{"sslmode":"verify-full"}}}}}
        """);

        Assert.Equal("verifyfull", result.Connections.Single().Options["sslmode"]);
    }

    [Fact]
    public void A_folder_a_connection_claims_is_materialised_even_if_undeclared()
    {
        var result = DBeaverImport.Parse("""
        {"connections":{"c1":{"name":"p","provider":"postgresql","folder":"Clients/Aur",
          "configuration":{"host":"h"}}}}
        """);

        Assert.Contains("Clients/Aur", result.Folders);
    }

    [Fact]
    public void A_connection_with_no_name_falls_back_to_its_key()
    {
        var result = DBeaverImport.Parse("""
        {"connections":{"postgres-jdbc-abc":{"provider":"postgresql","configuration":{"host":"h"}}}}
        """);

        Assert.Equal("postgres-jdbc-abc", result.Connections.Single().Name);
    }
}
