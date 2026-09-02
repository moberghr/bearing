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
    public void Columns_of_an_aliased_source_insert_qualified()
    {
        // In a select list / ORDER BY / WHERE, a bare `id` is ambiguous as soon as a second source joins,
        // and the alias is how the rest of the statement names the source.
        var result = Engine.Complete("select id from users u", caretOffset: 9, Schema);
        var id = result.Suggestions.First(s => s.Kind == SuggestionKind.Column && s.DisplayText == "id");
        Assert.Equal("u.id", id.ReplacementText);
        Assert.Equal("id", id.DisplayText);       // the label and the filter stay bare
        Assert.Equal("id", id.FilterText);
    }

    [Fact]
    public void Order_by_offers_alias_qualified_columns()
    {
        // The reported case: ORDER BY offered the right names but inserted them bare.
        var sql = "select * from users u order by ";
        var result = Engine.Complete(sql, sql.Length, Schema);
        var id = result.Suggestions.FirstOrDefault(s => s.Kind == SuggestionKind.Column && s.DisplayText == "id");
        Assert.NotNull(id);
        Assert.Equal("u.id", id!.ReplacementText);
    }

    [Fact]
    public void Columns_of_an_unaliased_source_stay_bare()
    {
        var result = Engine.Complete("select id from users", caretOffset: 9, Schema);
        var id = result.Suggestions.First(s => s.Kind == SuggestionKind.Column && s.DisplayText == "id");
        Assert.Equal("id", id.ReplacementText);
    }

    [Fact]
    public void Columns_after_an_alias_dot_stay_bare()
    {
        // The qualifier is already typed there — inserting "u.id" would yield "u.u.id".
        var result = Engine.Complete("select u. from users u", caretOffset: 9, Schema);
        Assert.All(result.Suggestions.Where(s => s.Kind == SuggestionKind.Column),
            s => Assert.DoesNotContain(".", s.ReplacementText));
    }

    [Fact]
    public void Inserting_in_whitespace_yields_empty_replacement_span()
    {
        var result = Engine.Complete("select * from ", caretOffset: 14, Schema);
        Assert.Equal(14, result.ReplacementStart);
        Assert.Equal(0, result.ReplacementLength);
    }

    // ---- string literals ------------------------------------------------------------------------

    [Fact]
    public void No_suggestions_inside_a_string_literal()
    {
        // 'text' is data. Nothing in the catalog or the grammar belongs there, and a popup over it is noise
        // to dismiss on the way to typing a value.
        const string sql = "select * from users where name = 'us";

        var result = Engine.Complete(sql, sql.Length, Schema);

        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public void A_string_literal_does_not_silence_the_rest_of_the_statement()
    {
        // The gate is the caret's position, not "this statement contains a quote".
        const string sql = "select * from users where name = 'ada' and u";

        var result = Engine.Complete(sql, sql.Length, Schema);

        Assert.NotEmpty(result.Suggestions);
    }

    [Fact]
    public void A_quoted_identifier_still_gets_suggestions()
    {
        // The reason this is single-quote only: "..." is a quoted *identifier* — how you name a table with a
        // capital or a space — so it is precisely where a table name is wanted.
        const string sql = "select * from \"us";

        var result = Engine.Complete(sql, sql.Length, Schema);

        Assert.NotEmpty(result.Suggestions);
    }
}
