using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UdpTcpListener.App.Localization;
using UdpTcpListener.Core.Capture;
using UdpTcpListener.Core.Formatting;
using UdpTcpListener.Core.Packets;
using UdpTcpListener.Core.Sending;

namespace UdpTcpListener.App.ViewModels;

public sealed record EncodingOption(PayloadEncoding Value, string Label);

public sealed record TransportOption(TransportProtocol Value, string Label);

public sealed record ReplayModeOption(bool Inject, string LabelKey)
{
    public string Label => Localizer.Instance[LabelKey];
}

public sealed record ReplayTimingOption(ReplayTiming Value, string LabelKey)
{
    public string Label => Localizer.Instance[LabelKey];
}

/// <summary>A reply shown under the sender.</summary>
public sealed record ReplyView(string Header, string Dump);

/// <summary>
/// The "Send / Replay" window. Send: craft a UDP/TCP payload and send it N times.
/// Replay: send captured packets again, as payloads over sockets or as raw frames.
/// </summary>
public sealed partial class SendViewModel : ViewModelBase
{
    private const int MaxReplies = 200;
    private static Localizer L => Localizer.Instance;

    private CancellationTokenSource? _cts;
    private IReadOnlyList<PacketRecord> _replayPackets = [];

    public SendViewModel(IReadOnlyList<NetworkDevice> devices)
    {
        Devices = devices;
        SelectedTransport = Transports[0];
        SelectedEncoding = Encodings[0];
        SelectedReplayMode = ReplayModes[0];
        SelectedTiming = Timings[0];
        SelectedDevice = devices.FirstOrDefault(d => d.Name is "lo" or "any") ?? devices.FirstOrDefault();
        Replies.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasReplies));
        UpdatePreview();
    }

    /// <summary>0 = send, 1 = replay.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSendTab), nameof(IsReplayTab))]
    public partial int SelectedTabIndex { get; set; }

    public bool IsSendTab => SelectedTabIndex == 0;
    public bool IsReplayTab => SelectedTabIndex == 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        IsBusy = true;
        HasError = false;
        _cts = new CancellationTokenSource();
        try
        {
            if (SelectedTabIndex == 0)
                await SendAsync(_cts.Token);
            else
                await ReplayAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            Status = L["SendStopped"];
        }
        catch (Exception ex) when (ex is SocketException or IOException or ArgumentException or InvalidOperationException
                                       or SharpPcap.PcapException or CaptureLibraryUnavailableException)
        {
            HasError = true;
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private bool CanStart() => !IsBusy;

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    // ---------------------------------------------------------------- send

    public IReadOnlyList<TransportOption> Transports { get; } = [new(TransportProtocol.Udp, "UDP"), new(TransportProtocol.Tcp, "TCP")];

    public IReadOnlyList<EncodingOption> Encodings { get; } =
    [
        new(PayloadEncoding.Text, "Text (\\n, \\xNN)"),
        new(PayloadEncoding.Hex, "Hex"),
        new(PayloadEncoding.Base64, "Base64"),
    ];

    [ObservableProperty]
    public partial TransportOption SelectedTransport { get; set; }

    [ObservableProperty]
    public partial string Host { get; set; } = "127.0.0.1";

    [ObservableProperty]
    public partial decimal? Port { get; set; } = 9000;

    [ObservableProperty]
    public partial string Payload { get; set; } = "Hello from UdpTcpListener\\n";

    [ObservableProperty]
    public partial EncodingOption SelectedEncoding { get; set; }

    [ObservableProperty]
    public partial decimal? Count { get; set; } = 1;

    [ObservableProperty]
    public partial decimal? IntervalMs { get; set; } = 0;

    [ObservableProperty]
    public partial bool WaitForReply { get; set; }

    [ObservableProperty]
    public partial decimal? ReplyTimeoutMs { get; set; } = 1000;

    /// <summary>"12 bytes" or the parse error.</summary>
    [ObservableProperty]
    public partial string PayloadInfo { get; set; } = "";

    [ObservableProperty]
    public partial bool PayloadInvalid { get; set; }

    public ObservableCollection<ReplyView> Replies { get; } = [];

    public bool HasReplies => Replies.Count > 0;

    partial void OnPayloadChanged(string value) => UpdatePreview();

    partial void OnSelectedEncodingChanged(EncodingOption value) => UpdatePreview();

    /// <summary>Pre-fills the sender from a captured packet ("Send again…").</summary>
    public void PrefillFrom(PacketRecord packet)
    {
        SelectedTabIndex = 0;
        SelectedTransport = Transports.First(t => t.Value == packet.Protocol);
        Host = packet.DestinationAddress.ToString();
        Port = packet.DestinationPort;
        SelectedEncoding = Encodings.First(e => e.Value == PayloadEncoding.Hex);
        Payload = CopyFormats.Format(packet.Payload, CopyFormat.Hex);
    }

    private void UpdatePreview()
    {
        if (SelectedEncoding is null)
            return;
        PayloadInvalid = !PayloadParser.TryParse(Payload ?? "", SelectedEncoding.Value, out var bytes, out var error);
        PayloadInfo = PayloadInvalid ? error! : L.Format("SendPayloadBytes", bytes.Length);
    }

    private async Task SendAsync(CancellationToken ct)
    {
        if (!PayloadParser.TryParse(Payload ?? "", SelectedEncoding.Value, out var bytes, out var error))
            throw new ArgumentException(error);

        Replies.Clear();
        var options = new SendOptions
        {
            Protocol = SelectedTransport.Value,
            Host = Host?.Trim() ?? "",
            Port = (int)(Port ?? 0),
            Payload = bytes,
            Count = (int)Math.Max(0, Count ?? 1),
            Interval = TimeSpan.FromMilliseconds((double)Math.Max(0, IntervalMs ?? 0)),
            ResponseTimeout = WaitForReply ? TimeSpan.FromMilliseconds((double)Math.Max(1, ReplyTimeoutMs ?? 1000)) : TimeSpan.Zero,
        };

        Status = L["SendSending"];
        var progress = new UiProgress<SendProgress>(p =>
            Status = L.Format("SendProgress", p.Sent, ByteSize(p.BytesSent)) + (p.Errors > 0 ? $" · {p.Errors} ✕ {p.LastError}" : ""));
        var result = await PacketSender.SendAsync(options, progress, ct);

        foreach (var reply in result.Responses.Take(MaxReplies))
            Replies.Add(new ReplyView($"← {reply.From}  ·  {reply.Timestamp:HH:mm:ss.fff}  ·  {L.Format("StreamBytes", reply.Data.Length)}", HexDump.Format(reply.Data)));

        HasError = result.Errors > 0;
        Status = L.Format("SendDone", result.Sent, ByteSize(result.BytesSent), result.Elapsed.TotalSeconds)
                 + (WaitForReply ? "  ·  " + L.Format("SendReplies", result.Responses.Count) : "")
                 + (result.Errors > 0 ? $"  ·  {result.Errors} ✕" : "");
    }

    // ---------------------------------------------------------------- replay

    public IReadOnlyList<NetworkDevice> Devices { get; }

    public IReadOnlyList<ReplayModeOption> ReplayModes { get; } =
    [
        new(false, "ReplayModePayload"),
        new(true, "ReplayModeInject"),
    ];

    public IReadOnlyList<ReplayTimingOption> Timings { get; } =
    [
        new(ReplayTiming.Original, "ReplayTimingOriginal"),
        new(ReplayTiming.FixedInterval, "ReplayTimingFixed"),
        new(ReplayTiming.AsFastAsPossible, "ReplayTimingFast"),
    ];

    [ObservableProperty]
    public partial string ReplaySource { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInjectMode))]
    public partial ReplayModeOption SelectedReplayMode { get; set; }

    public bool IsInjectMode => SelectedReplayMode?.Inject == true;

    /// <summary>host:port; empty = original destinations (UDP only).</summary>
    [ObservableProperty]
    public partial string? ReplayTarget { get; set; }

    [ObservableProperty]
    public partial NetworkDevice? SelectedDevice { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSpeedTiming), nameof(IsFixedTiming))]
    public partial ReplayTimingOption SelectedTiming { get; set; }

    public bool IsSpeedTiming => SelectedTiming?.Value == ReplayTiming.Original;
    public bool IsFixedTiming => SelectedTiming?.Value == ReplayTiming.FixedInterval;

    [ObservableProperty]
    public partial decimal? Speed { get; set; } = 1;

    [ObservableProperty]
    public partial decimal? ReplayIntervalMs { get; set; } = 100;

    [ObservableProperty]
    public partial decimal? Loops { get; set; } = 1;

    [ObservableProperty]
    public partial double ReplayProgressPercent { get; set; }

    public bool HasReplayPackets => _replayPackets.Count > 0;

    /// <summary>Selects the packets to replay and switches to the Replay tab.</summary>
    public void SetReplayPackets(IReadOnlyList<PacketRecord> packets, string description)
    {
        _replayPackets = packets;
        var udp = packets.Count(p => p.Protocol == TransportProtocol.Udp);
        ReplaySource = L.Format("ReplaySource", packets.Count, description, udp, packets.Count - udp);
        if (packets.Any(p => p.Protocol == TransportProtocol.Tcp) && string.IsNullOrWhiteSpace(ReplayTarget)
            && packets.FirstOrDefault(p => p.Protocol == TransportProtocol.Tcp && p.Payload.Length > 0) is { } first)
        {
            ReplayTarget = PacketRecord.FormatEndpoint(first.DestinationAddress, first.DestinationPort);
        }
        OnPropertyChanged(nameof(HasReplayPackets));
        SelectedTabIndex = 1;
    }

    private async Task ReplayAsync(CancellationToken ct)
    {
        if (_replayPackets.Count == 0)
            throw new ArgumentException(L["ReplayNoPackets"]);

        var options = new ReplayOptions
        {
            Timing = SelectedTiming.Value,
            Speed = (double)Math.Max(0.01m, Speed ?? 1),
            Interval = TimeSpan.FromMilliseconds((double)Math.Max(0, ReplayIntervalMs ?? 0)),
            Loops = (int)Math.Max(0, Loops ?? 1),
        };

        ReplayProgressPercent = 0;
        long errors = 0;
        string? lastError = null;
        var progress = new UiProgress<ReplayProgress>(p =>
        {
            errors = p.Errors;
            lastError = p.LastError;
            ReplayProgressPercent = p.Total > 0 ? Math.Min(100, 100.0 * (p.Sent + p.Errors) / p.Total) : 0;
            Status = L.Format("ReplayProgressText", p.Sent, p.Total) + (p.Errors > 0 ? $" · {p.Errors} ✕ {p.LastError}" : "");
        });

        long sent;
        if (IsInjectMode)
        {
            var device = SelectedDevice ?? throw new ArgumentException(L["ReplayNoDevice"]);
            Status = L["SendSending"];
            sent = await FrameInjector.InjectAsync(device.Name, _replayPackets, options, progress, ct);
        }
        else
        {
            IPEndPoint? target = null;
            if (!string.IsNullOrWhiteSpace(ReplayTarget))
            {
                if (!HostPort.TryParse(ReplayTarget, out var host, out var port))
                    throw new ArgumentException(L["ReplayBadTarget"]);
                target = IPAddress.TryParse(host, out var address)
                    ? new IPEndPoint(address, port)
                    : new IPEndPoint((await Dns.GetHostAddressesAsync(host, ct)).First(), port);
            }
            Status = L["SendSending"];
            sent = await Replay.ReplayPayloadsAsync(_replayPackets, target, options, progress, ct);
        }

        ReplayProgressPercent = 100;
        HasError = errors > 0;
        Status = L.Format("ReplayDone", sent) + (errors > 0 ? $" · {errors} ✕ {lastError}" : "");
    }

    private static string ByteSize(long bytes) => Core.Statistics.ByteSize.Format(bytes);

    /// <summary>
    /// Reports synchronously on the UI thread, in order. Progress&lt;T&gt; posts asynchronously, so its last
    /// report could land after the final status was written and overwrite it.
    /// </summary>
    private sealed class UiProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value)
        {
            if (Dispatcher.UIThread.CheckAccess())
                handler(value);
            else
                Dispatcher.UIThread.Invoke(() => handler(value));
        }
    }
}
