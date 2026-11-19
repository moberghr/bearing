using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// The lexical half of DynamoDB's full-scan question (<see cref="PartiQlScan"/>): which table and index each
/// <c>SELECT</c> reads, and which attributes its <c>WHERE</c> pins with <c>=</c> or <c>IN</c> in a top-level
/// <c>AND</c> or in every branch of a top-level <c>OR</c>. Whether a pinned attribute is the partition key is
/// <c>ScanGuard</c>'s question; these assert only what the text says.
/// </summary>
public class PartiQlScanTests
{
    private static ScanTarget Single(string sql)
    {
        var targets = PartiQlScan.Targets(sql);

        return Assert.Single(targets);
    }

    private static void AssertPins(string sql, params string[] expected)
    {
        var target = Single(sql);
        Assert.True(target.Readable, $"unreadable: {sql}");
        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), target.PinnedAttributes);
    }

    // ---- What pins ----

    [Theory]
    [InlineData("select * from \"Orders\" where OrderID = 100")]
    [InlineData("select * from \"Orders\" where 100 = OrderID")]                // either side
    [InlineData("select * from \"Orders\" where OrderID = '100'")]
    [InlineData("select * from \"Orders\" where OrderID = -5")]
    [InlineData("select * from \"Orders\" where OrderID = ?")]
    [InlineData("select * from \"Orders\" where OrderID=?")]                   // the lexer reads `=?` as one operator
    [InlineData("select * from \"Orders\" where \"OrderID\" = 100")]
    [InlineData("SELECT * FROM \"Orders\" WHERE OrderID = 100;")]
    public void An_equality_on_an_attribute_pins_it(string sql) => AssertPins(sql, "OrderID");

    [Theory]
    [InlineData("select * from \"Orders\" where OrderID in [100, 300, 234]")]
    [InlineData("select * from \"Orders\" where OrderID IN (100, 300, 234)")]
    [InlineData("select * from \"Orders\" where OrderID in ['a']")]
    [InlineData("select * from \"Orders\" where OrderID in [?, ?]")]
    public void In_with_a_list_or_a_parenthesised_list_pins_it(string sql) => AssertPins(sql, "OrderID");

    [Theory]
    [InlineData("select * from \"Orders\" where OrderID = 100 and Total > 500", "OrderID")]
    [InlineData("select * from \"Orders\" where Total > 500 and OrderID = 100", "OrderID")]
    [InlineData("select * from \"Orders\" where OrderID = 100 and Total between 1 and 9 and Region = 'eu'",
        "OrderID", "Region")]
    [InlineData("select * from \"Orders\" where OrderID = 100 and begins_with(Address, '7600')", "OrderID")]
    [InlineData("select * from \"Orders\" where (OrderID = 100) and not (Total > 500)", "OrderID")]
    public void An_and_unions_what_its_conjuncts_pin(string sql, params string[] expected) => AssertPins(sql, expected);

    [Theory]
    [InlineData("select * from \"Orders\" where OrderID = 1 or OrderID = 2", "OrderID")]
    [InlineData("select * from \"Orders\" where (OrderID = 1 and Total > 5) or (OrderID = 2)", "OrderID")]
    [InlineData("select * from \"Orders\" where (OrderID = 1 or OrderID in [3, 4]) and Total > 5", "OrderID")]
    [InlineData("select * from \"Orders\" where (a = 1 and b = 2) or (b = 3 and a = 4)", "a", "b")]
    public void An_or_pins_only_what_every_branch_pins(string sql, params string[] expected)
        => AssertPins(sql, expected);

    /// <summary><c>=</c> with no space either side is the same comparison — the lexer reads <c>=1</c> as an
    /// operator and a number, as it reads <c>=?</c> as one operator above.</summary>
    [Theory]
    [InlineData("select * from \"Orders\" where pk=1")]
    [InlineData("select * from \"Orders\" where pk='a'")]
    [InlineData("select * from \"Orders\" where \"pk\"=1")]
    public void An_equality_written_without_spaces_pins_it(string sql) => AssertPins(sql, "pk");

    /// <summary>AWS's own example of an OR that still queries (ql-reference.select.html, checked 2026-11-19):
    /// every branch pins the same partition key.</summary>
    [Fact]
    public void An_or_of_equalities_on_the_same_key_pins_it_as_aws_documents()
        => AssertPins("SELECT * FROM \"Orders\" WHERE OrderID = 100 OR OrderID = 200", "OrderID");

    /// <summary>AND binds tighter than OR, with no parentheses to say so: <c>a and b or c</c> is
    /// <c>(a and b) or c</c>, and the branch <c>c</c> alone pins nothing the other does — so nothing is pinned,
    /// whichever side the key is written on.</summary>
    [Theory]
    [InlineData("select * from \"Orders\" where pk = 1 and x = 2 or y = 3")]
    [InlineData("select * from \"Orders\" where y = 3 or pk = 1 and x = 2")]
    [InlineData("select * from \"Orders\" where pk = 1 and x = 2 or pk = 3 and y = 4 or z = 5")]
    public void And_binds_tighter_than_or_so_a_branch_without_the_key_pins_nothing(string sql) => AssertPins(sql);

    [Fact]
    public void And_binds_tighter_than_or_so_every_branch_holding_the_key_pins_it()
        => AssertPins("select * from \"Orders\" where pk = 1 and x = 2 or pk = 3 and y = 4", "pk");

    // ---- What pins nothing ----

    [Theory]
    [InlineData("select * from \"Orders\" where OrderID = 100 or pk = 200")]   // AWS: a key in one branch scans
    [InlineData("select * from \"Orders\" where pk = 1 or other = 2")]
    [InlineData("select * from \"Orders\" where Total > 500")]                 // AWS: a scan
    [InlineData("select * from \"Orders\" where OrderID < 100")]
    [InlineData("select * from \"Orders\" where OrderID <> 100")]
    [InlineData("select * from \"Orders\" where OrderID between 1 and 9")]
    [InlineData("select * from \"Orders\" where begins_with(OrderID, 'a')")]
    [InlineData("select * from \"Orders\" where not OrderID = 100")]
    [InlineData("select * from \"Orders\" where not (OrderID = 100)")]
    [InlineData("select * from \"Orders\" where OrderID not in [1, 2]")]
    [InlineData("select * from \"Orders\" where OrderID = Total")]             // attribute to attribute
    [InlineData("select * from \"Orders\" where OrderID = \"Total\"")]         // a double-quoted name, not a string
    [InlineData("select * from \"Orders\" where OrderID in [1, Total]")]
    [InlineData("select * from \"Orders\" where Address.city = 'x'")]          // a path, never a key
    [InlineData("select * from \"Orders\" where OrderID is not missing")]
    public void Other_comparisons_pin_nothing(string sql) => AssertPins(sql);

    [Theory]
    [InlineData("select * from \"Orders\"")]
    [InlineData("select OrderID, Total from \"Orders\" order by OrderID desc")]
    public void No_where_pins_nothing_and_is_still_readable(string sql) => AssertPins(sql);

    // ---- Whether there is a WHERE at all ----

    /// <summary>No <c>WHERE</c> clause at all: the projection, an index target and an <c>ORDER BY</c> do not
    /// change it.</summary>
    [Theory]
    [InlineData("select * from \"Orders\"")]
    [InlineData("SELECT * FROM Orders;")]
    [InlineData("select OrderID, Total from \"Orders\" order by OrderID desc")]
    [InlineData("select * from \"Orders\".\"CustomerIndex\"")]
    [InlineData("select Address['city'], size(Items) from \"Orders\"")]
    [InlineData("select * from \"Orders\" -- where OrderID = 1")]
    [InlineData("select * /* where */ from \"Orders\" /* where OrderID = 1 */")]
    [InlineData("select \"where\" from \"Orders\"")]
    [InlineData("select 'where x = 1' from \"Orders\"")]
    [InlineData("select * from \"where\"")]
    public void A_select_with_no_where_clause_is_unfiltered(string sql)
    {
        var target = Single(sql);
        Assert.True(target.Readable, $"unreadable: {sql}");
        Assert.False(target.HasWhere);
    }

    [Theory]
    [InlineData("select * from \"Orders\" where OrderID = 100")]
    [InlineData("select * from \"Orders\" where Total > 500")]
    [InlineData("select * from \"Orders\" WHERE Total > 500 order by OrderID")]
    [InlineData("select * from \"Orders\".\"CustomerIndex\" where CustomerID = 'c1'")]
    [InlineData("select * from \"Orders\" where \"where\" = 'where'")]
    public void A_select_with_a_where_clause_is_filtered(string sql)
    {
        var target = Single(sql);
        Assert.True(target.Readable, $"unreadable: {sql}");
        Assert.True(target.HasWhere);
    }

    /// <summary>A target the reduction could not read never claims to be unfiltered, and neither does one built
    /// without saying — the default is "filtered", so nobody's first page runs unasked by omission.</summary>
    [Theory]
    [InlineData("select * from \"T\" t")]
    [InlineData("select * from \"T\" limit 5")]
    [InlineData("select * from \"T\" where pk in (select pk from \"U\")")]
    public void An_unreadable_target_is_never_unfiltered(string sql)
    {
        var target = Single(sql);
        Assert.False(target.Readable);
        Assert.True(target.HasWhere);
    }

    [Fact]
    public void A_target_built_without_saying_counts_as_filtered()
        => Assert.True(new ScanTarget("select * from \"T\"", "T", null, [], Readable: true).HasWhere);

    [Fact]
    public void Each_select_in_a_batch_says_for_itself_whether_it_has_a_where()
    {
        var targets = PartiQlScan.Targets("select * from \"A\"; select * from \"B\" where x > 2; select * from \"C\"");
        Assert.Equal([false, true, false], targets.Select(t => t.HasWhere));
    }

    // ---- Targets ----

    [Fact]
    public void The_table_is_read_unquoted()
    {
        var target = Single("select * from \"Orders\" where OrderID = 1");
        Assert.Equal("Orders", target.Table);
        Assert.Null(target.Index);
    }

    [Fact]
    public void A_bare_table_name_keeps_its_case()
        => Assert.Equal("Orders", Single("select * from Orders").Table);

    [Fact]
    public void A_table_dot_index_names_the_index()
    {
        var target = Single("select * from \"Orders\".\"CustomerIndex\" where CustomerID = 'c1'");
        Assert.True(target.Readable);
        Assert.Equal("Orders", target.Table);
        Assert.Equal("CustomerIndex", target.Index);
        Assert.Equal(["CustomerID"], target.PinnedAttributes);
    }

    [Fact]
    public void Doubled_quotes_in_a_name_are_unescaped()
    {
        var target = Single("select * from \"We\"\"ird\".\"by \"\"x\"\"\" where \"a\"\"b\" = 1");
        Assert.Equal("We\"ird", target.Table);
        Assert.Equal("by \"x\"", target.Index);
        Assert.Equal(["a\"b"], target.PinnedAttributes);
    }

    [Fact]
    public void Attribute_names_keep_their_case()
        => AssertPins("select * from \"T\" where orderId = 1 and \"OrderID\" = 2", "OrderID", "orderId");

    [Fact]
    public void The_projection_may_hold_lists_paths_and_calls()
    {
        var target = Single("select Address['city'], Address.zip, size(Items) from \"Orders\" where OrderID = 1");
        Assert.True(target.Readable);
        Assert.Equal("Orders", target.Table);
        Assert.Equal(["OrderID"], target.PinnedAttributes);
    }

    // ---- Lexing ----

    [Fact]
    public void Keywords_inside_a_string_literal_are_text()
    {
        var target = Single("select * from \"T\" where pk = 'x or y from z where (' and note = 'it''s'");
        Assert.True(target.Readable);
        Assert.Equal("T", target.Table);
        Assert.Equal(["note", "pk"], target.PinnedAttributes);
    }

    [Fact]
    public void Comments_are_not_read()
    {
        var target = Single("""
            -- select * from "Other"
            select * /* from "Hidden" */ from "T"
            where pk = 1 -- or x = 2
            """);
        Assert.Equal("T", target.Table);
        Assert.Equal(["pk"], target.PinnedAttributes);
    }

    [Fact]
    public void Each_select_in_a_batch_is_one_target()
    {
        var targets = PartiQlScan.Targets("select * from \"A\" where pk = 1; select * from \"B\"; select * from \"C\" where x > 2");
        Assert.Equal(["A", "B", "C"], targets.Select(t => t.Table));
        Assert.Equal(["pk"], targets[0].PinnedAttributes);
        Assert.Empty(targets[1].PinnedAttributes);
        Assert.Empty(targets[2].PinnedAttributes);
        Assert.Equal("select * from \"A\" where pk = 1;", targets[0].Statement);
    }

    [Theory]
    [InlineData("insert into \"T\" value {'pk': 1}")]
    [InlineData("update \"T\" set x = 1 where pk = 1")]
    [InlineData("delete from \"T\" where pk = 1")]
    [InlineData("exists(select * from \"T\" where pk = 1)")]
    public void Statements_that_are_not_selects_are_not_returned(string sql)
        => Assert.Empty(PartiQlScan.Targets(sql));

    [Fact]
    public void A_write_beside_a_select_is_skipped_and_the_select_kept()
    {
        var targets = PartiQlScan.Targets("delete from \"T\" where pk = 1; select * from \"T\"");
        Assert.Equal("T", Assert.Single(targets).Table);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-- only a comment")]
    public void Nothing_to_read_is_an_empty_list(string sql) => Assert.Empty(PartiQlScan.Targets(sql));

    // ---- Unreadable ----

    [Theory]
    [InlineData("select 1")]                                                    // no FROM
    [InlineData("select * from")]                                               // no target
    [InlineData("select * from \"T\" where pk in (select pk from \"U\")")]     // a subquery
    [InlineData("select * from \"T\" where (pk = 1")]                          // unbalanced
    [InlineData("select * from \"T\" where pk = [1)")]                          // mismatched
    [InlineData("select * from \"T\" where")]                                   // an empty condition
    [InlineData("select * from \"T\" where pk = 1 and")]                        // a dangling AND
    [InlineData("select * from \"T\" where or pk = 1")]
    [InlineData("select * from \"T\" where pk = 1 or ()")]
    [InlineData("select * from \"T\", \"U\" where pk = 1")]                    // a second source
    [InlineData("select * from \"T\" t where pk = 1")]                         // an alias
    [InlineData("select * from \"T\" limit 5")]                                 // not PartiQL; not followed
    public void A_statement_it_cannot_follow_is_unreadable(string sql)
    {
        var target = Single(sql);
        Assert.False(target.Readable);
        Assert.Empty(target.PinnedAttributes);
    }

    [Fact]
    public void An_unreadable_target_keeps_the_table_it_had_read()
    {
        var target = Single("select * from \"T\".\"Idx\" t where pk = 1");
        Assert.False(target.Readable);
        Assert.Equal("T", target.Table);
        Assert.Equal("Idx", target.Index);
    }

    /// <summary>
    /// Text the PostgreSQL lexer reads differently from DynamoDB: the lexer's statements are not the ones that
    /// would be sent, so the whole batch is one unreadable target — even when the lexer saw no SELECT at all.
    /// </summary>
    [Theory]
    [InlineData("select * from \"T\" where pk = $$x$$")]
    [InlineData("select * from \"T\" where pk = E'x\\'")]
    [InlineData("/* /* */ select * from \"Big\"; /* */")]
    [InlineData("select * from \"T\" where pk = 'unterminated")]
    [InlineData("select * from \"T\" where pk = `1`")]
    [InlineData("select * from \"T\" where pk = 1 \\ ; select * from \"Big\"\nselect 1 from \"T\"")]
    [InlineData("@set pk = 1; select * from \"Big\"\nselect * from \"T\" where pk = 1")]
    public void A_batch_the_lexer_would_misread_is_one_unreadable_target(string sql)
    {
        var target = Single(sql);
        Assert.False(target.Readable);
        Assert.Null(target.Table);
        Assert.Equal(sql.Trim(), target.Statement);
    }

    // ---- The documented AWS examples (ql-reference.select.html) ----

    [Theory]
    [InlineData("SELECT OrderID, Total FROM \"Orders\" WHERE OrderID IN [1, 2, 3] ORDER BY OrderID DESC", true)]
    [InlineData("SELECT * FROM \"Orders\" WHERE OrderID = 100", true)]
    [InlineData("SELECT * FROM \"Orders\" WHERE OrderID = 100 AND Total > 500", true)]
    [InlineData("SELECT * FROM \"Orders\" WHERE OrderID IN [100, 300, 234]", true)]
    [InlineData("SELECT * FROM \"Orders\" WHERE Total > 500", false)]
    [InlineData("SELECT * FROM \"Orders\" WHERE OrderID = 100 OR pk = 200", false)]
    [InlineData("SELECT * FROM \"Orders\" WHERE Total BETWEEN 500 AND 600", false)]
    public void The_documented_examples_pin_the_key_only_where_aws_says_they_query(string sql, bool keyed)
    {
        var target = Single(sql);
        Assert.True(target.Readable);
        Assert.Equal("Orders", target.Table);
        Assert.Equal(keyed, target.PinnedAttributes.Contains("OrderID"));
    }

    [Fact]
    public void The_dialect_answers_with_this_reduction()
    {
        var targets = PartiQlDialect.Instance.ScanTargets("select * from \"T\" where pk = 1");
        Assert.NotNull(targets);
        Assert.Equal(["pk"], Assert.Single(targets).PinnedAttributes);
    }
}
