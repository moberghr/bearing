using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.App.Results;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Tying a result set back to the statement that produced it — what "Copy as ▸ Table with the query"
/// captions a grid with. The provider proves the mapping (<see cref="QueryResult.StatementIndex"/>, pinned
/// live in <c>Bearing.Data.Tests/StatementAttributionTests</c>); this is the half that turns a statement
/// number into its text, and the half that decides when not to.
/// </summary>
public class StatementProvenanceTests
{
    private const string Batch = """
        select 'one' as which from film;
        select 'two' as which from actor;
        select 'three' as which from category;
        """;

    private static QueryResult Set(int? statementIndex) => new(
        [new ColumnDescriptor("which", "text", typeof(string))],
        [["x"]], 1, TimeSpan.Zero, null, null, false)
    { StatementIndex = statementIndex };

    private static List<QueryResult> Batch3(int? i0 = 0, int? i1 = 1, int? i2 = 2)
        => [Set(i0), Set(i1), Set(i2)];

    [Fact]
    public void A_numbered_batch_gives_every_set_its_own_statement()
    {
        var sets = ResultSetBuilder.BuildResultSets(Batch3(), Batch, snapshot: null);

        Assert.Equal("select 'one' as which from film;", sets[0].ExecutedSql);
        Assert.Equal("select 'two' as which from actor;", sets[1].ExecutedSql);
        Assert.Equal("select 'three' as which from category;", sets[2].ExecutedSql);

        // Which is what reaches the clipboard: the second grid is captioned with the second query, and not
        // with the first — the whole point of the exercise.
        var html = CopyRenderer.Render(sets[1], TableBlock.ForResult(sets[1]), CopyFormat.HtmlWithQuery);
        Assert.Contains("from actor", html);
        Assert.DoesNotContain("from film", html);
    }

    /// <summary>
    /// A provider that can't prove which statement is which gets the whole batch on every set. Broad, and
    /// deliberately so: the alternative is numbering by position, which is exactly what a skipped statement
    /// makes wrong (see the live test).
    /// </summary>
    [Fact]
    public void An_unattributed_batch_falls_back_to_the_whole_run()
    {
        var sets = ResultSetBuilder.BuildResultSets(Batch3(null, null, null), Batch, snapshot: null);

        Assert.All(sets, s => Assert.Equal(Batch.Trim(), s.ExecutedSql));
        // One set missing its number is enough — a partly-numbered batch is not a mapping.
        var partial = ResultSetBuilder.BuildResultSets(Batch3(0, null, 2), Batch, snapshot: null);
        Assert.All(partial, s => Assert.Equal(Batch.Trim(), s.ExecutedSql));
    }

    /// <summary>
    /// The second check: our own split of the buffer has to find as many statements as there are sets. If it
    /// doesn't, index <i>i</i> doesn't mean the same statement to both sides and the texts would be
    /// misaligned — so the batch text is used instead of the wrong statement.
    /// </summary>
    [Fact]
    public void A_split_that_disagrees_with_the_run_is_not_trusted()
    {
        const string twoStatements = "select 1; select 2;";

        var sets = ResultSetBuilder.BuildResultSets(Batch3(), twoStatements, snapshot: null);

        Assert.All(sets, s => Assert.Equal(twoStatements, s.ExecutedSql));
        Assert.Null(ResultSetBuilder.StatementsBehind(Batch3(), twoStatements));
    }

    /// <summary>
    /// A single-set run keeps the text the caller passed, which is the user's statement — <b>not</b> the one
    /// the server saw. That run is the one <c>FirstPageLimiter</c> rewrites, so the executed statement ends
    /// in a <c>limit 501</c> of ours; a table pasted into a report captioned with that would claim a ceiling
    /// the user never wrote.
    /// </summary>
    [Fact]
    public void A_single_set_keeps_the_users_own_statement()
    {
        const string typed = "select title from film order by film_id";

        var sets = ResultSetBuilder.BuildResultSets([Set(0)], typed, snapshot: null);

        Assert.Equal(typed, sets[0].ExecutedSql);
        Assert.DoesNotContain("limit", sets[0].ExecutedSql);
        Assert.Null(ResultSetBuilder.StatementsBehind([Set(0)], typed));   // never even consulted
    }

    /// <summary>Paging is unaffected: it still runs against the run's own SELECT, and only a single-set run
    /// is pageable at all — so the batch attribution above can't reach it.</summary>
    [Fact]
    public void Attribution_does_not_touch_what_paging_runs_against()
    {
        const string typed = "select title from film";
        var single = ResultSetBuilder.BuildResultSets(
            [Set(0) with { Truncated = true }], typed, snapshot: null);
        Assert.True(single[0].IsPageable);
        Assert.Equal(typed, single[0].SourceSql);

        // A batch is not pageable, so the per-statement text never becomes a paging query.
        var batch = ResultSetBuilder.BuildResultSets(Batch3(), Batch, snapshot: null);
        Assert.All(batch, s => Assert.Null(s.SourceSql));
    }
}
