using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Data;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>PendingWriteStatements turns a result set's pending state into kind-tagged, ordered SQL
/// (deletes, then updates, then inserts) — the statements the save confirmation lists before committing.
/// Pure — no database needed.</summary>
public class CommitScriptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-commit", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private ShellViewModel NewVm() => new(
        new ProviderRegistry(),
        new JsonProjectStore(),
        new JsonSessionStore(),
        new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
        new FileRecentProjects(Path.Combine(_root, "recent.json")),
        new FileFallbackSecretStore(Path.Combine(_root, "secrets")));

    private static ResultSetViewModel EditableResultWith(Action<ResultSetViewModel> stage)
    {
        var cols = new[]
        {
            new ColumnDescriptor("id", "int4", typeof(int), 1u, 1),
            new ColumnDescriptor("total", "numeric", typeof(decimal), 1u, 2),
        };
        var rows = new List<object?[]> { new object?[] { 1, 9.99m }, new object?[] { 2, 5.00m } };
        var qr = new QueryResult(cols, rows, rows.Count, TimeSpan.Zero, null, null, Truncated: false);
        var rs = new ResultSetViewModel(qr, "select id, total from orders", pageable: false)
        {
            EditTarget = new EditTarget("public", "orders", new[]
            {
                new EditableColumn(0, "id", IsPrimaryKey: true),
                new EditableColumn(1, "total", IsPrimaryKey: false),
            }),
        };
        rs.CaptureOriginals();
        stage(rs);
        return rs;
    }

    [Fact]
    public void Emits_delete_update_insert_in_order()
    {
        var rs = EditableResultWith(rs =>
        {
            rs.Rows[0][1] = "19.99"; rs.MarkEdited(rs.Rows[0]);   // update
            rs.ToggleDelete(rs.Rows[1]);                          // delete
            var added = rs.AddRow(); added[0] = "3"; added[1] = "7.50"; // insert
        });

        var stmts = NewVm().Execution.PendingWriteStatements(rs);

        Assert.Equal(new[] { "DELETE", "UPDATE", "INSERT" }, stmts.Select(s => s.Kind));
        Assert.All(stmts, s => Assert.EndsWith(";", s.Sql));
        Assert.Contains("update", stmts[1].Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("19.99", stmts[1].Sql);           // inlined new value
        Assert.Contains("insert", stmts[2].Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_pending_changes_yields_no_statements()
        => Assert.Empty(NewVm().Execution.PendingWriteStatements(EditableResultWith(_ => { })));
}
