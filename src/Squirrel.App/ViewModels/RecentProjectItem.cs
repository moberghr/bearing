namespace Squirrel.App.ViewModels;

/// <summary>A recent project shown in the switcher: its folder plus the manifest name to display.</summary>
public sealed record RecentProjectItem(string Directory, string Name);
