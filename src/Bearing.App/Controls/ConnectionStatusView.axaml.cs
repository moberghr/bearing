using Avalonia;
using Avalonia.Controls;

namespace Bearing.App.Controls;

/// <summary>
/// The connection-state beacon + label, shown in the toolbar and mirrored in the status bar. Its DataContext
/// is the shell <see cref="Bearing.App.ViewModels.ShellViewModel"/> (inherited from the window); it binds
/// to the connections concern's <c>State</c> / <c>StatusLabel</c> / <c>IsConnecting</c> /
/// <c>IsDisconnected</c>. Pure visual — no code-behind logic.
/// </summary>
public partial class ConnectionStatusView : UserControl
{
    /// <summary>Rendered size of the beacon. The design calls for 14px in the toolbar and 13px in the status
    /// bar (CONNECTION_STATUS §2); 12 is the floor, below which the core and ring merge.</summary>
    public static readonly StyledProperty<double> BeaconSizeProperty =
        AvaloniaProperty.Register<ConnectionStatusView, double>(nameof(BeaconSize), 14d);

    public double BeaconSize
    {
        get => GetValue(BeaconSizeProperty);
        set => SetValue(BeaconSizeProperty, value);
    }

    public ConnectionStatusView() => InitializeComponent();
}
