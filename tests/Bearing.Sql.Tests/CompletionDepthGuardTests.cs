using System;
using System.Linq;
using Bearing.Core.Completion;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// Completion declines deeply nested text instead of parsing it.
/// <para>
/// This is a crash, not a slowdown, and it predates the formatter: <c>Complete</c> has always called
/// <c>parser.root()</c> on whatever is in the editor, wrapped in <c>try { } catch { }</c> — and a
/// <see cref="StackOverflowException"/> is not catchable in .NET. The catch never runs, the process dies,
/// and the unsaved buffer goes with it. Completion runs as you type, so pasting a deeply nested expression
/// was enough to trigger it.
/// </para>
/// <para>
/// If one of these ever crashes the test host rather than failing, that is the bug back again.
/// </para>
/// </summary>
public class CompletionDepthGuardTests
{
    private static readonly CompletionEngine Engine = new();
    private static readonly Bearing.Core.Schema.SchemaSnapshot Schema = TestSchema.Build();

    private static string Nested(int depth)
        => $"select {new string('(', depth)}1{new string(')', depth)} from users";

    [Theory]
    [InlineData(PgParsing.MaxNestingDepth + 1)]
    [InlineData(5000)]
    [InlineData(50000)]
    public void Deeply_nested_text_yields_no_suggestions_rather_than_killing_the_process(int depth)
    {
        var sql = Nested(depth);

        var result = Engine.Complete(sql, caretOffset: sql.Length, Schema);

        Assert.Empty(result.Suggestions);
    }

    [Theory]
    [InlineData(PgParsing.MaxNestingDepth + 1)]
    [InlineData(50000)]
    public void The_same_holds_for_the_intent_query(int depth)
    {
        var sql = Nested(depth);
        Assert.Empty(Engine.IntentsAt(sql, caretOffset: sql.Length));
    }

    /// <summary>The guard must not cost ordinary completion anything — the depth a real query reaches is
    /// nowhere near the limit.</summary>
    [Fact]
    public void Ordinary_nesting_still_completes()
    {
        var result = Engine.Complete("select * from u", caretOffset: 15, Schema);
        Assert.NotEmpty(result.Suggestions);

        // Genuinely nested, but sanely so.
        const string nested = "select * from (select id from (select id from users) a) b where ";
        Assert.NotEmpty(Engine.IntentsAt(nested, nested.Length));
    }
}
