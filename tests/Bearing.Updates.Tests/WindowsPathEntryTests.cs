using Bearing.Updates;
using Xunit;

namespace Bearing.Updates.Tests;

/// <summary>
/// Editing a PATH, as text. This is the half where an installer corrupts one, which is why it is pure and
/// tested exhaustively while the registry half stays small enough to read.
/// </summary>
public class WindowsPathEntryTests
{
    private const string Dir = @"C:\Users\x\AppData\Local\BearingSql\current";

    [Fact]
    public void An_entry_is_appended_when_it_is_not_there()
    {
        var updated = WindowsPathEntry.WithEntry(@"C:\Windows;C:\Windows\System32", Dir);

        // Appended, not prepended: this command is not one anybody should be shadowing an existing tool
        // with, and a PATH entry that jumps the queue changes what an unrelated command means.
        Assert.Equal($@"C:\Windows;C:\Windows\System32;{Dir}", updated);
    }

    /// <summary>
    /// Null means "nothing to do", and the caller relies on it: a pointless write rewrites the user's PATH
    /// and broadcasts a change to every running program. Every update re-asserts the entry, so this is the
    /// common case rather than an edge one.
    /// </summary>
    [Fact]
    public void Adding_an_entry_that_is_already_there_changes_nothing()
    {
        Assert.Null(WindowsPathEntry.WithEntry($@"C:\Windows;{Dir}", Dir));
    }

    /// <summary>
    /// PATH entries are hand-edited and arrive in every spelling. Treating these as different directories
    /// is how an install adds a duplicate and an uninstall leaves one behind.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\BearingSql\current")]
    [InlineData(@"C:\Users\x\AppData\Local\BearingSql\current\")]
    [InlineData(@"c:\users\x\appdata\local\bearingsql\current")]
    [InlineData(@"""C:\Users\x\AppData\Local\BearingSql\current""")]
    [InlineData(@"  C:\Users\x\AppData\Local\BearingSql\current  ")]
    public void The_same_directory_is_recognised_however_it_is_spelled(string existing)
    {
        Assert.Null(WindowsPathEntry.WithEntry($@"C:\Windows;{existing}", Dir));
        Assert.Equal(@"C:\Windows", WindowsPathEntry.WithoutEntry($@"C:\Windows;{existing}", Dir));
    }

    [Fact]
    public void Removing_takes_every_copy()
    {
        // An install that ran twice before this was idempotent could have left two.
        var updated = WindowsPathEntry.WithoutEntry($@"{Dir};C:\Windows;{Dir}\", Dir);

        Assert.Equal(@"C:\Windows", updated);
    }

    [Fact]
    public void Removing_something_that_is_not_there_changes_nothing()
    {
        Assert.Null(WindowsPathEntry.WithoutEntry(@"C:\Windows;C:\Windows\System32", Dir));
    }

    /// <summary>
    /// An empty entry means "the current directory" to the Windows loader, so a PATH that gains one from a
    /// stray semicolon is a real hazard — and joining a list that still held one is the way this code could
    /// introduce it.
    /// </summary>
    [Fact]
    public void Empty_entries_are_never_written_back()
    {
        Assert.Equal($@"C:\Windows;{Dir}", WindowsPathEntry.WithEntry(@"C:\Windows;;", Dir));
        Assert.Equal(@"C:\Windows", WindowsPathEntry.WithoutEntry($@";C:\Windows;;{Dir};", Dir));
    }

    /// <summary>
    /// A variable is left as it was written. The registry is read unexpanded precisely so this is possible;
    /// expanding it here would bake today's value into the user's PATH permanently.
    /// </summary>
    [Fact]
    public void An_unexpanded_variable_survives_the_edit()
    {
        var updated = WindowsPathEntry.WithEntry(@"%SystemRoot%;%SystemRoot%\System32", Dir);

        Assert.Equal($@"%SystemRoot%;%SystemRoot%\System32;{Dir}", updated);
    }

    [Fact]
    public void An_empty_path_gains_just_the_one_entry()
    {
        Assert.Equal(Dir, WindowsPathEntry.WithEntry("", Dir));
    }
}
