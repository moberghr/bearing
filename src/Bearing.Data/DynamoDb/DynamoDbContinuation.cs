using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bearing.Data.DynamoDb;

/// <summary>
/// What "read on from here" needs, held inside the opaque <c>QueryResult.Continuation</c> string: the statement
/// as sent, DynamoDB's <c>NextToken</c>, the column layout the first page fixed (names, types, whether the
/// <c>(other attributes)</c> column exists), the key attributes, and which statement of the batch this is.
/// </summary>
internal sealed record DynamoDbContinuation(
    string Statement,
    string NextToken,
    DynamoDbColumnLayout Layout,
    IReadOnlyList<string> KeyAttributes,
    int StatementIndex)
{
    private const int Version = 1;

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    /// <summary>
    /// The token as an opaque base64url string. It holds statement text and a server token, so it is for memory
    /// only — never logged, persisted or shown (§1.3), which is why nothing here ever puts it in a message.
    /// </summary>
    public string Encode()
    {
        var dto = new Dto
        {
            V = Version,
            Statement = Statement,
            NextToken = NextToken,
            Columns = Layout.Columns.Select(c => new ColumnDto { Name = c.Name, Type = c.Type.ToString() }).ToList(),
            HasOverflow = Layout.HasOverflow,
            Keys = KeyAttributes.ToList(),
            Index = StatementIndex,
        };

        return Base64Url(JsonSerializer.SerializeToUtf8Bytes(dto, Json));
    }

    /// <summary>
    /// The token <paramref name="text"/> encodes. Anything that is not exactly a token this class wrote — not
    /// base64url, not JSON, a missing or mistyped field, another version — throws
    /// <see cref="DynamoDbContinuationException"/>; there is no partial result. The exception names nothing of
    /// the input.
    /// </summary>
    public static DynamoDbContinuation Decode(string text)
    {
        if (string.IsNullOrEmpty(text)) throw new DynamoDbContinuationException();

        Dto? dto;

        try
        {
            dto = JsonSerializer.Deserialize<Dto>(FromBase64Url(text), Json);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            throw new DynamoDbContinuationException();
        }

        if (dto is null
            || dto.V != Version
            || string.IsNullOrEmpty(dto.Statement)
            || string.IsNullOrEmpty(dto.NextToken)
            || dto.Columns is null
            || dto.HasOverflow is null
            || dto.Keys is null
            || dto.Index is null or < 0
            || dto.Keys.Any(k => k is null)) throw new DynamoDbContinuationException();

        var columns = new List<DynamoDbColumn>(dto.Columns.Count);

        foreach (var column in dto.Columns)
        {
            if (column?.Name is null
                || column.Type is null
                || !Enum.TryParse<DynamoDbColumnType>(column.Type, ignoreCase: false, out var type)
                || !Enum.IsDefined(type)) throw new DynamoDbContinuationException();

            columns.Add(new DynamoDbColumn(column.Name, type));
        }

        return new DynamoDbContinuation(
            dto.Statement,
            dto.NextToken,
            new DynamoDbColumnLayout(columns, dto.HasOverflow.Value),
            dto.Keys,
            dto.Index.Value);
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');

        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded,
        };

        return Convert.FromBase64String(padded);
    }

    // Nullable on purpose: a token missing a field must read as garbage, not as a default.
    private sealed class Dto
    {
        [JsonPropertyName("v")]
        public int? V { get; set; }

        [JsonPropertyName("s")]
        public string? Statement { get; set; }

        [JsonPropertyName("t")]
        public string? NextToken { get; set; }

        [JsonPropertyName("c")]
        public List<ColumnDto>? Columns { get; set; }

        [JsonPropertyName("o")]
        public bool? HasOverflow { get; set; }

        [JsonPropertyName("k")]
        public List<string>? Keys { get; set; }

        [JsonPropertyName("i")]
        public int? Index { get; set; }
    }

    private sealed class ColumnDto
    {
        [JsonPropertyName("n")]
        public string? Name { get; set; }

        [JsonPropertyName("y")]
        public string? Type { get; set; }
    }
}

/// <summary>A continuation string that is not one this provider issued. The message is fixed text: the input
/// may carry statement text and a server token, so it is never echoed.</summary>
internal sealed class DynamoDbContinuationException : FormatException
{
    public DynamoDbContinuationException()
        : base("The continuation token is not valid.")
    {
    }
}
