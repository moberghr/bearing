using System.Collections.Generic;
using System.Linq;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// <c>@set name = value</c> lines (#103): which lines are directives in each dialect, what their values mean,
/// and that writing values back as <c>@set</c> lines reads back the same values.
/// </summary>
public class ScriptDirectivesTests
{
    private static readonly ISqlDialect Pg = PostgresDialect.Instance;
    private static readonly ISqlDialect Ms = SqlServerDialect.Instance;
    private static readonly ISqlDialect My = MySqlDialect.Instance;

    public static TheoryData<string> Dialects => new() { "pg", "ms", "my" };

    private static ISqlDialect For(string id) => id switch { "pg" => Pg, "my" => My, _ => Ms };

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Lines_at_the_start_of_a_line_are_found_with_their_spans(string dialect)
    {
        const string sql = "@set id = 42\n  @SET since='2005-06-01';\nselect :id, :since";

        var found = ScriptDirectives.Find(For(dialect), sql);

        Assert.Equal(["id", "since"], found.Select(d => d.Name));
        Assert.Equal(("42", QueryParameterKind.Number), (found[0].Value, found[0].Kind));
        Assert.Equal(("2005-06-01", QueryParameterKind.Text), (found[1].Value, found[1].Kind));
        Assert.Equal("@set id = 42", sql.Substring(found[0].Start, found[0].Length));
        Assert.Equal("  @SET since='2005-06-01';", sql.Substring(found[1].Start, found[1].Length));
    }

    [Theory]
    [InlineData("pg", "select '\n@set id = 1\n'")]                 // inside a string
    [InlineData("pg", "/*\n@set id = 1\n*/ select 1")]              // inside a block comment
    [InlineData("pg", "-- @set id = 1")]                            // after a line comment marker
    [InlineData("pg", "do $$\n@set id = 1\n$$")]                    // inside a dollar-quoted body
    [InlineData("pg", "select 1 @set id = 1")]                      // not at the start of its line
    [InlineData("ms", "select '\n@set id = 1\n'")]
    [InlineData("ms", "/*\n@set id = 1\n*/ select 1")]
    [InlineData("ms", "update t set\n@set = 5")]                     // T-SQL: assigning a variable named @set
    [InlineData("my", "select '\n@set id = 1\n'")]
    [InlineData("my", "# @set id = 1")]
    [InlineData("my", "select \"\n@set id = 1\n\"")]                 // double quotes delimit a string
    [InlineData("my", "@set := 1")]                                 // a MySQL user variable assignment
    [InlineData("pg", "@set id == 1")]
    public void Lines_that_are_sql_or_inside_something_else_are_not_directives(string dialect, string sql)
        => Assert.Empty(ScriptDirectives.Find(For(dialect), sql));

    [Theory]
    [InlineData("42", "42", QueryParameterKind.Number)]
    [InlineData(" -1.5 ;", "-1.5", QueryParameterKind.Number)]
    [InlineData("01234", "01234", QueryParameterKind.Text)]        // a leading zero is a code, not a number
    [InlineData("'01234'", "01234", QueryParameterKind.Text)]
    [InlineData("2005-06-01", "2005-06-01", QueryParameterKind.Text)]
    [InlineData("'it''s'", "it's", QueryParameterKind.Text)]
    [InlineData("''", "", QueryParameterKind.Text)]
    [InlineData("", "", QueryParameterKind.Text)]
    [InlineData("'null'", "null", QueryParameterKind.Text)]
    [InlineData("'a' 'b'", "'a' 'b'", QueryParameterKind.Text)]   // quotes that don't enclose it: as written
    [InlineData("jane@example.com", "jane@example.com", QueryParameterKind.Text)]
    public void Values_read_as_text_or_number(string raw, string value, QueryParameterKind kind)
        => Assert.Equal((value, kind), ScriptDirectives.ParseValue(raw));

    [Theory]
    [InlineData("null")]
    [InlineData(" NULL; ")]
    public void Bare_null_is_sql_null(string raw)
        => Assert.Equal((null, QueryParameterKind.Null), ScriptDirectives.ParseValue(raw));

    [Fact]
    public void Rendered_values_read_back_as_the_same_values()
    {
        (string, string?, QueryParameterKind)[] values =
        [
            ("id", "42", QueryParameterKind.Number),
            ("code", "01234", QueryParameterKind.Text),
            ("n", "17", QueryParameterKind.Text),          // text that looks like a number stays text
            ("who", "O'Brien", QueryParameterKind.Text),
            ("gone", null, QueryParameterKind.Null),
            ("word", "null", QueryParameterKind.Text),
        ];

        var rendered = ScriptDirectives.Render(values);
        var read = ScriptDirectives.Find(Pg, rendered + "select 1");

        Assert.Equal(values, read.Select(d => (d.Name, d.Value, d.Kind)));
    }

    [Fact]
    public void A_value_with_a_line_break_is_left_for_the_prompt()
    {
        var rendered = ScriptDirectives.Render([("a", "x", QueryParameterKind.Text), ("b", "two\nlines", QueryParameterKind.Text)]);

        Assert.Equal("@set a = 'x'\n", rendered);
    }

    [Fact]
    public void Blanking_keeps_every_offset()
    {
        const string sql = "@set id = 42\nselect :id";
        var blanked = ScriptDirectives.Blank(sql, ScriptDirectives.Find(Pg, sql));

        Assert.Equal(sql.Length, blanked.Length);
        Assert.Equal(new string(' ', 12) + "\nselect :id", blanked);
    }
}
