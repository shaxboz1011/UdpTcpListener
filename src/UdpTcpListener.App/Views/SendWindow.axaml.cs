using Avalonia.Controls;
using UdpTcpListener.App.ViewModels;

namespace UdpTcpListener.App.Views;

public partial class SendWindow : Window
{
    public SendWindow()
    {
        InitializeComponent();
    }

    /// <summary>Stop a running send/replay when the window is closed.</summary>
    protected override void OnClosed(EventArgs e)
    {
        (DataContext as SendViewModel)?.StopCommand.Execute(null);
        base.OnClosed(e);
    }
}
