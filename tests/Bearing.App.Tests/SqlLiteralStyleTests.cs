using System;
using Bearing.App.Formatting;
using Bearing.App.Results;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The literal forms that differ between the two engines, and the display form that had to be checked
/// because a second engine returns a type Postgres rarely does.
/// <para>
/// This is the display-only side of §5.4 — the write path itself stays parameterized — but two of these are
/// executed: the foreign-key lookup is built as text, and so is the inline-edit preview the user reads
/// before committing. A literal that is a syntax error on the target engine is a broken feature, and one
/// that is <em>valid but different</em> (a quoted <c>0x01</c> is the string "0x01") is worse.
/// </para>
/// </summary>
public class SqlLiteralStyleTests
{
    [Fact]
    public void Postgres_is_the_default_style()
    {
        // Every caller that legitimately has no connection to ask keeps exactly the old output.
        Assert.Equal(SqlValue.Literal(SqlLiteralStyle.Postgres, true), SqlValue.Literal(true));
        Assert.Equal("true", SqlValue.Literal(true));
        Assert.Equal("false", SqlValue.Literal(false));
    }

    [Fact]
    public void T_sql_has_no_boolean_literal()
    {
        // `flag = true` is a syntax error in T-SQL; a bit compares against 1/0.
        Assert.Equal("1", SqlValue.Literal(SqlLiteralStyle.TSql, true));
        Assert.Equal("0", SqlValue.Literal(SqlLiteralStyle.TSql, false));
    }

    [Fact]
    public void Binary_is_each_engines_own_constant_and_never_an_array_literal()
    {
        var bytes = new byte[] { 1, 2, 255 };

        // The bug the second engine would have exposed: a byte[] is an Array, so an arm order that let it
        // fall through would render varbinary as the Postgres array literal '{1,2,255}'.
        Assert.Equal(@"'\x0102ff'", SqlValue.Literal(SqlLiteralStyle.Postgres, bytes));
        Assert.Equal("0x0102FF", SqlValue.Literal(SqlLiteralStyle.TSql, bytes));
        Assert.DoesNotContain("{", SqlValue.Literal(SqlLiteralStyle.TSql, bytes));
    }

    [Fact]
    public void Everything_the_two_engines_agree_on_is_rendered_once()
    {
        // Quoting, ISO dates and invariant numbers are shared, which is why the style is a two-member enum
        // and not a second renderer. Asserted so a future arm can't quietly fork them.
        var when = new DateTime(2026, 8, 11, 14, 3, 22, DateTimeKind.Unspecified);
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        foreach (var style in new[] { SqlLiteralStyle.Postgres, SqlLiteralStyle.TSql })
        {
            Assert.Equal("'2026-08-11 14:03:22'", SqlValue.Literal(style, when));
            Assert.Equal("'11111111-2222-3333-4444-555555555555'", SqlValue.Literal(style, id));
            Assert.Equal("9.5", SqlValue.Literal(style, 9.5m));
            Assert.Equal("null", SqlValue.Literal(style, null));
            Assert.Equal("'O''Brien'", SqlValue.Literal(style, "O'Brien"));
        }
    }

    [Fact]
    public void The_grid_shows_a_byte_array_as_hex_not_as_an_array()
    {
        // The display side of the same arm-order question. varbinary arrives as byte[] from SqlClient, and
        // "{1, 2, 255}" would be a plausible-looking lie about the value.
        Assert.Equal(@"\x0102ff", CellFormat.Display(new byte[] { 1, 2, 255 }));
        Assert.DoesNotContain("[", CellFormat.Display(new byte[] { 1, 2, 255 }));
        // A long value is capped with its real length rather than truncated silently.
        Assert.Contains("(40 bytes)", CellFormat.Display(new byte[40]));
    }
}
