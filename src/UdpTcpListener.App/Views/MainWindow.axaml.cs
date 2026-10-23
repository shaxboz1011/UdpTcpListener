using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using UdpTcpListener.App.Localization;
using UdpTcpListener.App.Services;
using UdpTcpListener.App.ViewModels;
using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.App.Views;

public partial class MainWindow : Window, IFileDialogs
{
    private static readonly FilePickerFileType Pcap = new("PCAP (Wireshark)") { Patterns = ["*.pcap"] };
    private static readonly FilePickerFileType Csv = new("CSV") { Patterns = ["*.csv"] };
    private static readonly FilePickerFileType Json = new("JSON") { Patterns = ["*.json"] };
    private static readonly FilePickerFileType Text = new("Text log") { Patterns = ["*.txt"] };

    private MainWindowViewModel? _viewModel;
    private bool _scrollPending;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.PacketsAppended -= OnPacketsAppended;
            _viewModel.Dialogs = null;
        }

        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PacketsAppended += OnPacketsAppended;
            _viewModel.Dialogs = this;
        }
    }

    private void OnPacketsAppended(object? sender, PacketRecord last)
    {
        if (_viewModel is not { AutoScroll: true } || _scrollPending)
            return;

        // Coalesce: scroll once after layout instead of on every batch.
        _scrollPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollPending = false;
            if (_viewModel is { AutoScroll: true, VisiblePackets.Count: > 0 } vm)
                PacketGrid.ScrollIntoView(vm.VisiblePackets[^1], null);
        }, DispatcherPriority.Background);
    }

    public async Task<string?> SaveCaptureAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Localizer.Instance["Export"],
            SuggestedFileName = suggestedName,
            DefaultExtension = "pcap",
            FileTypeChoices = [Pcap, Csv, Json, Text],
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> OpenCaptureAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Instance["Open"],
            AllowMultiple = false,
            FileTypeFilter = [Pcap, FilePickerFileTypes.All],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task OpenUrlAsync(string url)
    {
        if (Launcher is { } launcher)
            await launcher.LaunchUriAsync(new Uri(url));
    }
}
