using System;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Bearing.Core.Completion;

namespace Bearing.App.Completion;

/// <summary>Adapts a <see cref="Suggestion"/> to AvaloniaEdit's completion list.</summary>
internal sealed class BearingCompletionData : ICompletionData
{
    private readonly Suggestion _s;

    public BearingCompletionData(Suggestion s) => _s = s;

    public IImage? Image => null;

    /// <summary>Text used for the editor's as-you-type filtering AND (by default) insertion.</summary>
    public string Text => _s.FilterText;

    public object Content => _s.DetailText is { Length: > 0 } d
        ? $"{_s.DisplayText}    {d}"
        : _s.DisplayText;

    public object Description => _s.Description ?? _s.DisplayText;

    public double Priority => _s.Priority;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        => textArea.Document.Replace(completionSegment, _s.ReplacementText);
}
