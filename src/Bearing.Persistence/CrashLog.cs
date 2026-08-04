using System.Text;

namespace Bearing.Persistence;

/// <summary>
/// Best-effort diagnostic log for unexpected errors, appended to <c>crash.log</c> in the app data dir.
/// Never throws — a failure to log must never cascade into a second failure.
/// </summary>
public static class CrashLog
{
    public static string Path => System.IO.Path.Combine(BearingPaths.DataDir, "crash.log");

    public static void Write(string context, Exception ex)
    {
        try
        {
            var entry = new StringBuilder()
                .Append("=== ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(" · ").Append(context).AppendLine(" ===")
                .AppendLine(ex.ToString())
                .AppendLine()
                .ToString();
            File.AppendAllText(Path, entry);
        }
        catch { /* diagnostics must never cascade */ }
    }
}
