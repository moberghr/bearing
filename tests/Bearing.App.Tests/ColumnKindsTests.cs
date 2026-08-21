using System;
using Bearing.App.Results;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The column-shape predicates the results grid branches on. Each drives a visible decision — a checkbox
/// instead of text, a JSON tree instead of raw text, a jump icon — so getting one wrong is a
/// rendering bug, not a cosmetic one.
/// </summary>
public class ColumnKindsTests
{
    private static ColumnDescriptor Col(string type, Type clr) => new("c", type, clr);

    [Fact]
    public void Bool_and_nullable_bool_are_checkbox_columns()
    {
        Assert.True(ColumnKinds.IsBool(Col("bool", typeof(bool))));
        Assert.True(ColumnKinds.IsBool(Col("bool", typeof(bool?))));
    }

    [Fact]
    public void Nothing_else_is_a_checkbox_column()
    {
        Assert.False(ColumnKinds.IsBool(Col("int4", typeof(int))));
        Assert.False(ColumnKinds.IsBool(Col("text", typeof(string))));
        Assert.False(ColumnKinds.IsBool(Col("_bool", typeof(bool[])))); // a bool ARRAY is not a checkbox
    }

    [Theory]
    [InlineData("json", true)]
    [InlineData("jsonb", true)]
    [InlineData("JSONB", true)]   // Postgres type names arrive lower-cased, but don't rely on it
    [InlineData("text", false)]
    [InlineData("jsonpath", false)]
    public void Json_detection_is_by_declared_type_case_insensitively(string type, bool expected)
        => Assert.Equal(expected, ColumnKinds.IsJson(type));

    [Theory]
    [InlineData("{\"a\":1}", true)]
    [InlineData("[1,2]", true)]
    [InlineData("  \n {\"a\":1}", true)]  // leading whitespace is skipped
    [InlineData("hello", false)]
    [InlineData("", false)]
    public void Looks_json_sniffs_the_first_non_space_character(string raw, bool expected)
        => Assert.Equal(expected, ColumnKinds.LooksJson(raw));
}
