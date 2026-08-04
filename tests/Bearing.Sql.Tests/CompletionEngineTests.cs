using Bearing.Core.Completion;
using Xunit;

namespace Bearing.Sql.Tests;

public class CompletionEngineTests
{
    private static readonly CompletionEngine Engine = new();
    private static readonly Bearing.Core.Schema.SchemaSnapshot Schema = TestSchema.Build();

    [Fact]
    public void After_from_the_caret_is_a_table_position()
    {
        var intents = Engine.IntentsAt("select * from u", caretOffset: 15);
        Assert.Contains(CompletionIntent.TablePosition, intents);
    }

    [Fact]
    public void After_from_offers_the_schema_tables()
    {
        var result = Engine.Complete("select * from u", caretOffset: 15, Schema);
        var tables = result.Suggestions.Where(s => s.Kind is SuggestionKind.Table).Select(s => s.DisplayText).ToList();
        Assert.Contains("users", tables);
        Assert.Contains("orders", tables);
    }

    [Fact]
    public void In_the_select_list_the_caret_is_a_column_position()
    {
        var intents = Engine.IntentsAt("select id from users", caretOffset: 9);
        Assert.Contains(CompletionIntent.ColumnPosition, intents);
    }

    [Fact]
    public void Select_list_offers_columns()
    {
        var result = Engine.Complete("select id from users", caretOffset: 9, Schema);
        var cols = result.Suggestions.Where(s => s.Kind is SuggestionKind.Column).Select(s => s.DisplayText).ToList();
        Assert.Contains("id", cols);
        Assert.Contains("email", cols);
    }

    [Fact]
    public void Statement_start_offers_the_select_keyword()
    {
        var result = Engine.Complete("sel", caretOffset: 3, Schema);
        var keywords = result.Suggestions.Where(s => s.Kind is SuggestionKind.Keyword).Select(s => s.DisplayText).ToList();
        Assert.Contains("SELECT", keywords);
    }

    [Fact]
    public void Editing_a_word_sets_the_replacement_span_to_that_word()
    {
        // "select * from us" — caret at end of the partial identifier "us".
        var result = Engine.Complete("select * from us", caretOffset: 16, Schema);
        Assert.Equal(14, result.ReplacementStart);
        Assert.Equal(2, result.ReplacementLength);
    }

    [Fact]
    public void Inserting_in_whitespace_yields_empty_replacement_span()
    {
        var result = Engine.Complete("select * from ", caretOffset: 14, Schema);
        Assert.Equal(14, result.ReplacementStart);
        Assert.Equal(0, result.ReplacementLength);
    }
}
