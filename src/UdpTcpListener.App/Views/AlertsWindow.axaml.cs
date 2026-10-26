using Avalonia.Controls;
using UdpTcpListener.App.ViewModels;

namespace UdpTcpListener.App.Views;

public partial class AlertsWindow : Window
{
    public AlertsWindow()
    {
        InitializeComponent();
        // Looking at the log counts as reading the alerts.
        Tabs.SelectionChanged += (_, _) => MarkReadIfLogVisible();
        Activated += (_, _) => MarkReadIfLogVisible();
        LogGrid.DoubleTapped += (_, _) =>
        {
            if (LogGrid.SelectedItem is AlertLogEntry { PacketNumber: { } number })
                GoToPacket?.Invoke(this, number);
        };
    }

    /// <summary>Raised when the user double-clicks a log entry.</summary>
    public event EventHandler<long>? GoToPacket;

    public void ShowLog() => Tabs.SelectedIndex = 1;

    private void MarkReadIfLogVisible()
    {
        if (Tabs.SelectedIndex == 1 && DataContext is AlertsViewModel vm)
            vm.MarkRead();
    }
}
