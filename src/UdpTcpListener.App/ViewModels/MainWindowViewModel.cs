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
using UdpTcpListener.Core.Capture;
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

    public MainWindowViewModel() : this(new AppSettings())
    {
    }

    public MainWindowViewModel(AppSettings settings)
    {
        _settings = settings;
        Update.BeforeRestart = () =>
        {
            StopCapture();
            SaveSettings();
        };

        SelectedProtocol = Protocols.First(p => p.Value == settings.Protocol);
        SelectedTheme = Themes.First(t => t.Value == settings.Theme);
        SelectedLanguage = Localizer.Languages.First(l => l.Code == Localizer.Instance.Language);
        SourceHost = settings.SourceHost;
        DestinationHost = settings.DestinationHost;
        PortText = settings.Port;
        CustomBpf = settings.CustomBpf;
        AutoScroll = settings.AutoScroll;
        StatusText = L["StatusReady"];

        _engine.PacketCaptured += (_, packet) => _incoming.Enqueue(packet);

        _uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => DrainIncoming());
        _rateTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateRates());

        Localizer.Instance.LanguageChanged += (_, _) => RefreshStatus();

        RefreshDevices();
        if (settings.LastDevice is { } last)
            SelectedDevice = Devices.FirstOrDefault(d => d.Name == last) ?? SelectedDevice;
    }

    public IFileDialogs? Dialogs { get; set; }

    public UpdateViewModel Update { get; } = new();

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

    [ObservableProperty]
    public partial bool IsCaptureLibraryMissing { get; set; }

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
            IsCaptureLibraryMissing = false;
            ErrorMessage = Devices.Count == 0 ? L["NoDevices"] : null;
        }
        catch (CaptureLibraryUnavailableException)
        {
            IsCaptureLibraryMissing = true;
            ErrorMessage = OperatingSystem.IsWindows() ? L["NpcapMissing"] : L["LibpcapMissing"];
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

        var path = await Dialogs.SaveCaptureAsync($"capture_{DateTime.Now:yyyyMMdd_HHmmss}.pcap");
        if (path is null)
            return;

        // Export what the user sees: respects the search filter.
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
            var packets = await Task.Run(() => PcapFileReader.Read(path).ToList());
            Clear();
            foreach (var packet in packets)
                Append(packet);
            RebuildVisible();
            RefreshStatistics();
            ErrorMessage = null;
            StatusText = L.Format("StatusOpened", packets.Count, Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
        }
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

        try
        {
            _engine.Start(SelectedDevice.Name, filter);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or SharpPcap.PcapException)
        {
            ErrorMessage = L.Format("StartFailed", ex.Message);
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
        _rateTimer.Stop();
        IsCapturing = false;
        IsPaused = false;
        DrainIncoming();
        _uiTimer.Stop();
        StatPacketsPerSecond = "0";
        StatBandwidth = "0 B/s";
        RefreshStatus();
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
        var shown = string.IsNullOrWhiteSpace(SearchText) ? "" : "  ·  " + L.Format("Shown", VisiblePackets.Count, _all.Count);
        StatusText = IsCapturing
            ? (IsPaused ? L["StatusPaused"] : L.Format("StatusCapturing", SelectedDevice?.Description)) + shown
            : _all.Count > 0
                ? L.Format("StatusStopped", _all.Count) + shown
                : L["StatusReady"];
    }

    // ---------------------------------------------------------------- lifetime

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
        SettingsService.Save(_settings);
    }

    public void Dispose()
    {
        _uiTimer.Stop();
        _rateTimer.Stop();
        _engine.Dispose();
    }
}
