using Avalonia.Controls;

namespace Squirrel.App.Controls;

/// <summary>
/// The connection-status dot + label, shown in the toolbar and mirrored in the status bar. Its DataContext
/// is the shell <see cref="Squirrel.App.ViewModels.ShellViewModel"/> (inherited from the window); it binds
/// to the connections concern's <c>StatusLabel</c> / <c>IsConnecting</c> / <c>IsDisconnected</c>. Pure
/// visual — no code-behind logic.
/// </summary>
public partial class ConnectionStatusView : UserControl
{
    public ConnectionStatusView() => InitializeComponent();
}
