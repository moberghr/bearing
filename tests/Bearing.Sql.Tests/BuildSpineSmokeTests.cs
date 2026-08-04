using Antlr4.Runtime;
using Antlr4CodeCompletion.Core.CodeCompletion;
using Bearing.Sql;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// M0 smoke tests: prove the ANTLR build spine works end to end — the PostgreSQL grammar
/// generates a lexer/parser, and the vendored antlr4-c3 core links against it and runs.
/// </summary>
public class BuildSpineSmokeTests
{
    [Fact]
    public void Lexes_select_one()
    {
        var tokens = PgParsing.LexAll("select 1");

        // First on-channel token is the SELECT keyword; an integer literal "1" appears.
        var onChannel = tokens.Where(t => t.Channel == TokenConstants.DefaultChannel
                                          && t.Type != TokenConstants.EOF).ToList();

        Assert.Equal("select", onChannel[0].Text, ignoreCase: true);
        Assert.Contains(onChannel, t => t.Text == "1");
    }

    [Fact]
    public void Parses_select_to_root_without_throwing()
    {
        var parsed = PgParsing.Create("select 1");
        var tree = parsed.Parser.root();
        Assert.NotNull(tree);
    }

    [Fact]
    public void CodeCompletionCore_runs_against_generated_parser()
    {
        var parsed = PgParsing.Create("select ");
        // Prime the parser over the statement so c3 has a token stream to walk.
        parsed.Parser.root();
        parsed.Parser.Reset();

        var core = new CodeCompletionCore(
            parsed.Parser,
            preferredRules: new HashSet<int> { PostgreSQLParser.RULE_table_ref, PostgreSQLParser.RULE_columnref },
            ignoredTokens: new HashSet<int>());

        // Caret right after "select " (token index 1 on the default channel).
        var candidates = core.CollectCandidates(caretTokenIndex: 1, context: null);

        Assert.NotNull(candidates);
        // At minimum the walk completes and yields *some* candidate tokens or rules.
        Assert.True(candidates.Tokens.Count + candidates.Rules.Count > 0);
    }
}
