using System;
using System.Linq;
using Squirrel.Core.Data;
using Squirrel.Sql;
using Xunit;

namespace Squirrel.Sql.Tests;

public class DmlGeneratorTests
{
    private static ColumnValue CV(string c, object? v) => new(c, v);

    [Fact]
    public void Update_sets_then_keys_in_parameter_order()
    {
        var cmd = DmlGenerator.Update("public", "film",
            assignments: new[] { CV("title", "Blade"), CV("release_year", 1999) },
            keys: new[] { CV("film_id", 5) });

        Assert.Equal(
            "update \"public\".\"film\" set \"title\" = @p0, \"release_year\" = @p1 where \"film_id\" = @p2",
            cmd.Sql);
        Assert.Equal(new object?[] { "Blade", 1999, 5 }, cmd.Parameters.Select(p => p.Value));
        Assert.Equal(new[] { "@p0", "@p1", "@p2" }, cmd.Parameters.Select(p => p.Name));
    }

    [Fact]
    public void Delete_by_composite_key()
    {
        var cmd = DmlGenerator.Delete("public", "film_actor",
            new[] { CV("actor_id", 1), CV("film_id", 2) });

        Assert.Equal("delete from \"public\".\"film_actor\" where \"actor_id\" = @p0 and \"film_id\" = @p1", cmd.Sql);
        Assert.Equal(new object?[] { 1, 2 }, cmd.Parameters.Select(p => p.Value));
    }

    [Fact]
    public void Insert_lists_columns_and_returns_star()
    {
        var cmd = DmlGenerator.Insert("public", "language", new[] { CV("name", "Klingon") });

        Assert.Equal("insert into \"public\".\"language\" (\"name\") values (@p0) returning *", cmd.Sql);
        Assert.Equal("Klingon", Assert.Single(cmd.Parameters).Value);
    }

    [Fact]
    public void Null_value_binds_as_parameter_on_insert()
    {
        var cmd = DmlGenerator.Insert("public", "t", new[] { CV("note", null) });
        Assert.Equal("insert into \"public\".\"t\" (\"note\") values (@p0) returning *", cmd.Sql);
        Assert.Null(Assert.Single(cmd.Parameters).Value);
    }

    [Fact]
    public void Null_key_becomes_is_null_with_no_parameter()
    {
        var cmd = DmlGenerator.Delete("public", "t", new[] { CV("k", null) });
        Assert.Equal("delete from \"public\".\"t\" where \"k\" is null", cmd.Sql);
        Assert.Empty(cmd.Parameters);
    }

    [Fact]
    public void Identifiers_are_quoted_and_escaped()
    {
        var cmd = DmlGenerator.Update(null, "we\"ird",
            assignments: new[] { CV("a\"b", 1) }, keys: new[] { CV("id", 2) });
        // No schema → unqualified; embedded quotes doubled.
        Assert.Equal("update \"we\"\"ird\" set \"a\"\"b\" = @p0 where \"id\" = @p1", cmd.Sql);
    }

    [Fact]
    public void Empty_assignments_or_keys_throw()
    {
        Assert.Throws<ArgumentException>(() =>
            DmlGenerator.Update("s", "t", Array.Empty<ColumnValue>(), new[] { CV("id", 1) }));
        Assert.Throws<ArgumentException>(() =>
            DmlGenerator.Update("s", "t", new[] { CV("a", 1) }, Array.Empty<ColumnValue>()));
        Assert.Throws<ArgumentException>(() =>
            DmlGenerator.Delete("s", "t", Array.Empty<ColumnValue>()));
        Assert.Throws<ArgumentException>(() =>
            DmlGenerator.Insert("s", "t", Array.Empty<ColumnValue>()));
    }
}
