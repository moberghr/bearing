namespace Squirrel.App.ViewModels;

/// <summary>One saved SQL script shown in the side pane's Scripts list.</summary>
public sealed record ScriptItem(string Name, string FullPath);
