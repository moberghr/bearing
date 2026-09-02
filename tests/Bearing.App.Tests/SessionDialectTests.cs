using System;
using System.IO;
using System.Threading.Tasks;
using Bearing.App.Results;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Data.SqlServer;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The dialect travels with the session. This is the headline bug the SQL Server work had to avoid: every
/// piece of generated SQL in <see cref="ExecutionViewModel"/> used to call a Postgres-bound static, so a tab
/// connected to SQL Server would have been paged with <c>limit … offset …</c> — a syntax error on every
/// scroll, on a connection that had otherwise worked.
/// <para>
/// Driven end-to-end through the view-model with the fake executor recording the SQL it was handed, because
/// the only thing worth asserting is what the <em>connection's</em> dialect produced, not what a dialect
/// produces in isolation (<c>Bearing.Sql.Tests</c> already covers that).
/// </para>
/// </summary>
public class SessionDialectTests : IDisposable
{
    private const int Page = 10;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-dialect", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private sealed record Harness(ExecutionViewModel Exec, EditorTabViewModel Tab, PageableExecutor Executor);

    /// <summary>A tab that has run <paramref name="sql"/> against a connection on
    /// <paramref name="providerId"/>. The fake provider answers for every id (it is its own registry), so the
    /// only thing that varies between the two cases below is the id on the connection — which is exactly what
    /// the dialect resolution keys off.</summary>
    private async Task<Harness> RunOnce(string providerId, string sql, int totalRows = 40)
    {
        Directory.CreateDirectory(_root);
        var executor = new PageableExecutor { TotalRows = totalRows, PageSize = Page };
        var ctx = new WorkspaceContext(
            new FakeProvider { Executor = executor },
            new JsonProjectStore(),
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            new FakeSecretStore(),
            settings: SettingsService.InMemory(new AppSettings
            {
                AutosaveMode = AutosaveMode.Off,
                ResultPageSize = Page,
                ResultFetchAllMaxRows = AppSettings.Defaults.ResultFetchAllMaxRows,
            }));
        var conn = new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = "c",
            ProviderId = providerId,
            Database = "app",
        };
        ctx.Project = new Project { Directory = _root, Manifest = new ProjectManifest { Connections = { conn } } };

        var tab = new EditorTabViewModel("t") { ConnectionId = conn.Id };
        ctx.Tabs.Add(tab);
        ctx.SelectedTab = tab;
        ctx.Status = _ => { };

        var exec = new ExecutionViewModel(ctx, dialogs: null);
        await exec.ExecuteAsync(sql);
        return new Harness(exec, tab, executor);
    }

    [Fact]
    public async Task A_postgres_tab_still_pages_with_limit_and_offset()
    {
        var h = await RunOnce("postgres", "select n from t order by n");

        await h.Exec.LoadMoreAsync(h.Tab.LastResult!);

        Assert.Contains("limit", h.Executor.LastPageSql);
        Assert.Contains("offset 10", h.Executor.LastPageSql);
        Assert.DoesNotContain("fetch next", h.Executor.LastPageSql);
    }

    [Fact]
    public async Task A_sql_server_tab_pages_with_offset_fetch_next()
    {
        var h = await RunOnce(SqlServerProvider.ProviderId, "select n from t order by n");

        await h.Exec.LoadMoreAsync(h.Tab.LastResult!);

        Assert.Contains("offset 10 rows fetch next 10 rows only", h.Executor.LastPageSql);
        // The Postgres suffix would be a syntax error here, and it is what the old static produced.
        Assert.DoesNotContain("limit", h.Executor.LastPageSql);
    }

    [Fact]
    public async Task The_first_page_limit_is_the_connections_own_clause()
    {
        // Postgres appends `limit N`; T-SQL has no bare limit, so its clause is OFFSET/FETCH — and only when
        // the query already carries a top-level ORDER BY, which this one does.
        var pg = await RunOnce("postgres", "select n from t order by n");
        Assert.Contains("limit 11", pg.Executor.LastExecuteSql);

        var ms = await RunOnce(SqlServerProvider.ProviderId, "select n from t order by n");
        Assert.Contains("offset 0 rows fetch next 11 rows only", ms.Executor.LastExecuteSql);
    }

    [Fact]
    public async Task A_sql_server_query_with_no_order_by_is_still_capped_server_side_with_top()
    {
        // T-SQL rejects OFFSET/FETCH without a top-level ORDER BY, and synthesising one would make paging
        // silently non-deterministic. TOP has no such restriction and no offset, which makes it exactly
        // right for a first page: the server produces ~one page instead of computing and streaming the
        // whole result set for the client to read 10 rows of and discard. This used to run unbounded.
        var h = await RunOnce(SqlServerProvider.ProviderId, "select n from t");

        Assert.Equal("select top (11) n from t", h.Executor.LastExecuteSql);
        Assert.DoesNotContain("offset", h.Executor.LastExecuteSql, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Postgres_keeps_its_bare_limit_for_the_same_unordered_query()
    {
        // The TOP path is the T-SQL dialect's alone — Postgres' LIMIT never needed an ORDER BY, so nothing
        // about its first page changes.
        var pg = await RunOnce("postgres", "select n from t");

        Assert.Contains("limit 11", pg.Executor.LastExecuteSql);
        Assert.DoesNotContain("top", pg.Executor.LastExecuteSql, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fetch_all_asks_for_its_window_in_the_connections_dialect()
    {
        var h = await RunOnce(SqlServerProvider.ProviderId, "select n from t order by n");

        await h.Exec.FetchAllAsync(h.Tab.LastResult!);

        Assert.Contains("fetch next", h.Executor.LastStreamSql);
        Assert.DoesNotContain("limit", h.Executor.LastStreamSql);
    }
}
