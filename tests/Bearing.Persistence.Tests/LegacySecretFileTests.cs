using Bearing.Persistence;
using Xunit;

namespace Bearing.Persistence.Tests;

/// <summary>
/// The removed on-disk fallback left real, recoverable passwords in <c>&lt;data dir&gt;/secrets/</c> on any
/// machine that had opted into it. Nothing reads or clears them any more, so startup deletes them — a file
/// nothing can use is pure exposure.
/// </summary>
public class LegacySecretFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-legacy-secrets", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { } }

    private string Dir => Path.Combine(_root, "secrets");

    [Fact]
    public void Deletes_every_leftover_secret_and_the_directory()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, Guid.NewGuid().ToString("N")), "cGFzc3dvcmQ=");
        File.WriteAllText(Path.Combine(Dir, Guid.NewGuid().ToString("N")), "aHVudGVyMg==");

        Assert.Equal(2, LegacySecretFiles.Purge(Dir));
        Assert.False(Directory.Exists(Dir));
    }

    [Fact]
    public void Is_a_no_op_when_there_is_nothing_to_purge()
    {
        // The common case by far — a fresh install, or the second launch after the first one cleaned up.
        Assert.Equal(0, LegacySecretFiles.Purge(Dir));

        Directory.CreateDirectory(Dir);
        Assert.Equal(0, LegacySecretFiles.Purge(Dir));
        Assert.False(Directory.Exists(Dir));    // an empty leftover directory goes too
    }

    [Fact]
    public void Runs_again_without_complaining()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, Guid.NewGuid().ToString("N")), "cGFzc3dvcmQ=");

        Assert.Equal(1, LegacySecretFiles.Purge(Dir));
        Assert.Equal(0, LegacySecretFiles.Purge(Dir));   // startup calls it every launch
    }
}
