using Bearing.Updates;
using Xunit;

namespace Bearing.Updates.Tests;

/// <summary>
/// Deciding whether a shortcut is ours and stale. Pure and tested exhaustively for the same reason
/// <see cref="WindowsPathEntryTests"/> is: this is the half that could rewrite something it does not own,
/// and a <c>.lnk</c> belonging to somebody else is not recoverable by re-running an installer.
/// </summary>
public class WindowsShortcutTargetTests
{
    private const string Dir = @"C:\Users\x\AppData\Local\BearingSql\current";
    private const string Stale = "bearing.exe";
    private const string App = "bearing-app.exe";

    private static string? Corrected(string? target, string directory = Dir)
        => WindowsShortcutTarget.Corrected(target, directory, Stale, App);

    /// <summary>The case this exists for: 1.0.x wrote this shortcut, 1.1.0 renamed the window's
    /// executable, and Velopack does not revisit a shortcut it wrote at install time.</summary>
    [Fact]
    public void The_shortcut_left_by_the_rename_is_repointed()
    {
        Assert.Equal($@"{Dir}\{App}", Corrected($@"{Dir}\{Stale}"));
    }

    /// <summary>Null means "leave it alone", and the caller relies on it: a rewrite that changed nothing
    /// would still touch the file's timestamp and hand the shell a reason to re-read it.</summary>
    [Fact]
    public void A_shortcut_already_pointing_at_the_app_is_left_alone()
    {
        Assert.Null(Corrected($@"{Dir}\{App}"));
    }

    /// <summary>Running the migration twice writes once. Every update after 1.1.0 runs it again.</summary>
    [Fact]
    public void The_repair_is_idempotent()
    {
        var once = Corrected($@"{Dir}\{Stale}");
        Assert.NotNull(once);
        Assert.Null(Corrected(once));
    }

    /// <summary>
    /// The condition that keeps this from being dangerous. A shortcut to a <i>different</i> program that
    /// happens to be called <c>bearing.exe</c> — a build output, a copy someone keeps elsewhere, another
    /// vendor's tool — is not ours to repoint at an executable that is not beside it.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Program Files\Something\bearing.exe")]
    [InlineData(@"C:\Users\x\source\repos\bearing\src\Bearing.Cli\bin\Debug\net10.0\bearing.exe")]
    [InlineData(@"C:\Users\x\AppData\Local\BearingSql\bearing.exe")]        // the stub, one level up
    [InlineData(@"C:\Users\x\AppData\Local\BearingSql\current\sub\bearing.exe")]
    public void A_shortcut_outside_the_install_directory_is_never_touched(string target)
    {
        Assert.Null(Corrected(target));
    }

    /// <summary>Only the one file name. Everything else in that directory belongs to the app and is not a
    /// thing a shortcut of ours was ever aimed at.</summary>
    [Theory]
    [InlineData("Update.exe")]
    [InlineData("bearing-app.exe")]
    [InlineData("bearingx.exe")]
    [InlineData("bearing.dll")]
    public void Another_file_in_the_same_directory_is_never_touched(string name)
    {
        Assert.Null(Corrected($@"{Dir}\{name}"));
    }

    /// <summary>
    /// A target is stored in whatever spelling the writer used. Treating these as different paths is how a
    /// stale shortcut survives the migration that exists to fix it.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\BearingSql\current\bearing.exe")]
    [InlineData(@"""C:\Users\x\AppData\Local\BearingSql\current\bearing.exe""")]
    [InlineData(@"  C:\Users\x\AppData\Local\BearingSql\current\bearing.exe  ")]
    [InlineData(@"c:\users\x\appdata\local\bearingsql\current\BEARING.EXE")]
    [InlineData(@"C:/Users/x/AppData/Local/BearingSql/current/bearing.exe")]
    public void The_same_target_is_recognised_however_it_is_spelled(string target)
    {
        Assert.NotNull(Corrected(target));
    }

    /// <summary>The install directory arrives from <c>AppContext.BaseDirectory</c>, which carries a
    /// trailing separator. The caller trims it; this does not depend on that.</summary>
    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\BearingSql\current")]
    [InlineData(@"C:\Users\x\AppData\Local\BearingSql\current\")]
    [InlineData(@"c:\users\x\appdata\local\bearingsql\current")]
    public void The_install_directory_is_matched_however_it_arrives(string directory)
    {
        Assert.NotNull(Corrected($@"{Dir}\{Stale}", directory));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_to_read_is_nothing_to_do(string? target)
    {
        Assert.Null(Corrected(target));
    }

    /// <summary>A path the platform cannot parse is not one we wrote, and must not escape as an exception
    /// from inside an installer callback.</summary>
    [Fact]
    public void A_target_that_is_not_a_path_is_declined_rather_than_thrown_over()
    {
        Assert.Null(Corrected("\0"));
        Assert.Null(Corrected("|||"));
    }

    /// <summary>A guard against the migration being wired to rename a file to itself, which would make
    /// every shortcut in the install directory look stale forever.</summary>
    [Fact]
    public void A_rename_to_the_same_name_is_not_a_rename()
    {
        Assert.Null(WindowsShortcutTarget.Corrected($@"{Dir}\{Stale}", Dir, Stale, Stale));
    }
}
