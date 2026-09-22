using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bearing.Core.Data;
using Bearing.Core.Logging;
using Bearing.Core.Workspace;
using Bearing.Data;
using Bearing.Data.Postgres;
using Bearing.Cli.Tools;
using Bearing.Persistence;
using Bearing.Results;
using Bearing.Sessions;
using Bearing.Testing;
using Xunit;

namespace Bearing.Cli.Tests;

/// <summary>
/// The whole path against a live server: a project file on disk, a password in the real OS keychain, the
/// real providers, and the protocol on top. Skips when Postgres is unreachable (§4.2) — and skips when this
/// machine has no keychain, because a stored password with nowhere to live is not a failure of anything
/// under test.
/// <para>
/// The two assertions that matter most here are the ones that could not be made anywhere else:
/// <c>default_transaction_read_only</c> and <c>statement_timeout</c> are read back <i>from the server</i>,
/// which is the only way to know that <see cref="ExternalAccessPolicy.ForExternalHost"/>'s forcing reached
/// the startup packet rather than merely being set on a record. §1.9 is emphatic that this is the
/// difference between a setting and a claim.
/// </para>
/// </summary>
public class LiveExposureTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bearing-mcp-tests", Guid.NewGuid().ToString("N"));

    private readonly Guid _connectionId = Guid.NewGuid();
    private ISecretStore? _secrets;
    private ConnectionSessionManager? _sessions;
    private readonly RecordingQueryLog _log = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // The keychain entry is the one thing here that outlives the process, so it is removed whatever
        // happened — a test that leaves credentials on a developer's machine is its own bug.
        if (_secrets is not null)
        {
            try { await _secrets.DeleteAsync(_connectionId, CancellationToken.None); } catch { }
        }

        if (_sessions is not null) await _sessions.DisposeAsync();

        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// The real thing: a project on disk, the password in the OS keychain, the real providers. Skips when
    /// Postgres is unreachable (§4.2), and when this machine has no keychain — a stored password with
    /// nowhere to live is not a failure of anything under test.
    /// </summary>
    private async Task<BearingHost> LiveHostAsync()
    {
        var provider = new PostgresProvider();
        await using (var probe = provider.CreateConnectionFactory(PgTestServer.Info(), PgTestServer.Password))
            await PgTestServer.RequireAsync(probe);

        _secrets = await SecretStoreFactory.CreatePlatformStoreAsync();
        Skip.If(_secrets is null or { CanStore: false }, "No OS keychain on this machine to store the password in.");
        await _secrets!.SetPasswordAsync(_connectionId, PgTestServer.Password, CancellationToken.None);

        var projectDirectory = await WriteProjectAsync();

        var providers = new ProviderRegistry();
        var credentials = new CredentialResolver(() => _secrets, prompt: null, new EntraTokenProvider());
        // No sweep timer: this manager lives for one test, and a background sweep would be a second thing
        // that could dispose the pool while assertions run.
        _sessions = new ConnectionSessionManager(
            providers, () => credentials, idleTimeout: null, clock: null, runSweepTimer: false);

        return new BearingHost(new JsonProjectStore(), projectDirectory, providers, _sessions, _log);
    }

    [SkippableFact]
    public async Task An_exposed_connection_serves_reads_and_refuses_everything_else()
    {
        var host = await LiveHostAsync();

        // ---- it is listed, and named, and nothing else about it is ---------------------------------
        var listed = (ConnectionsResponse)await host.ListConnectionsAsync(CancellationToken.None);
        var connection = Assert.Single(listed.Connections);
        Assert.Equal("agent-reads", connection.Name);
        Assert.Equal("read-only", connection.Access);
        Assert.True(connection.Available);

        // §1.11: the caller gets a handle, not an address. Asserted on the serialised form as well as the
        // record, because a field could carry an address under an innocent name — and the record's own
        // shape is now the compiler's business, which is the point of it being a record.
        //
        // The connections are deliberately not named after the database: the first version of this called
        // one "agent-pagila", and "no database name in the output" then failed on the connection's own
        // name. A fixture that cannot express the claim is worse than no test (§4.7).
        Assert.Null(connection.Environment);
        Assert.Null(connection.UnavailableReason);

        // Through the runner's own serializer, so this asserts what a caller actually receives —
        // and by the runtime type, since the interface has no properties to write.
        var rendered = CliRunner.Serialize(listed);
        Assert.DoesNotContain(PgTestServer.Host, rendered);
        Assert.DoesNotContain(PgTestServer.User, rendered);
        Assert.DoesNotContain(PgTestServer.Database, rendered);
        Assert.DoesNotContain(PgTestServer.Password, rendered);
        Assert.DoesNotContain(PgTestServer.Port.ToString(), rendered);

        // ---- the forced settings reached the server, not just the record ---------------------------
        var readOnly = await ScalarAsync(host, "show default_transaction_read_only");
        Assert.Equal("on", readOnly);

        var timeout = await ScalarAsync(host, "show statement_timeout");
        Assert.Equal($"{SessionPolicy.PresetTimeoutSeconds}s", timeout);

        // ---- reads work ----------------------------------------------------------------------------
        var tables = (TablesResponse)await host.ListTablesAsync("agent-reads", "public", CancellationToken.None);
        var names = tables.Tables.Select(t => t.Name).ToList();
        Assert.Contains("payment", names);
        Assert.Contains("customer", names);

        var payment = (TableResponse)await host.DescribeTableAsync(
            "agent-reads", "public.payment", CancellationToken.None);
        Assert.Contains("payment_id", payment.Columns.Select(c => c.Name));

        // pagila's payment is a *partitioned parent* and declares no foreign keys of its own — the three
        // are on each monthly partition. Measured, after this test first asserted the opposite: `select
        // conname from pg_constraint where conrelid='public.payment'::regclass and contype='f'` returns
        // nothing, and relkind is 'p'. Asserted rather than dropped, because it is the shape most likely to
        // be "fixed" by someone who assumed the read was broken (§4.7).
        Assert.Empty(payment.ForeignKeys);

        // So the keys are checked on an ordinary table — and on both of its sides, which is what
        // "foreign keys touching it" means and the reason a caller can ask "what references this".
        //
        // The arithmetic is measured, not assumed: rental declares three (to customer, inventory and
        // staff) and six of payment's monthly partitions reference it, so nine reach this list. Asserting
        // three here was the third wrong premise in this file, and the count is kept split so a future
        // reader can see which half moved.
        var rental = (TableResponse)await host.DescribeTableAsync("agent-reads", "rental", CancellationToken.None);

        var declared = rental.ForeignKeys.Where(k => k.From.Table == "public.rental").ToList();
        var pointingAtIt = rental.ForeignKeys.Where(k => k.To.Table == "public.rental").ToList();
        Assert.Equal(3, declared.Count);
        Assert.Equal(6, pointingAtIt.Count);
        Assert.Equal(rental.ForeignKeys.Count, declared.Count + pointingAtIt.Count);

        var toCustomer = Assert.Single(declared, k => k.To.Table == "public.customer");
        Assert.Equal("customer_id", toCustomer.From.Columns[0]);
        Assert.Equal("customer_id", toCustomer.To.Columns[0]);

        var rows = (QueryResponse)await host.QueryAsync(
            Run("agent-reads", "select payment_id, amount from payment order by payment_id", maxRows: 3),
            CancellationToken.None);
        var set = Assert.Single(rows.Results);
        Assert.Equal(3, set.RowCount);
        Assert.True(set.Truncated);

        // ---- writes do not -------------------------------------------------------------------------
        var refused = await Assert.ThrowsAsync<CommandFailure>(() => host.QueryAsync(Run("agent-reads", "update payment set amount = 0"), CancellationToken.None));
        Assert.Contains("reads only", refused.Message);
        Assert.Contains("UPDATE", refused.Message);

        // The advice has to be advice the caller can act on. WriteRefusal's own sentence ends "Turn
        // read-only off for this connection to write to it", which is written for the person at the
        // keyboard and is wrong through here twice over: read-only is not this connection's setting, and
        // turning it off would change nothing because exposure forces it back on (§1.1).
        Assert.DoesNotContain("Turn read-only off", refused.Message);

        // The half a client-side refusal cannot provide, and the reason §1.9a insists the two engines are
        // described differently. `nextval` writes, but its statement leads with SELECT, so WriteGuard does
        // not flag it — this reaches the server and the server is what refuses it. The message is Postgres'
        // own, which is the proof: nothing in Bearing produces that sentence.
        //
        // film_film_id_seq is pagila's own (§4.7 — a fixture's contents are a fact to look up, and this one
        // is looked up rather than assumed).
        var atServer = await Assert.ThrowsAsync<CommandFailure>(() => host.QueryAsync(Run("agent-reads", "select nextval('film_film_id_seq')"), CancellationToken.None));
        Assert.Contains("read-only transaction", atServer.Message);
    }

    /// <summary>
    /// The bypass, against the live server that demonstrated it. Before <c>ExternalSqlPolicy</c> existed
    /// both of these returned a number: <c>begin read write</c> and <c>set transaction read write</c> are
    /// not risky verbs, so the deny-list passed them, and Postgres honoured them — the sequence really
    /// advanced on a connection exposed read-only.
    /// <para>
    /// The sequence is read before and after, because "the command failed" is a weaker claim than "nothing
    /// happened": a refusal that arrived after the statement ran would look identical from the exit code.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task The_read_only_barrier_cannot_be_lifted_from_outside()
    {
        var host = await LiveHostAsync();

        var before = await ScalarAsync(host, "select last_value from film_film_id_seq");

        foreach (var attempt in new[]
                 {
                     "begin read write; select nextval('film_film_id_seq')",
                     "set transaction read write; select nextval('film_film_id_seq')",
                     "begin; select nextval('film_film_id_seq'); commit",
                     "set default_transaction_read_only = off; select nextval('film_film_id_seq')",
                 })
        {
            var refused = await Assert.ThrowsAsync<CommandFailure>(
                () => host.QueryAsync(Run("agent-reads", attempt), CancellationToken.None));
            Assert.NotEmpty(refused.Message);
        }

        Assert.Equal(before, await ScalarAsync(host, "select last_value from film_film_id_seq"));
    }

    /// <summary>
    /// Read-only bounds writes and never bounded these: measured on this same superuser connection with
    /// read-only fully in force, <c>pg_read_file('/etc/passwd')</c> returned the file and
    /// <c>pg_ls_dir('/etc')</c> listed the directory. They are reads — just not of the data.
    /// </summary>
    [SkippableFact]
    public async Task A_function_that_reaches_past_the_data_does_not_run()
    {
        var host = await LiveHostAsync();

        foreach (var attempt in new[]
                 {
                     "select pg_read_file('/etc/passwd')",
                     "select pg_ls_dir('/etc')",
                     "select set_config('default_transaction_read_only', 'off', false)",
                 })
        {
            await Assert.ThrowsAsync<CommandFailure>(
                () => host.QueryAsync(Run("agent-reads", attempt), CancellationToken.None));
        }

        // And an ordinary read on the same connection still works, so the refusals above are the policy
        // rather than a connection that stopped functioning.
        Assert.Equal("1000", await ScalarAsync(host, "select count(*)::text from film"));
    }

    /// <summary>
    /// What an agent ran has to be in the record, or the audit export's "what ran against production last
    /// week" is quietly incomplete (§1.6). The origin is what tells such a row from one the user ran.
    /// </summary>
    [SkippableFact]
    public async Task A_query_is_recorded_with_the_origin_that_says_who_ran_it()
    {
        var host = await LiveHostAsync();

        await host.QueryAsync(Run("agent-reads", "select 1"), CancellationToken.None);

        var row = Assert.Single(_log.Entries);
        Assert.Equal(QueryOrigin.Cli, row.Origin);
        Assert.Equal("select 1", row.SqlText);
        Assert.Equal("agent-reads", row.ConnectionName);
        Assert.Equal(_connectionId, row.ConnectionId);
        Assert.True(row.Success);
    }

    /// <summary>
    /// **A refusal is recorded too**, though nothing ran. §1.3 calls the log the record of SQL the user
    /// ran, and strictly this did not — but the reason this path exists is that the caller is not the
    /// person at the keyboard, and "an agent tried to lift read-only and was stopped" is the single most
    /// useful line the log can hold. Written in the shape a failed execution already has.
    /// </summary>
    [SkippableFact]
    public async Task A_refused_statement_is_recorded_as_an_attempt()
    {
        var host = await LiveHostAsync();

        await Assert.ThrowsAsync<CommandFailure>(() => host.QueryAsync(Run("agent-reads", "begin read write; select nextval('film_film_id_seq')"), CancellationToken.None));

        var row = Assert.Single(_log.Entries);
        Assert.Equal(QueryOrigin.Cli, row.Origin);
        Assert.False(row.Success);
        Assert.Contains("BEGIN", row.ErrorMessage);
        // The statement it tried, verbatim — an audit that recorded only "refused" could not say what of.
        Assert.Contains("begin read write", row.SqlText);
        Assert.Equal(0, row.RowCount);
    }

    /// <summary>A connection the user has <b>not</b> exposed is refused by name, and never connected to.</summary>
    [SkippableFact]
    public async Task A_connection_that_was_not_exposed_is_refused_by_name()
    {
        var provider = new PostgresProvider();
        await using var probe = provider.CreateConnectionFactory(PgTestServer.Info(), PgTestServer.Password);
        await PgTestServer.RequireAsync(probe);

        var projectDirectory = await WriteProjectAsync();

        var providers = new ProviderRegistry();
        await using var sessions = new ConnectionSessionManager(
            providers, () => null, idleTimeout: null, clock: null, runSweepTimer: false);
        var host = new BearingHost(new JsonProjectStore(), projectDirectory, providers, sessions);

        // Only the exposed one is listed at all.
        var listed = (ConnectionsResponse)await host.ListConnectionsAsync(CancellationToken.None);
        Assert.Single(listed.Connections);

        var refused = await Assert.ThrowsAsync<CommandFailure>(
            () => host.QueryAsync(Run("private-reads", "select 1"), CancellationToken.None));

        // Told plainly rather than reported as missing: a caller that can run this process can read the
        // same project.json, so hiding it would be theatre — and this way it can ask the owner.
        Assert.Contains("not exposed", refused.Message);
    }

    /// <summary>
    /// <c>--out report.csv</c> against a live server. One statement to a CSV is the streamed path
    /// (<c>ResultExport.WriteCsvStreamAsync</c>), so this is the only place the whole of it runs: a real
    /// reader, real batches, a real file.
    /// <para>
    /// The file is compared with the one the materialising writer produces from the same query, rather than
    /// with a hand-written expectation — an equality that fails if either half of the CSV rendering ever
    /// drifts, which is the risk introduced by there being two ways to write this format at all.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_single_statement_csv_export_streams_and_matches_the_materialised_file()
    {
        var host = await LiveHostAsync();
        const string sql = "select film_id, title, rental_rate from film order by film_id";

        var streamed = Path.Combine(_root, "streamed.csv");
        var response = (ExportResponse)await host.QueryAsync(
            Run("agent-reads", sql) with { OutPath = streamed }, CancellationToken.None);

        // Unlimited by default for an export: a capped file is a silently truncated one.
        Assert.Equal(1000, response.Rows);
        Assert.Equal(1, response.Results);
        Assert.False(response.Truncated);
        Assert.Equal(Path.GetFullPath(streamed), response.Written);

        // The same bytes the app's own export would have written for these rows.
        var rows = (QueryResponse)await host.QueryAsync(
            Run("agent-reads", sql) with { UnlimitedRows = true }, CancellationToken.None);
        var materialised = Path.Combine(_root, "materialised.csv");
        ResultExport.Write(
            materialised,
            new TableBlock(
                rows.Results[0].Columns.Select(c => new ColumnDescriptor(c.Name, c.Type, typeof(object))).ToList(),
                rows.Results[0].Rows.Select(r => r.ToArray()).ToList()),
            ExportFormat.Csv,
            "Result 1");

        Assert.Equal(File.ReadAllBytes(materialised), File.ReadAllBytes(streamed));

        // And it is in the history like any other statement the caller ran, with the rows that reached the
        // file — a streamed read has no QueryResult to take that count from, so it is counted on the way past.
        var logged = _log.Entries.Last(e => e.SqlText == sql && e.Success);
        Assert.Equal(1000, logged.RowCount);
        Assert.Equal(QueryOrigin.Cli, logged.Origin);
    }

    /// <summary>
    /// The row ceiling reaches the caller even though the rows went to a file. A truncated export is the one
    /// outcome a script must not mistake for a complete one, and nothing in the file itself says so.
    /// </summary>
    [SkippableFact]
    public async Task A_capped_export_says_it_was_capped()
    {
        var host = await LiveHostAsync();
        var path = Path.Combine(_root, "capped.csv");

        var response = (ExportResponse)await host.QueryAsync(
            Run("agent-reads", "select film_id from film order by film_id", maxRows: 10) with { OutPath = path },
            CancellationToken.None);

        Assert.Equal(10, response.Rows);
        Assert.True(response.Truncated);
        Assert.Equal(11, File.ReadAllLines(path).Length);   // the header and ten rows
    }

    /// <summary>
    /// <c>--timeout</c> may only <b>lower</b> what the connection allows. Raising it would let a caller lift
    /// a limit its owner set, which is the inversion §1.9 exists to prevent — and read back from the server
    /// rather than off the record, because a setting that never reached the startup packet is a claim.
    /// </summary>
    [SkippableFact]
    public async Task A_caller_may_lower_the_statement_timeout_and_may_not_raise_it()
    {
        var host = await LiveHostAsync();

        // Lower: honoured.
        Assert.Equal("5s", await TimeoutWith(host, 5));

        // Higher than the 30s an exposed connection is given: ignored, and the connection's own stands.
        Assert.Equal($"{SessionPolicy.PresetTimeoutSeconds}s", await TimeoutWith(host, 600));

        // Equal is not a lowering either, and must not be mistaken for one by an off-by-one.
        Assert.Equal(
            $"{SessionPolicy.PresetTimeoutSeconds}s",
            await TimeoutWith(host, SessionPolicy.PresetTimeoutSeconds));
    }

    private static async Task<string?> TimeoutWith(BearingHost host, int seconds)
    {
        var response = (QueryResponse)await host.QueryAsync(
            Run("agent-reads", "show statement_timeout") with { TimeoutSeconds = seconds },
            CancellationToken.None);
        return Convert.ToString(response.Results[0].Rows[0][0], CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The materialising export: more than one statement, so the streamed single-statement path does not
    /// apply. An xlsx takes a sheet per result set — the app's own Export-run rule, reached through the same
    /// writer — and the file is read back rather than trusted, since a workbook that is subtly malformed
    /// fails at the user's desk (§4.4).
    /// </summary>
    [SkippableFact]
    public async Task A_batch_exports_one_sheet_per_result_set()
    {
        var host = await LiveHostAsync();
        var path = Path.Combine(_root, "book.xlsx");

        var response = (ExportResponse)await host.QueryAsync(
            Run("agent-reads", "select title from film order by title limit 3; select first_name from actor order by actor_id limit 2")
                with { OutPath = path },
            CancellationToken.None);

        Assert.Equal(2, response.Results);
        Assert.Equal(5, response.Rows);
        Assert.False(response.Truncated);

        using var zip = ZipFile.OpenRead(path);
        Assert.NotNull(zip.GetEntry("xl/worksheets/sheet1.xml"));
        Assert.NotNull(zip.GetEntry("xl/worksheets/sheet2.xml"));
    }

    /// <summary>
    /// A CSV holds one table, so a batch that returned two is refused rather than written as the first one
    /// — the caller asked for a file of the answer, and a file of part of it is the outcome no error
    /// message can undo later. Nothing is written at the path.
    /// </summary>
    [SkippableFact]
    public async Task A_csv_refuses_a_batch_that_returned_more_than_one_table()
    {
        var host = await LiveHostAsync();
        var path = Path.Combine(_root, "two.csv");

        var refused = await Assert.ThrowsAsync<CommandFailure>(() => host.QueryAsync(
            Run("agent-reads", "select 1 as a; select 2 as b") with { OutPath = path },
            CancellationToken.None));

        Assert.Contains("holds one table", refused.Message);
        Assert.False(File.Exists(path));
    }

    /// <summary>The ceiling reaches the caller from a materialised export too, not only the streamed one —
    /// and it is the OR across the grids, since any truncated sheet makes the workbook partial.</summary>
    [SkippableFact]
    public async Task A_capped_workbook_says_it_was_capped()
    {
        var host = await LiveHostAsync();
        var path = Path.Combine(_root, "capped.xlsx");

        var response = (ExportResponse)await host.QueryAsync(
            Run("agent-reads", "select film_id from film order by film_id; select actor_id from actor order by actor_id", maxRows: 3)
                with { OutPath = path },
            CancellationToken.None);

        Assert.Equal(2, response.Results);
        Assert.Equal(6, response.Rows);
        Assert.True(response.Truncated);
    }

    /// <summary>
    /// <c>explain</c> plans without running, and <c>--analyze</c> runs inside a transaction that is rolled
    /// back. The second is what keeps "explain this" from being a write — a plain SELECT can call a volatile
    /// function that writes — so the flag on the response is the thing to assert, not merely that a plan
    /// came back.
    /// </summary>
    [SkippableFact]
    public async Task Explain_plans_without_running_and_analyze_runs_inside_a_rollback()
    {
        var host = await LiveHostAsync();
        const string sql = "select title from film where film_id = 1";

        var planned = (PlanResponse)await host.ExplainAsync(Run("agent-reads", sql), CancellationToken.None);
        Assert.False(planned.Analyzed);
        Assert.False(planned.RolledBack);
        Assert.Null(planned.ExecutionMs);        // nothing ran, so there is no execution time
        Assert.NotEmpty(planned.Hotspots);

        var measured = (PlanResponse)await host.ExplainAsync(
            Run("agent-reads", sql) with { Analyze = true }, CancellationToken.None);
        Assert.True(measured.Analyzed);
        Assert.True(measured.RolledBack);
        Assert.NotNull(measured.ExecutionMs);
    }

    /// <summary>
    /// A path that cannot be written is reported as the file's failure, and — the half that only the log can
    /// show — is <b>not recorded as a failed execution</b>. The statement ran; an audit row saying it failed
    /// with "Could not find a part of the path" is a false record of what happened on the server (§1.11d).
    /// </summary>
    [SkippableFact]
    public async Task An_export_to_an_unwritable_path_is_not_recorded_as_a_failed_statement()
    {
        var host = await LiveHostAsync();
        var path = Path.Combine(_root, "no-such-directory", "report.csv");
        var before = _log.Entries.Count;

        var failed = await Assert.ThrowsAsync<CommandFailure>(() => host.QueryAsync(
            Run("agent-reads", "select film_id from film limit 5") with { OutPath = path },
            CancellationToken.None));

        Assert.Contains("Could not write", failed.Message);
        Assert.DoesNotContain("could not be run", failed.Message);

        // Nothing about a statement that ran fine. A refusal *is* logged (§1.11d) and a failed read is too;
        // a full disk is neither.
        Assert.Equal(before, _log.Entries.Count);
    }

    /// <summary>
    /// An array column, against the real driver. The unit tests hand <c>CellValue</c> a <c>string[]</c> they
    /// made themselves; only this says the type Npgsql actually produces for <c>text[]</c> lands in the same
    /// arm. It did not: every array column answered with the literal string "System.String[]" — the value
    /// gone rather than formatted oddly, in the output a caller parses.
    /// </summary>
    [SkippableFact]
    public async Task An_array_column_reaches_the_caller_as_an_array()
    {
        var host = await LiveHostAsync();

        var response = (QueryResponse)await host.QueryAsync(
            Run("agent-reads", "select special_features from film where film_id = 1"),
            CancellationToken.None);

        var cell = response.Results[0].Rows[0][0];
        var features = Assert.IsAssignableFrom<IEnumerable<object?>>(cell);
        Assert.Contains("Deleted Scenes", features.Select(f => f?.ToString()));

        // And through the serializer, because a value that is right in the record and wrong in the JSON is
        // still wrong — this is the text the caller receives.
        var json = CliRunner.Serialize(response);
        Assert.DoesNotContain("System.String", json);
        Assert.Contains("\"Deleted Scenes\"", json);
    }

    /// <summary>
    /// The two fences that are not read-only, against a live server. Both are plain SELECTs a read-only
    /// session runs perfectly happily, which is the whole reason they need a fence of their own.
    /// </summary>
    [SkippableFact]
    public async Task A_read_that_reaches_past_the_data_is_refused_before_it_is_sent()
    {
        var host = await LiveHostAsync();

        // A comment is whitespace to Postgres and not to a regex: this ran, and returned the file, against
        // the text match the scan used to be.
        var hidden = await Assert.ThrowsAsync<CommandFailure>(() => host.QueryAsync(
            Run("agent-reads", "select pg_read_file/**/('/etc/passwd')"), CancellationToken.None));
        Assert.Contains("pg_read_file", hidden.Message);

        // §1.8's catalogs. The connection here is a superuser, which is exactly the case the fence is for.
        var credentials = await Assert.ThrowsAsync<CommandFailure>(() => host.QueryAsync(
            Run("agent-reads", "select rolname, rolpassword from pg_authid"), CancellationToken.None));
        Assert.Contains("pg_authid", credentials.Message);

        // And the read that replaces it still works, so the refusal is about the hash rather than the topic.
        var roles = (QueryResponse)await host.QueryAsync(
            Run("agent-reads", "select rolname from pg_roles order by rolname limit 3"), CancellationToken.None);
        Assert.NotEmpty(roles.Results[0].Rows);
    }

    /// <summary>
    /// <c>--timeout 0</c> is "no limit" in both engines, so a non-positive ask is a request to *remove* the
    /// owner's timeout — the inversion <c>WithTimeout</c> exists to prevent, arriving as a smaller number
    /// rather than a larger one. The parser refuses it today, but this method owns the rule and
    /// <c>IBearingHost</c> is public, so the guarantee is asserted where it is stated.
    /// </summary>
    [SkippableFact]
    public async Task A_caller_cannot_remove_the_timeout_by_asking_for_zero()
    {
        var host = await LiveHostAsync();

        Assert.Equal($"{SessionPolicy.PresetTimeoutSeconds}s", await TimeoutWith(host, 0));
        Assert.Equal($"{SessionPolicy.PresetTimeoutSeconds}s", await TimeoutWith(host, -1));
    }

    /// <summary>A project with one exposed connection and one that is not.</summary>
    private async Task<string> WriteProjectAsync()
    {
        var directory = Path.Combine(_root, "project");
        var store = new JsonProjectStore();
        var project = await store.CreateAsync(directory, "Agent", CancellationToken.None);

        project.Manifest.Connections.Add(new ConnectionInfo
        {
            Id = _connectionId,
            Name = "agent-reads",
            ProviderId = PostgresProvider.ProviderId,
            Host = PgTestServer.Host,
            Port = PgTestServer.Port,
            Database = PgTestServer.Database,
            User = PgTestServer.User,
            ExternalAccess = ExternalAccess.ReadOnly,
        });

        project.Manifest.Connections.Add(new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = "private-reads",
            ProviderId = PostgresProvider.ProviderId,
            Host = PgTestServer.Host,
            Port = PgTestServer.Port,
            Database = PgTestServer.Database,
            User = PgTestServer.User,
        });

        await store.SaveAsync(project, CancellationToken.None);
        return directory;
    }

    /// <summary>One statement, as the request every run command takes.</summary>
    private static RunRequest Run(string connection, string sql, int? maxRows = null)
        => new(connection, sql) { MaxRows = maxRows };

    /// <summary>
    /// The first cell, as text. Reading the record rather than serialised JSON, so the assertion is about
    /// the value the command produced and not about how it was spelled on the way out.
    /// </summary>
    private static async Task<string?> ScalarAsync(BearingHost host, string sql)
    {
        var response = (QueryResponse)await host.QueryAsync(Run("agent-reads", sql), CancellationToken.None);
        var cell = response.Results[0].Rows[0][0];
        return cell is null ? null : Convert.ToString(cell, CultureInfo.InvariantCulture);
    }
}
