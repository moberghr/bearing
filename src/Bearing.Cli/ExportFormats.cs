using Bearing.Results;

namespace Bearing.Cli;

/// <summary>
/// Which export a path asks for, read from its extension.
/// <para>
/// The extension rather than a <c>--format</c> flag: a caller naming <c>report.xlsx</c> has already said
/// what they want, and a flag that could disagree with the name is a way to write a CSV called
/// <c>.xlsx</c>. An unknown extension is refused rather than defaulted, because the default would be wrong
/// exactly when someone typed the name carelessly.
/// </para>
/// </summary>
public static class ExportFormats
{
    public static ExportFormat? For(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".csv" => ExportFormat.Csv,
            ".xlsx" => ExportFormat.Xlsx,
            _ => null,
        };

    /// <summary>How a format is named in a message to the caller.</summary>
    public static string Name(ExportFormat format) => format == ExportFormat.Xlsx ? "xlsx" : "CSV";
}
