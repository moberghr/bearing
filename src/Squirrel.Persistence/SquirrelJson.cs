using System.Text.Json;

namespace Squirrel.Persistence;

/// <summary>Shared JSON settings: indented + camelCase for clean, git-friendly diffs.</summary>
internal static class SquirrelJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
