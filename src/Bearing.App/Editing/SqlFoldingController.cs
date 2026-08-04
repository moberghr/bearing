using System.Linq;
using AvaloniaEdit;
using AvaloniaEdit.Folding;
using Bearing.Sql;

namespace Bearing.App.Editing;

/// <summary>
/// Installs AvaloniaEdit's fold margin on the editor and keeps its foldings in sync with the SQL
/// statements in the buffer (one per multi-line query, via <see cref="SqlFolding"/>). Also drives
/// the keyboard fold commands. Folding a query collapses everything below its first line.
/// </summary>
internal sealed class SqlFoldingController
{
    private readonly TextEditor _editor;
    private readonly FoldingManager _manager;

    public SqlFoldingController(TextEditor editor)
    {
        _editor = editor;
        _manager = FoldingManager.Install(editor.TextArea); // adds the clickable [-]/[+] margin
    }

    /// <summary>Rebuild foldings from the current text; UpdateFoldings keeps unchanged regions' folded state.</summary>
    public void Refresh()
    {
        var foldings = SqlFolding.ComputeFoldRegions(_editor.Text)
            .Select(r => new NewFolding(r.Start, r.End));
        _manager.UpdateFoldings(foldings, -1);
    }

    public void FoldCurrent() => SetCurrent(true);
    public void UnfoldCurrent() => SetCurrent(false);

    public void FoldAll() => SetAll(true);
    public void UnfoldAll() => SetAll(false);

    private void SetCurrent(bool folded)
    {
        if (StatementSplitter.StatementAt(_editor.Text, _editor.CaretOffset) is not { } stmt) return;
        // The fold section owned by this statement is the one whose header line sits within its span.
        var section = _manager.AllFoldings.FirstOrDefault(f => f.StartOffset >= stmt.Start && f.StartOffset < stmt.End);
        if (section is not null) section.IsFolded = folded;
    }

    private void SetAll(bool folded)
    {
        foreach (var section in _manager.AllFoldings) section.IsFolded = folded;
    }
}
