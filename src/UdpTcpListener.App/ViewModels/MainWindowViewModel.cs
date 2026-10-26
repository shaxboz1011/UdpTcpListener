using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UdpTcpListener.App.Localization;
using UdpTcpListener.App.Services;
using UdpTcpListener.Core.Analysis;
using UdpTcpListener.Core.Capture;
using UdpTcpListener.Core.Decoding;
using UdpTcpListener.Core.Export;
using UdpTcpListener.Core.Formatting;
using UdpTcpListener.Core.Packets;
using UdpTcpListener.Core.Statistics;

namespace UdpTcpListener.App.ViewModels;

public sealed record ProtocolOption(ProtocolFilter Value, string Label);

public sealed class ThemeOption : ObservableObject
{
    private readonly string _labelKey;

    public ThemeOption(AppTheme value, string labelKey)
    {
        Value = value;
        _labelKey = labelKey;
        Localizer.Instance.LanguageChanged += (_, _) => OnPropertyChanged(nameof(Label));
    }

    public AppTheme Value { get; }

    public string Label => Localizer.Instance[_labelKey];
}

public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    /// <summary>Packets kept in memory; the oldest 10% are dropped when exceeded.</summary>
    public const int MaxPackets = 250_000;

    private const int MaxPacketsPerTick = 5_000;
    private const int RateHistoryLength = 60;
    private const int TopCount = 6;

    private static Localizer L => Localizer.Instance;

    private readonly PcapCaptureEngine _engine = new();
    private readonly ConcurrentQueue<PacketRecord> _incoming = new();
    private readonly List<PacketRecord> _all = [];
    private readonly TrafficStatistics _stats = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _rateTimer;
    private readonly AppSettings _settings;
    private readonly Queue<double> _rateHistory = new();
    private long _lastRatePackets;
    private long _lastRateBytes;
    private readonly Stopwatch _rateClock = new();
    private readonly ConversationTracker _conversations = new();
    private readonly Dictionary<ConversationKey, ConversationRow> _conversationRows = [];
    private readonly Lock _recorderLock = new();
    private RotatingPacketWriter? _recorder;

    public MainWindowViewModel() : this(new AppSettings())
    {
    }

    public MainWindowViewModel(AppSettings settings)
    {
        _settings = settings;
        Update.BeforeRestart = PrepareForRestart;
        Converters.AppConverters.Decoders = Decoders.Registry;
        Setup = new SetupViewModel(url => Dialogs?.OpenUrlAsync(url) ?? Task.CompletedTask) { BeforeRestart = PrepareForRestart };
        Setup.DriverInstalledByUser += (_, _) => RefreshDevices();

        SelectedProtocol = Protocols.First(p => p.Value == settings.Protocol);
        SelectedTheme = Themes.First(t => t.Value == settings.Theme);
        SelectedLanguage = Localizer.Languages.First(l => l.Code == Localizer.Instance.Language);
        SourceHost = settings.SourceHost;
        DestinationHost = settings.DestinationHost;
        PortText = settings.Port;
        CustomBpf = settings.CustomBpf;
        AutoScroll = settings.AutoScroll;
        RecordToDisk = settings.RecordToDisk;
        RecordFolder = settings.RecordFolder;
        RecordFileSizeMb = Math.Max(1, settings.RecordFileSizeMb);
        RecordMaxFiles = Math.Max(0, settings.RecordMaxFiles);
        StatusText = L["StatusReady"];

        _engine.PacketCaptured += (_, packet) =>
        {
            _incoming.Enqueue(packet);
            RecordPacket(packet);
        };

        _uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => DrainIncoming());
        _rateTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateRates());

        Localizer.Instance.LanguageChanged += (_, _) => RefreshStatus();

        RefreshDevices();
        if (settings.LastDevice is { } last)
            SelectedDevice = Devices.FirstOrDefault(d => d.Name == last) ?? SelectedDevice;
    }

    public IFileDialogs? Dialogs { get; set; }

    public UpdateViewModel Update { get; } = new();

    public SetupViewModel Setup { get; }

    // ---------------------------------------------------------------- decoders, send & replay

    public ProtocolService Decoders { get; } = new();

    /// <summary>Kept between openings so the sender remembers what was typed.</summary>
    private SendViewModel? _sender;

    public string ProtocolsSummary => Decoders.CustomNames.Count == 0
        ? L["ProtocolsNone"]
        : L.Format("ProtocolsLoaded", Decoders.CustomNames.Count, string.Join(", ", Decoders.CustomNames));

    public string ProtocolErrors => string.Join("\n", Decoders.Errors);

    public bool HasProtocolErrors => Decoders.Errors.Count > 0;

    public string ProtocolsFolder => Decoders.Directory;

    /// <summary>Packet-list columns the user hid; the view applies and updates them.</summary>
    public ICollection<string> HiddenColumns => _settings.HiddenColumns;

    // ---------------------------------------------------------------- conversations

    public ObservableCollection<ConversationRow> Conversations { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowConversationPacketsCommand), nameof(FollowConversationCommand), nameof(ReplayConversationCommand))]
    public partial ConversationRow? SelectedConversation { get; set; }

    [ObservableProperty]
    public partial int ConversationCount { get; set; }

    /// <summary>0 = packets, 1 = conversations.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPacketsTab), nameof(IsConversationsTab))]
    public partial int SelectedTabIndex { get; set; }

    public bool IsPacketsTab => SelectedTabIndex == 0;
    public bool IsConversationsTab => SelectedTabIndex == 1;

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == 1)
            RefreshConversations();
    }

    /// <summary>When set, the packet list shows only this conversation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConversationFilter), nameof(ConversationFilterText))]
    public partial ConversationKey? ConversationFilter { get; set; }

    partial void OnConversationFilterChanged(ConversationKey? value) => RebuildVisible();

    public bool HasConversationFilter => ConversationFilter is not null;

    public string? ConversationFilterText => ConversationFilter is { } key
        ? $"{(key.Protocol == TransportProtocol.Udp ? "UDP" : "TCP")}  {key.EndpointA} ⇄ {key.EndpointB}"
        : null;

    // ---------------------------------------------------------------- save to disk

    public static string DefaultRecordFolder { get; } = Path.Combine(UserDocumentsFolder(), "UdpTcpListener", "captures");

    /// <summary>
    /// The user's Documents folder. On Linux without XDG user dirs .NET returns an empty string here,
    /// which would silently make the path relative to the working directory.
    /// </summary>
    private static string UserDocumentsFolder()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.UserProfile })
        {
            var path = Environment.GetFolderPath(folder);
            if (Path.IsPathFullyQualified(path))
                return path;
        }
        return Path.GetTempPath();
    }

    [ObservableProperty]
    public partial bool RecordToDisk { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordFolderDisplay))]
    public partial string? RecordFolder { get; set; }

    public string RecordFolderDisplay => string.IsNullOrWhiteSpace(RecordFolder) ? DefaultRecordFolder : RecordFolder;

    [ObservableProperty]
    public partial decimal RecordFileSizeMb { get; set; }

    [ObservableProperty]
    public partial decimal RecordMaxFiles { get; set; }

    /// <summary>File currently being written while recording, for the status bar.</summary>
    [ObservableProperty]
    public partial string? RecordingFile { get; set; }

    /// <summary>Raised after new rows were appended, so the view can auto-scroll.</summary>
    public event EventHandler<PacketRecord>? PacketsAppended;

    // ---------------------------------------------------------------- options

    public ObservableCollection<NetworkDevice> Devices { get; } = [];

    public IReadOnlyList<ProtocolOption> Protocols { get; } =
    [
        new(ProtocolFilter.Both, "UDP + TCP"),
        new(ProtocolFilter.Udp, "UDP"),
        new(ProtocolFilter.Tcp, "TCP"),
    ];

    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new(AppTheme.System, "ThemeSystem"),
        new(AppTheme.Light, "ThemeLight"),
        new(AppTheme.Dark, "ThemeDark"),
    ];

    public IReadOnlyList<LanguageOption> Languages => Localizer.Languages;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleCaptureCommand))]
    public partial NetworkDevice? SelectedDevice { get; set; }

    [ObservableProperty]
    public partial ProtocolOption SelectedProtocol { get; set; }

    [ObservableProperty]
    public partial string? SourceHost { get; set; }

    [ObservableProperty]
    public partial string? DestinationHost { get; set; }

    [ObservableProperty]
    public partial string? PortText { get; set; }

    [ObservableProperty]
    public partial string? CustomBpf { get; set; }

    [ObservableProperty]
    public partial bool AutoScroll { get; set; }

    [ObservableProperty]
    public partial ThemeOption SelectedTheme { get; set; }

    [ObservableProperty]
    public partial LanguageOption SelectedLanguage { get; set; }

    partial void OnSelectedThemeChanged(ThemeOption value)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = value.Value switch
            {
                AppTheme.Light => ThemeVariant.Light,
                AppTheme.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }

    partial void OnSelectedLanguageChanged(LanguageOption value) => Localizer.Instance.SetLanguage(value.Code);

    // ---------------------------------------------------------------- state

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditFilter))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    public partial bool IsCapturing { get; set; }

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    public bool CanEditFilter => !IsCapturing;

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    // ---------------------------------------------------------------- packets

    public ObservableCollection<PacketRecord> VisiblePackets { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial int TotalCount { get; set; }

    public bool IsEmpty => TotalCount == 0;

    [ObservableProperty]
    public partial string? SearchText { get; set; }

    partial void OnSearchTextChanged(string? value) => RebuildVisible();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(FollowStreamCommand), nameof(FilterConversationCommand), nameof(CopyPacketCommand), nameof(ResendPacketCommand))]
    public partial PacketRecord? SelectedPacket { get; set; }

    public bool HasSelection => SelectedPacket is not null;

    public ObservableCollection<PacketLayer> SelectedLayers { get; } = [];

    [ObservableProperty]
    public partial string SelectedHexDump { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedPayloadText { get; set; } = "";

    partial void OnSelectedPacketChanged(PacketRecord? value)
    {
        SelectedLayers.Clear();
        if (value is null)
        {
            SelectedHexDump = SelectedPayloadText = "";
            return;
        }

        // Application protocols first: they are what the user usually wants to read.
        foreach (var decoded in Decoders.Registry.Decode(value))
            SelectedLayers.Add(ToPacketLayer(decoded));
        foreach (var layer in PacketDecoder.DescribeLayers(value))
            SelectedLayers.Add(layer);
        SelectedHexDump = HexDump.Format(value.RawData);
        SelectedPayloadText = HexDump.ToAscii(value.Payload, max: 4096);
    }

    // ---------------------------------------------------------------- dashboard

    [ObservableProperty]
    public partial long StatPackets { get; set; }

    [ObservableProperty]
    public partial string StatBytes { get; set; } = "0 B";

    [ObservableProperty]
    public partial string StatPacketsPerSecond { get; set; } = "0";

    [ObservableProperty]
    public partial string StatBandwidth { get; set; } = "0 B/s";

    [ObservableProperty]
    public partial long StatUdp { get; set; }

    [ObservableProperty]
    public partial long StatTcp { get; set; }

    [ObservableProperty]
    public partial double UdpShare { get; set; }

    [ObservableProperty]
    public partial long StatDropped { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<double> RateHistory { get; set; } = [];

    public ObservableCollection<TalkerStat> TopHosts { get; } = [];
    public ObservableCollection<TalkerStat> TopPorts { get; } = [];

    // ---------------------------------------------------------------- commands

    [RelayCommand]
    public void RefreshDevices()
    {
        var previous = SelectedDevice?.Name;
        Devices.Clear();
        try
        {
            foreach (var device in PcapCaptureEngine.GetDevices())
                Devices.Add(device);
            ErrorMessage = Devices.Count == 0 ? L["NoDevices"] : null;
            Setup.Report(driverInstalled: true, elevationRecommended: Devices.Count == 0);
        }
        catch (CaptureLibraryUnavailableException)
        {
            // The setup assistant explains this and offers a one-click install.
            ErrorMessage = null;
            Setup.Report(driverInstalled: false, elevationRecommended: false);
        }

        SelectedDevice = Devices.FirstOrDefault(d => d.Name == previous)
                         ?? Devices.FirstOrDefault(d => d.Addresses.Count > 0 && d.Name is not "lo" and not "any")
                         ?? Devices.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(CanToggleCapture))]
    private void ToggleCapture()
    {
        if (IsCapturing)
            StopCapture();
        else
            StartCapture();
    }

    private bool CanToggleCapture() => IsCapturing || SelectedDevice is not null;

    [RelayCommand]
    private void TogglePause()
    {
        IsPaused = !IsPaused;
        if (!IsPaused)
            DrainIncoming();
        RefreshStatus();
    }

    [RelayCommand]
    private void Clear()
    {
        _incoming.Clear();
        _all.Clear();
        VisiblePackets.Clear();
        _stats.Reset();
        _conversations.Clear();
        _conversationRows.Clear();
        Conversations.Clear();
        ConversationCount = 0;
        ConversationFilter = null;
        _rateHistory.Clear();
        _lastRatePackets = _lastRateBytes = 0;
        SelectedPacket = null;
        TotalCount = 0;
        StatDropped = 0;
        RefreshStatistics();
        StatPacketsPerSecond = "0";
        StatBandwidth = "0 B/s";
        RateHistory = [];
        RefreshStatus();
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Dialogs is null)
            return;

        if (_all.Count == 0)
        {
            ErrorMessage = L["NothingToExport"];
            return;
        }

        var path = await Dialogs.SaveCaptureAsync($"capture_{DateTime.Now:yyyyMMdd_HHmmss}.pcapng");
        if (path is null)
            return;

        // Export what the user sees: respects the search and conversation filters.
        var packets = VisiblePackets.ToList();
        try
        {
            var count = await Task.Run(() => PacketWriter.Export(path, packets));
            ErrorMessage = null;
            StatusText = L.Format("StatusExported", count, Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditFilter))]
    private async Task OpenAsync()
    {
        if (Dialogs is null)
            return;

        var path = await Dialogs.OpenCaptureAsync();
        if (path is null)
            return;

        try
        {
            var packets = await Task.Run(() => CaptureFile.Read(path).ToList());
            Clear();
            foreach (var packet in packets)
                Append(packet);
            RebuildVisible();
            RefreshStatistics();
            RefreshConversations();
            ErrorMessage = null;
            StatusText = L.Format("StatusOpened", packets.Count, Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void FollowStream() => ShowStream(ConversationKey.From(SelectedPacket!));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void FilterConversation() => ConversationFilter = ConversationKey.From(SelectedPacket!);

    [RelayCommand]
    private void ClearConversationFilter() => ConversationFilter = null;

    [RelayCommand]
    private void OpenSender() => Dialogs?.ShowSender(Sender());

    /// <summary>"Send again…": opens the sender pre-filled with the selected packet.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ResendPacket()
    {
        var sender = Sender();
        sender.PrefillFrom(SelectedPacket!);
        Dialogs?.ShowSender(sender);
    }

    /// <summary>Replays the packets currently shown (search and conversation filters applied).</summary>
    [RelayCommand]
    private void ReplayVisible()
    {
        var sender = Sender();
        sender.SetReplayPackets(VisiblePackets.ToList(), L["ReplaySourceVisible"]);
        Dialogs?.ShowSender(sender);
    }

    [RelayCommand(CanExecute = nameof(HasConversationSelection))]
    private void ReplayConversation()
    {
        var key = SelectedConversation!.Key;
        var sender = Sender();
        sender.SetReplayPackets(_all.Where(key.Matches).ToList(), $"{key.EndpointA} ⇄ {key.EndpointB}");
        Dialogs?.ShowSender(sender);
    }

    private SendViewModel Sender() => _sender ??= new SendViewModel(Devices.ToList());

    [RelayCommand]
    private void ReloadProtocols()
    {
        Decoders.Reload();
        Converters.AppConverters.Decoders = Decoders.Registry;
        RebuildVisible(); // refresh the Info column
        OnPropertyChanged(nameof(ProtocolsSummary));
        OnPropertyChanged(nameof(ProtocolErrors));
        OnPropertyChanged(nameof(HasProtocolErrors));
        OnSelectedPacketChanged(SelectedPacket); // re-decode with the new definitions
        StatusText = HasProtocolErrors ? L["ProtocolsReloadedWithErrors"] : L["ProtocolsReloaded"];
    }

    [RelayCommand]
    private Task OpenProtocolsFolder() => Dialogs?.OpenFolderAsync(Decoders.Directory) ?? Task.CompletedTask;

    /// <summary>Shows a decoded protocol in the details tree; nested fields are indented.</summary>
    private static PacketLayer ToPacketLayer(DecodedLayer layer)
    {
        var fields = new List<PacketField>();
        void Add(IEnumerable<DecodedField> items, int depth)
        {
            foreach (var field in items)
            {
                fields.Add(new PacketField(new string(' ', depth * 3) + (depth > 0 ? "› " : "") + field.Name, field.Value));
                if (field.Children is { } children)
                    Add(children, depth + 1);
            }
        }
        Add(layer.Fields, 0);
        return new PacketLayer($"{layer.Protocol}  ·  {layer.Summary}", fields);
    }

    [RelayCommand(CanExecute = nameof(HasConversationSelection))]
    private void ShowConversationPackets()
    {
        ConversationFilter = SelectedConversation!.Key;
        SelectedTabIndex = 0;
    }

    [RelayCommand(CanExecute = nameof(HasConversationSelection))]
    private void FollowConversation() => ShowStream(SelectedConversation!.Key);

    private bool HasConversationSelection() => SelectedConversation is not null;

    private void ShowStream(ConversationKey key)
    {
        var stream = StreamReassembler.Follow(_all, key);
        Dialogs?.ShowStream(new StreamViewModel(stream, Dialogs));
    }

    /// <param name="format">"Summary", "Frame" (hex dump of the whole frame) or a <see cref="CopyFormat"/> name (payload).</param>
    [RelayCommand(CanExecute = nameof(HasSelectionFor))]
    private async Task CopyPacketAsync(string format)
    {
        if (SelectedPacket is not { } p || Dialogs is null)
            return;

        var text = format switch
        {
            "Summary" => $"{p.Number}\t{p.Timestamp:HH:mm:ss.ffffff}\t{p.ProtocolName}\t{p.Source}\t{p.Destination}\t{p.Length}\t{p.Info}",
            "Frame" => CopyFormats.Format(p.RawData, CopyFormat.HexDump),
            _ => CopyFormats.Format(p.Payload, Enum.Parse<CopyFormat>(format)),
        };
        await Dialogs.CopyTextAsync(text);
        StatusText = L["Copied"];
    }

    private bool HasSelectionFor(string _) => HasSelection;

    [RelayCommand]
    private async Task ChooseRecordFolderAsync()
    {
        if (Dialogs is not null && await Dialogs.PickFolderAsync() is { } folder)
            RecordFolder = folder;
    }

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    [RelayCommand]
    private Task OpenUrl(string url) => Dialogs?.OpenUrlAsync(url) ?? Task.CompletedTask;

    // ---------------------------------------------------------------- capture

    private void StartCapture()
    {
        if (SelectedDevice is null)
            return;

        int? port = null;
        if (!string.IsNullOrWhiteSpace(PortText))
        {
            if (!int.TryParse(PortText.Trim(), out var p) || p is < 0 or > ushort.MaxValue)
            {
                ErrorMessage = L["InvalidPort"];
                return;
            }
            port = p;
        }

        var filter = new CaptureFilter
        {
            Protocol = SelectedProtocol.Value,
            SourceHost = SourceHost,
            DestinationHost = DestinationHost,
            Port = port,
            CustomExpression = CustomBpf,
        };

        if (RecordToDisk && !StartRecording())
            return;

        try
        {
            _engine.Start(SelectedDevice.Name, filter);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or SharpPcap.PcapException)
        {
            StopRecording();
            ErrorMessage = L.Format("StartFailed", ex.Message);
            // Opening a device usually fails for lack of rights (Linux without root, restricted Npcap).
            if (ex is SharpPcap.PcapException)
                Setup.Report(driverInstalled: true, elevationRecommended: true);
            return;
        }

        ErrorMessage = null;
        IsPaused = false;
        IsCapturing = true;
        _rateClock.Restart();
        _lastRatePackets = _stats.TotalPackets;
        _lastRateBytes = _stats.TotalBytes;
        _uiTimer.Start();
        _rateTimer.Start();
        RefreshStatus();
    }

    public void StopCapture()
    {
        if (!IsCapturing)
            return;

        _engine.Stop();
        StopRecording();
        _rateTimer.Stop();
        IsCapturing = false;
        IsPaused = false;
        DrainIncoming();
        _uiTimer.Stop();
        RefreshConversations();
        StatPacketsPerSecond = "0";
        StatBandwidth = "0 B/s";
        RefreshStatus();
    }

    private bool StartRecording()
    {
        try
        {
            var recorder = new RotatingPacketWriter(new RotationOptions
            {
                Directory = RecordFolderDisplay,
                BaseName = "capture",
                Format = ExportFormat.PcapNg,
                MaxFileBytes = (long)Math.Max(1, RecordFileSizeMb) * 1024 * 1024,
                MaxFiles = (int)Math.Max(0, RecordMaxFiles),
            });
            lock (_recorderLock)
                _recorder = recorder;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ErrorMessage = L.Format("RecordFailed", ex.Message);
            return false;
        }
    }

    private void StopRecording()
    {
        lock (_recorderLock)
        {
            _recorder?.Dispose();
            _recorder = null;
        }
        RecordingFile = null;
    }

    /// <summary>Runs on the capture thread.</summary>
    private void RecordPacket(PacketRecord packet)
    {
        lock (_recorderLock)
        {
            if (_recorder is null)
                return;
            try
            {
                _recorder.Write(packet);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Disk full or folder gone: stop recording but keep capturing.
                _recorder.Dispose();
                _recorder = null;
                Dispatcher.UIThread.Post(() =>
                {
                    RecordingFile = null;
                    ErrorMessage = L.Format("RecordFailed", ex.Message);
                });
            }
        }
    }

    private void DrainIncoming()
    {
        if (IsPaused)
            return;

        PacketRecord? last = null;
        var processed = 0;
        for (; processed < MaxPacketsPerTick && _incoming.TryDequeue(out var packet); processed++)
        {
            Append(packet);
            if (Matches(packet))
            {
                VisiblePackets.Add(packet);
                last = packet;
            }
        }

        if (_all.Count > MaxPackets)
        {
            _all.RemoveRange(0, MaxPackets / 10);
            RebuildVisible();
        }

        if (processed > 0)
        {
            RefreshStatistics();
            RefreshStatus();
            if (last is not null)
                PacketsAppended?.Invoke(this, last);
        }
    }

    private void Append(PacketRecord packet)
    {
        _all.Add(packet);
        _stats.Add(packet);
        _conversations.Add(packet);
        TotalCount = _all.Count;
    }

    private void UpdateRates()
    {
        var seconds = _rateClock.Elapsed.TotalSeconds;
        _rateClock.Restart();
        if (seconds <= 0)
            return;

        var pps = (_stats.TotalPackets - _lastRatePackets) / seconds;
        var bps = (_stats.TotalBytes - _lastRateBytes) / seconds;
        _lastRatePackets = _stats.TotalPackets;
        _lastRateBytes = _stats.TotalBytes;

        StatPacketsPerSecond = pps.ToString("0");
        StatBandwidth = ByteSize.Format(bps) + "/s";

        _rateHistory.Enqueue(pps);
        while (_rateHistory.Count > RateHistoryLength)
            _rateHistory.Dequeue();
        RateHistory = _rateHistory.ToArray();

        if (_engine.GetStatistics() is { } device)
            StatDropped = device.Dropped + device.InterfaceDropped;

        lock (_recorderLock)
            RecordingFile = _recorder?.CurrentFile is { } file ? Path.GetFileName(file) : null;

        if (SelectedTabIndex == 1)
            RefreshConversations();
    }

    private void RefreshStatistics()
    {
        StatPackets = _stats.TotalPackets;
        StatBytes = ByteSize.Format(_stats.TotalBytes);
        StatUdp = _stats.UdpPackets;
        StatTcp = _stats.TcpPackets;
        UdpShare = _stats.TotalPackets == 0 ? 0 : 100.0 * _stats.UdpPackets / _stats.TotalPackets;

        Replace(TopHosts, _stats.TopHosts(TopCount));
        Replace(TopPorts, _stats.TopPorts(TopCount));
    }

    private void RefreshConversations()
    {
        foreach (var summary in _conversations.Snapshot())
        {
            if (!_conversationRows.TryGetValue(summary.Key, out var row))
            {
                row = new ConversationRow(summary.Key);
                _conversationRows[summary.Key] = row;
                Conversations.Add(row);
            }
            row.Update(summary);
        }
        ConversationCount = _conversations.Count;
    }

    private static void Replace(ObservableCollection<TalkerStat> target, IReadOnlyList<TalkerStat> source)
    {
        for (var i = 0; i < source.Count; i++)
        {
            if (i < target.Count)
            {
                if (target[i] != source[i])
                    target[i] = source[i];
            }
            else
            {
                target.Add(source[i]);
            }
        }
        while (target.Count > source.Count)
            target.RemoveAt(target.Count - 1);
    }

    private void RebuildVisible()
    {
        var selected = SelectedPacket;
        VisiblePackets.Clear();
        foreach (var packet in _all)
        {
            if (Matches(packet))
                VisiblePackets.Add(packet);
        }
        SelectedPacket = selected is not null && VisiblePackets.Contains(selected) ? selected : null;
        TotalCount = _all.Count;
        RefreshStatus();
    }

    private bool Matches(PacketRecord packet)
    {
        if (ConversationFilter is { } conversation && !conversation.Matches(packet))
            return false;

        var term = SearchText?.Trim();
        if (string.IsNullOrEmpty(term))
            return true;

        return packet.Source.Contains(term, StringComparison.OrdinalIgnoreCase)
               || packet.Destination.Contains(term, StringComparison.OrdinalIgnoreCase)
               || packet.ProtocolName.Equals(term, StringComparison.OrdinalIgnoreCase)
               || (packet.TcpFlags?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
               || (term.Length >= 3 && HexDump.ToAscii(packet.Payload).Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshStatus()
    {
        var shown = string.IsNullOrWhiteSpace(SearchText) && ConversationFilter is null ? "" : "  ·  " + L.Format("Shown", VisiblePackets.Count, _all.Count);
        StatusText = IsCapturing
            ? (IsPaused ? L["StatusPaused"] : L.Format("StatusCapturing", SelectedDevice?.Description)) + shown
            : _all.Count > 0
                ? L.Format("StatusStopped", _all.Count) + shown
                : L["StatusReady"];
    }

    // ---------------------------------------------------------------- lifetime

    private void PrepareForRestart()
    {
        StopCapture();
        SaveSettings();
    }

    public void SaveSettings()
    {
        _settings.Theme = SelectedTheme.Value;
        _settings.Language = Localizer.Instance.Language;
        _settings.LastDevice = SelectedDevice?.Name;
        _settings.Protocol = SelectedProtocol.Value;
        _settings.SourceHost = SourceHost;
        _settings.DestinationHost = DestinationHost;
        _settings.Port = PortText;
        _settings.CustomBpf = CustomBpf;
        _settings.AutoScroll = AutoScroll;
        _settings.RecordToDisk = RecordToDisk;
        _settings.RecordFolder = RecordFolder;
        _settings.RecordFileSizeMb = (int)Math.Max(1, RecordFileSizeMb);
        _settings.RecordMaxFiles = (int)Math.Max(0, RecordMaxFiles);
        SettingsService.Save(_settings);
    }

    public void Dispose()
    {
        _uiTimer.Stop();
        _rateTimer.Stop();
        _engine.Dispose();
        StopRecording();
    }
}
