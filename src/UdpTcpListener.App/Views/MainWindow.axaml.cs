using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using UdpTcpListener.App.Localization;
using UdpTcpListener.App.Services;
using UdpTcpListener.App.ViewModels;
using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.App.Views;

public partial class MainWindow : Window, IFileDialogs
{
    private static readonly FilePickerFileType PcapNg = new("pcapng (Wireshark)") { Patterns = ["*.pcapng"] };
    private static readonly FilePickerFileType Pcap = new("pcap (Wireshark, tcpdump)") { Patterns = ["*.pcap"] };
    private static readonly FilePickerFileType Captures = new("Capture files") { Patterns = ["*.pcapng", "*.pcap", "*.cap"] };
    private static readonly FilePickerFileType Csv = new("CSV") { Patterns = ["*.csv"] };
    private static readonly FilePickerFileType Json = new("JSON") { Patterns = ["*.json"] };
    private static readonly FilePickerFileType Text = new("Text") { Patterns = ["*.txt"] };

    private MainWindowViewModel? _viewModel;
    private bool _scrollPending;

    public MainWindow()
    {
        InitializeComponent();

        // Right-click selects the row first, so the context menu acts on it.
        PacketGrid.CellPointerPressed += (_, e) =>
        {
            if (e.PointerPressedEventArgs.GetCurrentPoint(PacketGrid).Properties.IsRightButtonPressed)
                PacketGrid.SelectedItem = e.Row.DataContext;
        };
        ConversationGrid.CellPointerPressed += (_, e) =>
        {
            if (e.PointerPressedEventArgs.GetCurrentPoint(ConversationGrid).Properties.IsRightButtonPressed)
                ConversationGrid.SelectedItem = e.Row.DataContext;
        };
        ConversationGrid.DoubleTapped += (_, _) => _viewModel?.ShowConversationPacketsCommand.Execute(null);
        ColumnsButton.Click += (_, _) => ShowColumnChooser();
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
            foreach (var column in PacketGrid.Columns)
                column.IsVisible = !_viewModel.HiddenColumns.Contains(ColumnKey(column));
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

    /// <summary>Menu of checkable column names; unchecking hides the column and is remembered.</summary>
    private void ShowColumnChooser()
    {
        var menu = new MenuFlyout();
        foreach (var column in PacketGrid.Columns)
        {
            var item = new MenuItem
            {
                Header = column.Header,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = column.IsVisible,
                // Keep at least one column visible.
                IsEnabled = !column.IsVisible || PacketGrid.Columns.Count(c => c.IsVisible) > 1,
            };
            item.Click += (_, _) =>
            {
                column.IsVisible = !column.IsVisible;
                if (_viewModel is null)
                    return;
                var key = ColumnKey(column);
                if (column.IsVisible)
                    _viewModel.HiddenColumns.Remove(key);
                else if (!_viewModel.HiddenColumns.Contains(key))
                    _viewModel.HiddenColumns.Add(key);
            };
            menu.Items.Add(item);
        }
        menu.ShowAt(ColumnsButton);
    }

    private static string ColumnKey(DataGridColumn column) => column.Tag as string ?? column.Header?.ToString() ?? "";

    public async Task<string?> SaveCaptureAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Localizer.Instance["Export"],
            SuggestedFileName = suggestedName,
            DefaultExtension = "pcapng",
            FileTypeChoices = [PcapNg, Pcap, Csv, Json, Text],
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
            FileTypeFilter = [Captures, FilePickerFileTypes.All],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SaveTextAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Localizer.Instance["Save"],
            SuggestedFileName = suggestedName,
            DefaultExtension = "txt",
            FileTypeChoices = [Text],
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Localizer.Instance["RecordFolder"],
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task OpenUrlAsync(string url)
    {
        if (Launcher is { } launcher)
            await launcher.LaunchUriAsync(new Uri(url));
    }

    public async Task CopyTextAsync(string text)
    {
        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    public void ShowStream(StreamViewModel stream) => new StreamWindow { DataContext = stream }.Show(this);

    private SendWindow? _sendWindow;

    /// <summary>One Send/Replay window at a time; reopening brings it to the front.</summary>
    public void ShowSender(SendViewModel sender)
    {
        if (_sendWindow is null)
        {
            _sendWindow = new SendWindow { DataContext = sender };
            _sendWindow.Closed += (_, _) => _sendWindow = null;
            _sendWindow.Show(this);
        }
        else
        {
            _sendWindow.DataContext = sender;
            _sendWindow.Activate();
        }
    }

    public async Task OpenFolderAsync(string path)
    {
        Directory.CreateDirectory(path);
        if (Launcher is { } launcher)
            await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
    }
}
