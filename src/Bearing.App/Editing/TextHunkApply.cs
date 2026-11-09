using System.Collections.Generic;
using AvaloniaEdit.Document;
using Bearing.App.Results;

namespace Bearing.App.Editing;

/// <summary>Applies a rewrite's <see cref="TextHunks"/> to the editor's document, as one undo step.</summary>
internal static class TextHunkApply
{
    /// <summary>Apply <paramref name="hunks"/> (from <see cref="TextHunks.Diff"/> of the text sitting at
    /// <paramref name="start"/> in <paramref name="document"/>) — one undo step.</summary>
    public static void Apply(TextDocument document, int start, IReadOnlyList<TextHunks.Hunk> hunks)
    {
        if (hunks.Count == 0)
        {
            return;
        }
        // One update, so Ctrl+Z undoes the whole rewrite at once; back to front, so earlier offsets hold.
        document.BeginUpdate();
        try
        {
            for (var i = hunks.Count - 1; i >= 0; i--)
            {
                // One Replace per hunk, never remove-then-insert: an anchor right after the hunk (a statement end —
                // "desc" upper-cased at the end of a statement with no ';') then follows the end of the new text,
                // and the undo's Replace puts it back at the end of the old (TextHunksTests pins both).
                document.Replace(start + hunks[i].Offset, hunks[i].Length, hunks[i].Text);
            }
        }
        finally
        {
            document.EndUpdate();
        }
    }
}
