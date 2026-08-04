using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bearing.Persistence;

/// <summary>Shared JSON settings: indented + camelCase for clean, git-friendly diffs.</summary>
internal static class BearingJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Enums as readable strings (e.g. credentialKind: "EntraToken"). Reads legacy numeric values too,
        // and a missing property still deserializes to the enum's default — so older project files load.
        Converters = { new JsonStringEnumConverter() },
    };
}
