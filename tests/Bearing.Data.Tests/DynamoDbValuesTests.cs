using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Bearing.Data.DynamoDb;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// Attribute values as cells, the column layout a first page fixes, the overflow column, and the continuation
/// token. Pure — the SDK's own <see cref="AttributeValue"/> is built by hand, so this pins the mapping without
/// a container (the live proof is the spec's SC8).
/// </summary>
public class DynamoDbValuesTests
{
    private static AttributeValue S(string s) => new() { S = s };

    private static AttributeValue N(string n) => new() { N = n };

    private static AttributeValue Bool(bool b) => new() { BOOL = b };

    private static AttributeValue Null() => new() { NULL = true };

    private static AttributeValue Bin(params byte[] bytes) => new() { B = new MemoryStream(bytes) };

    private static AttributeValue Map(params (string Key, AttributeValue Value)[] entries)
        => new() { M = entries.ToDictionary(e => e.Key, e => e.Value) };

    private static AttributeValue List(params AttributeValue[] items) => new() { L = [.. items] };

    private static Dictionary<string, AttributeValue> Item(params (string Key, AttributeValue Value)[] entries)
        => entries.ToDictionary(e => e.Key, e => e.Value);

    // ---- values ------------------------------------------------------------------------------------

    [Fact]
    public void Each_scalar_type_maps_to_its_cell()
    {
        Assert.Equal("hello", DynamoDbValues.ToCell(S("hello")));
        Assert.Equal("", DynamoDbValues.ToCell(S("")));
        Assert.Equal(42m, DynamoDbValues.ToCell(N("42")));
        Assert.Equal(true, DynamoDbValues.ToCell(Bool(true)));
        Assert.Equal(false, DynamoDbValues.ToCell(Bool(false)));
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(DynamoDbValues.ToCell(Bin(1, 2, 3))));
    }

    [Fact]
    public void A_null_and_a_missing_attribute_are_both_null()
    {
        Assert.Null(DynamoDbValues.ToCell(Null()));
        Assert.Null(DynamoDbValues.ToCell(null));
        Assert.Null(DynamoDbValues.ToCell(new AttributeValue()));
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("-1.50", "-1.50")]
    [InlineData("3.14159", "3.14159")]
    [InlineData("1E+2", "100")]
    [InlineData("12345678901234567890123456", "12345678901234567890123456")]
    [InlineData("0.0000000000000000000000000001", "0.0000000000000000000000000001")]
    public void A_number_that_fits_a_decimal_exactly_is_a_decimal(string text, string shown)
    {
        var cell = Assert.IsType<decimal>(DynamoDbValues.ToCell(N(text)));

        Assert.Equal(shown, cell.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("1234567890123456789012345678901234567890")]
    [InlineData("0.12345678901234567890123456789012345678")]
    [InlineData("1E+126")]
    [InlineData("1E-130")]
    [InlineData("9.9999999999999999999999999999999999999E+125")]
    public void A_number_that_does_not_fit_a_decimal_exactly_stays_its_exact_text(string text)
        => Assert.Equal(text, Assert.IsType<string>(DynamoDbValues.ToCell(N(text))));

    [Fact]
    public void A_forty_digit_number_stays_exact_in_a_cell_and_in_json()
    {
        const string forty = "1234567890123456789012345678901234567890";

        Assert.Equal(forty, DynamoDbValues.ToCell(N(forty)));
        Assert.Equal($"[{forty}]", DynamoDbValues.ToJson(List(N(forty))));
    }

    [Fact]
    public void Maps_lists_and_sets_are_json_text()
    {
        Assert.IsType<string>(DynamoDbValues.ToCell(Map(("a", S("x")))));
        Assert.IsType<string>(DynamoDbValues.ToCell(List(N("1"))));
        Assert.IsType<string>(DynamoDbValues.ToCell(new AttributeValue { SS = ["a"] }));
        Assert.IsType<string>(DynamoDbValues.ToCell(new AttributeValue { NS = ["1"] }));
        Assert.IsType<string>(DynamoDbValues.ToCell(new AttributeValue { BS = [new MemoryStream([1])] }));
    }

    [Fact]
    public void A_map_is_json_with_keys_in_ordinal_order_and_numbers_unquoted_as_written()
    {
        var value = Map(
            ("b", N("1.50")),
            ("a", S("x\"y")),
            ("C", Bool(true)),
            ("n", Null()),
            ("blob", Bin(0xFF, 0x00)),
            ("inner", Map(("z", N("2")), ("y", List(S("p"), N("3"))))));

        var json = DynamoDbValues.ToJson(value);

        Assert.Equal(
            "{\"C\":true,\"a\":\"x\\\"y\",\"b\":1.50,\"blob\":\"/wA=\",\"inner\":{\"y\":[\"p\",3],\"z\":2},\"n\":null}",
            json);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, parsed.RootElement.ValueKind);
    }

    [Fact]
    public void The_same_item_always_reads_the_same()
    {
        var first = Map(("b", S("1")), ("a", S("2")), ("c", Map(("y", N("1")), ("x", N("2")))));
        var second = Map(("c", Map(("x", N("2")), ("y", N("1")))), ("a", S("2")), ("b", S("1")));

        Assert.Equal(DynamoDbValues.ToJson(first), DynamoDbValues.ToJson(second));
    }

    [Fact]
    public void Sets_are_arrays_sorted_so_they_read_the_same_every_time()
    {
        Assert.Equal("[\"a\",\"b\",\"c\"]", DynamoDbValues.ToJson(new AttributeValue { SS = ["c", "a", "b"] }));
        Assert.Equal("[-5,2,10.5]", DynamoDbValues.ToJson(new AttributeValue { NS = ["10.5", "-5", "2"] }));
        Assert.Equal(
            "[\"AQ==\",\"Ag==\"]",
            DynamoDbValues.ToJson(new AttributeValue { BS = [new MemoryStream([2]), new MemoryStream([1])] }));
    }

    [Fact]
    public void Special_characters_are_kept_readable_not_escaped_to_unicode()
        => Assert.Equal("{\"k\":\"čšž <é> 日本\"}", DynamoDbValues.ToJson(Map(("k", S("čšž <é> 日本")))));

    [Fact]
    public void An_empty_map_and_an_empty_list_are_still_json()
    {
        Assert.Equal("{}", DynamoDbValues.ToJson(new AttributeValue { M = [] }));
        Assert.Equal("[]", DynamoDbValues.ToJson(new AttributeValue { L = [] }));
    }

    // ---- layout ------------------------------------------------------------------------------------

    [Fact]
    public void The_layout_puts_the_keys_first_then_the_rest_alphabetically()
    {
        var page = new[]
        {
            Item(("zeta", S("z")), ("id", S("1")), ("alpha", S("a")), ("ts", N("5"))),
            Item(("id", S("2")), ("Beta", S("b")), ("ts", N("6"))),
        };

        var layout = DynamoDbValues.Layout(page, ["id", "ts"], hasOverflow: false);

        Assert.Equal(["id", "ts", "Beta", "alpha", "zeta"], layout.Columns.Select(c => c.Name));
    }

    [Fact]
    public void A_key_attribute_is_a_column_even_when_the_page_has_no_value_for_it()
    {
        var layout = DynamoDbValues.Layout([Item(("a", S("x")))], ["pk", "sk"], hasOverflow: false);

        Assert.Equal(["pk", "sk", "a"], layout.Columns.Select(c => c.Name));
    }

    [Fact]
    public void An_empty_first_page_gives_just_the_keys()
    {
        var layout = DynamoDbValues.Layout([], ["pk"], hasOverflow: false);

        Assert.Equal(["pk"], layout.Columns.Select(c => c.Name));
        Assert.Equal(DynamoDbColumnType.Text, layout.Columns[0].Type);
    }

    [Fact]
    public void A_columns_type_is_that_of_its_first_non_null_value()
    {
        var page = new[]
        {
            Item(("s", S("a")), ("n", N("1")), ("b", Bool(true)), ("x", Bin(1)), ("lazy", Null())),
            Item(("s", S("b")), ("n", N("2.5")), ("b", Bool(false)), ("x", Bin(2)), ("lazy", N("9"))),
        };

        var layout = DynamoDbValues.Layout(page, [], hasOverflow: false);
        var types = layout.Columns.ToDictionary(c => c.Name, c => c.Type);

        Assert.Equal(DynamoDbColumnType.Text, types["s"]);
        Assert.Equal(DynamoDbColumnType.Number, types["n"]);
        Assert.Equal(DynamoDbColumnType.Boolean, types["b"]);
        Assert.Equal(DynamoDbColumnType.Binary, types["x"]);
        Assert.Equal(DynamoDbColumnType.Number, types["lazy"]);
    }

    [Fact]
    public void A_mixed_column_is_text_and_shows_every_value_as_text()
    {
        var page = new[]
        {
            Item(("id", S("1")), ("v", N("7"))),
            Item(("id", S("2")), ("v", S("seven"))),
            Item(("id", S("3")), ("v", Bool(true))),
            Item(("id", S("4")), ("v", N("1.50"))),
        };

        var layout = DynamoDbValues.Layout(page, ["id"], hasOverflow: false);
        var rows = page.Select(item => DynamoDbValues.Row(item, layout)).ToList();

        Assert.Equal(DynamoDbColumnType.Text, layout.Columns[1].Type);
        Assert.Equal(["7", "seven", "true", "1.50"], rows.Select(r => r[1]));
    }

    [Fact]
    public void A_number_column_with_an_inexact_value_is_text_so_that_value_stays_exact()
    {
        const string forty = "1234567890123456789012345678901234567890";
        var page = new[] { Item(("n", N("5"))), Item(("n", N(forty))) };

        var layout = DynamoDbValues.Layout(page, [], hasOverflow: false);

        Assert.Equal(DynamoDbColumnType.Text, layout.Columns[0].Type);
        Assert.Equal(forty, DynamoDbValues.Row(page[1], layout)[0]);
    }

    [Fact]
    public void Collections_in_a_column_make_it_text_json()
    {
        var page = new[] { Item(("m", Map(("a", N("1"))))), Item(("m", List(S("x")))) };

        var layout = DynamoDbValues.Layout(page, [], hasOverflow: false);
        var rows = page.Select(item => DynamoDbValues.Row(item, layout)).ToList();

        Assert.Equal(DynamoDbColumnType.Text, layout.Columns[0].Type);
        Assert.Equal(["{\"a\":1}", "[\"x\"]"], rows.Select(r => r[0]));
    }

    [Fact]
    public void Descriptors_carry_the_clr_type_of_each_column()
    {
        var layout = DynamoDbValues.Layout(
            [Item(("s", S("a")), ("n", N("1")), ("b", Bool(true)), ("x", Bin(1)))],
            [],
            hasOverflow: false);

        var descriptors = layout.Descriptors();

        Assert.Equal([typeof(bool), typeof(decimal), typeof(string), typeof(byte[])], descriptors.Select(d => d.ClrType));
        Assert.Equal(["b", "n", "s", "x"], descriptors.Select(d => d.Name));
    }

    // ---- rows and the overflow column --------------------------------------------------------------

    [Fact]
    public void A_row_has_a_cell_per_column_and_null_for_a_missing_attribute()
    {
        var page = new[]
        {
            Item(("id", S("1")), ("n", N("5")), ("gone", S("x"))),
            Item(("id", S("2")), ("n", Null())),
        };
        var layout = DynamoDbValues.Layout(page, ["id"], hasOverflow: false);

        Assert.Equal(["id", "gone", "n"], layout.Columns.Select(c => c.Name));
        Assert.Equal(new object?[] { "1", "x", 5m }, DynamoDbValues.Row(page[0], layout));
        Assert.Equal(new object?[] { "2", null, null }, DynamoDbValues.Row(page[1], layout));
    }

    [Fact]
    public void A_result_that_fits_one_page_has_no_overflow_column()
    {
        var layout = DynamoDbValues.Layout([Item(("id", S("1")))], ["id"], hasOverflow: false);

        var descriptors = layout.Descriptors();

        Assert.DoesNotContain(descriptors, d => d.Name == DynamoDbValues.OtherAttributes);
        Assert.Single(DynamoDbValues.Row(Item(("id", S("1"))), layout));
    }

    [Fact]
    public void The_overflow_column_is_last_and_named_exactly()
    {
        var layout = DynamoDbValues.Layout([Item(("id", S("1")), ("a", S("x")))], ["id"], hasOverflow: true);

        var descriptors = layout.Descriptors();

        Assert.Equal("(other attributes)", DynamoDbValues.OtherAttributes);
        Assert.Equal(["id", "a", "(other attributes)"], descriptors.Select(d => d.Name));
        Assert.Equal(typeof(string), descriptors[^1].ClrType);
    }

    [Fact]
    public void The_overflow_column_holds_only_the_attributes_the_layout_has_no_column_for()
    {
        var layout = DynamoDbValues.Layout([Item(("id", S("1")), ("a", S("x")))], ["id"], hasOverflow: true);
        var later = Item(
            ("id", S("2")),
            ("a", S("y")),
            ("late2", Map(("k", N("1")))),
            ("late1", N("7")));

        var row = DynamoDbValues.Row(later, layout);

        Assert.Equal("2", row[0]);
        Assert.Equal("y", row[1]);
        Assert.Equal("{\"late1\":7,\"late2\":{\"k\":1}}", row[2]);
    }

    [Fact]
    public void The_overflow_cell_is_null_when_a_row_has_nothing_extra()
    {
        var layout = DynamoDbValues.Layout([Item(("id", S("1")))], ["id"], hasOverflow: true);

        var row = DynamoDbValues.Row(Item(("id", S("2"))), layout);

        Assert.Equal(new object?[] { "2", null }, row);
    }

    [Fact]
    public void A_value_that_does_not_fit_its_column_goes_to_the_overflow_column_not_into_the_grid_cell()
    {
        var layout = DynamoDbValues.Layout([Item(("flag", Bool(true)))], [], hasOverflow: true);

        var row = DynamoDbValues.Row(Item(("flag", N("3"))), layout);

        Assert.Null(row[0]);
        Assert.Equal("{\"flag\":3}", row[1]);
    }

    [Fact]
    public void A_misfit_without_an_overflow_column_keeps_its_natural_cell()
    {
        var layout = DynamoDbValues.Layout([Item(("flag", Bool(true)))], [], hasOverflow: false);

        var row = DynamoDbValues.Row(Item(("flag", N("3"))), layout);

        Assert.Equal(3m, row[0]);
    }

    // ---- continuation ------------------------------------------------------------------------------

    private static DynamoDbContinuation SampleToken()
        => new(
            "select * from \"Orders\" where \"pk\" = 'a;b'",
            "tok/en+with=odd_chars-and a space",
            new DynamoDbColumnLayout(
                [
                    new DynamoDbColumn("pk", DynamoDbColumnType.Text),
                    new DynamoDbColumn("sk", DynamoDbColumnType.Number),
                    new DynamoDbColumn("flag", DynamoDbColumnType.Boolean),
                    new DynamoDbColumn("blob", DynamoDbColumnType.Binary),
                ],
                HasOverflow: true),
            ["pk", "sk"],
            2);

    [Fact]
    public void A_continuation_round_trips()
    {
        var original = SampleToken();

        var back = DynamoDbContinuation.Decode(original.Encode());

        Assert.Equal(original.Statement, back.Statement);
        Assert.Equal(original.NextToken, back.NextToken);
        Assert.Equal(original.Layout.Columns, back.Layout.Columns);
        Assert.Equal(original.Layout.HasOverflow, back.Layout.HasOverflow);
        Assert.Equal(original.KeyAttributes, back.KeyAttributes);
        Assert.Equal(original.StatementIndex, back.StatementIndex);
    }

    [Fact]
    public void A_continuation_without_the_overflow_column_round_trips_as_such()
    {
        var token = SampleToken() with { Layout = SampleToken().Layout with { HasOverflow = false } };

        Assert.False(DynamoDbContinuation.Decode(token.Encode()).Layout.HasOverflow);
    }

    [Fact]
    public void An_encoded_continuation_is_url_safe_and_does_not_show_its_statement()
    {
        var encoded = SampleToken().Encode();

        Assert.Matches("^[A-Za-z0-9_-]+$", encoded);
        Assert.DoesNotContain("Orders", encoded, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a token")]
    [InlineData("%%%%")]
    [InlineData("e30")]
    [InlineData("bnVsbA")]
    [InlineData("W10")]
    public void Garbage_is_rejected(string text)
        => Assert.Throws<DynamoDbContinuationException>(() => DynamoDbContinuation.Decode(text));

    [Fact]
    public void A_truncated_token_is_rejected_never_half_read()
    {
        var encoded = SampleToken().Encode();

        for (var cut = 1; cut < encoded.Length; cut += 7)
        {
            var truncated = encoded[..cut];

            Assert.Throws<DynamoDbContinuationException>(() => DynamoDbContinuation.Decode(truncated));
        }
    }

    [Theory]
    [InlineData("{\"v\":2,\"s\":\"select 1\",\"t\":\"x\",\"c\":[],\"o\":false,\"k\":[],\"i\":0}")]
    [InlineData("{\"v\":1,\"t\":\"x\",\"c\":[],\"o\":false,\"k\":[],\"i\":0}")]
    [InlineData("{\"v\":1,\"s\":\"select 1\",\"c\":[],\"o\":false,\"k\":[],\"i\":0}")]
    [InlineData("{\"v\":1,\"s\":\"select 1\",\"t\":\"x\",\"o\":false,\"k\":[],\"i\":0}")]
    [InlineData("{\"v\":1,\"s\":\"select 1\",\"t\":\"x\",\"c\":[],\"k\":[],\"i\":0}")]
    [InlineData("{\"v\":1,\"s\":\"select 1\",\"t\":\"x\",\"c\":[],\"o\":false,\"i\":0}")]
    [InlineData("{\"v\":1,\"s\":\"select 1\",\"t\":\"x\",\"c\":[],\"o\":false,\"k\":[]}")]
    [InlineData("{\"v\":1,\"s\":\"select 1\",\"t\":\"x\",\"c\":[],\"o\":false,\"k\":[],\"i\":-1}")]
    [InlineData("{\"v\":1,\"s\":\"select 1\",\"t\":\"x\",\"c\":[{\"n\":\"a\",\"y\":\"Nonsense\"}],\"o\":false,\"k\":[],\"i\":0}")]
    [InlineData("{\"v\":1,\"s\":\"select 1\",\"t\":\"x\",\"c\":[{\"n\":\"a\",\"y\":\"7\"}],\"o\":false,\"k\":[],\"i\":0}")]
    [InlineData("{\"v\":1,\"s\":\"select 1\",\"t\":\"x\",\"c\":[{\"y\":\"Text\"}],\"o\":false,\"k\":[],\"i\":0}")]
    [InlineData("{\"v\":1,\"s\":\"select 1\",\"t\":\"x\",\"c\":[null],\"o\":false,\"k\":[],\"i\":0}")]
    [InlineData("{\"v\":1,\"s\":\"select 1\",\"t\":\"x\",\"c\":[],\"o\":\"yes\",\"k\":[],\"i\":0}")]
    public void A_well_formed_envelope_missing_or_mistyping_a_field_is_rejected(string json)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Throws<DynamoDbContinuationException>(() => DynamoDbContinuation.Decode(encoded));
    }

    [Fact]
    public void The_rejection_names_nothing_of_the_input()
    {
        var ex = Assert.Throws<DynamoDbContinuationException>(() => DynamoDbContinuation.Decode("secret-looking-text"));

        Assert.IsAssignableFrom<FormatException>(ex);
        Assert.DoesNotContain("secret", ex.Message, StringComparison.Ordinal);
    }

    // ---- the overflow column's name ---------------------------------------------------------------

    [Fact]
    public void A_real_attribute_named_like_the_overflow_column_does_not_collide_with_it()
    {
        var layout = DynamoDbValues.Layout(
            [Item(("pk", S("a")), (DynamoDbValues.OtherAttributes, S("real")))], ["pk"], hasOverflow: true);

        Assert.Equal(
            ["pk", DynamoDbValues.OtherAttributes, "(other attributes 2)"],
            layout.Descriptors().Select(d => d.Name));

        var row = DynamoDbValues.Row(Item(("pk", S("b")), (DynamoDbValues.OtherAttributes, S("x")), ("late", S("y"))), layout);

        Assert.Equal("x", row[1]);
        Assert.Contains("\"late\"", (string)row[2]!);
    }

    [Fact]
    public void Without_a_clash_the_overflow_column_keeps_its_name()
    {
        var layout = DynamoDbValues.Layout([Item(("pk", S("a")))], ["pk"], hasOverflow: true);

        Assert.Equal(DynamoDbValues.OtherAttributes, layout.OverflowName);
    }

    // ---- projected names fix columns ---------------------------------------------------------------

    [Fact]
    public void Projected_names_are_columns_even_when_the_first_page_is_empty()
    {
        var layout = DynamoDbValues.Layout([], ["pk"], hasOverflow: true, projected: ["total", "pk", "status"]);

        Assert.Equal(["pk", "status", "total"], layout.Columns.Select(c => c.Name));
    }
}
