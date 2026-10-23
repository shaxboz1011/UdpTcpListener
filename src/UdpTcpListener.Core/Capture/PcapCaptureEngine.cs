using System.Net.Sockets;
using SharpPcap;
using SharpPcap.LibPcap;
using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.Core.Capture;

public sealed record NetworkDevice(string Name, string Description, IReadOnlyList<string> Addresses)
{
    public string DisplayName => Addresses.Count == 0 ? Description : $"{Description}  ({string.Join(", ", Addresses)})";
}

public sealed record CaptureStatistics(long Received, long Dropped, long InterfaceDropped);

/// <summary>Thrown when libpcap (Linux) or Npcap (Windows) is missing or cannot be loaded.</summary>
public sealed class CaptureLibraryUnavailableException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// Live capture through SharpPcap. Packets are decoded on the capture thread and raised via
/// <see cref="PacketCaptured"/>; subscribers must marshal to their own thread if needed.
/// </summary>
public sealed class PcapCaptureEngine : IDisposable
{
    public const string NpcapDownloadUrl = "https://npcap.com/#download";

    private readonly Lock _gate = new();
    private ILiveDevice? _device;
    private long _counter;

    public event EventHandler<PacketRecord>? PacketCaptured;

    public bool IsRunning { get; private set; }

    /// <summary>Lists capture devices. Throws <see cref="CaptureLibraryUnavailableException"/> if pcap is missing.</summary>
    public static IReadOnlyList<NetworkDevice> GetDevices()
    {
        CaptureDeviceList list;
        try
        {
            list = CaptureDeviceList.New();
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or PcapException or BadImageFormatException)
        {
            throw new CaptureLibraryUnavailableException(
                OperatingSystem.IsWindows()
                    ? "Npcap is not installed. Install it from https://npcap.com (enable \"WinPcap API-compatible mode\")."
                    : "libpcap is not installed. Install it with your package manager (e.g. 'sudo apt install libpcap0.8').",
                ex);
        }

        return list.Select(ToNetworkDevice).ToList();
    }

    /// <summary>Opens the device in promiscuous mode with the given filter and starts capturing.</summary>
    public void Start(string deviceName, CaptureFilter filter)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceName);
        var bpf = filter.ToBpf();

        lock (_gate)
        {
            if (IsRunning)
                throw new InvalidOperationException("Capture is already running.");

            var device = CaptureDeviceList.New().FirstOrDefault(d => d.Name == deviceName)
                ?? throw new InvalidOperationException($"Device '{deviceName}' was not found.");

            device.Open(DeviceModes.Promiscuous, read_timeout: 500);
            try
            {
                device.Filter = bpf;
            }
            catch
            {
                device.Close();
                throw;
            }

            device.OnPacketArrival += OnPacketArrival;
            Interlocked.Exchange(ref _counter, 0);
            device.StartCapture();

            _device = device;
            IsRunning = true;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_device is null)
                return;

            try
            {
                _device.StopCapture();
            }
            finally
            {
                _device.OnPacketArrival -= OnPacketArrival;
                _device.Close();
                _device = null;
                IsRunning = false;
            }
        }
    }

    public CaptureStatistics? GetStatistics()
    {
        lock (_gate)
        {
            var stats = _device?.Statistics;
            return stats is null ? null : new CaptureStatistics(stats.ReceivedPackets, stats.DroppedPackets, stats.InterfaceDroppedPackets);
        }
    }

    public void Dispose() => Stop();

    private void OnPacketArrival(object sender, PacketCapture e)
    {
        var raw = e.GetPacket();
        var record = PacketDecoder.TryDecode(
            raw.LinkLayerType,
            raw.Data,
            new DateTimeOffset(raw.Timeval.Date, TimeSpan.Zero).ToLocalTime(),
            Interlocked.Increment(ref _counter),
            raw.PacketLength);

        if (record is not null)
            PacketCaptured?.Invoke(this, record);
    }

    private static NetworkDevice ToNetworkDevice(ILiveDevice device)
    {
        var addresses = new List<string>();
        string? friendly = null;

        if (device is LibPcapLiveDevice pcap)
        {
            friendly = pcap.Interface?.FriendlyName;
            addresses.AddRange((pcap.Addresses ?? [])
                .Select(a => a.Addr?.ipAddress)
                .Where(ip => ip is { AddressFamily: AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 })
                .Select(ip => ip!.ToString()));
        }

        var description = !string.IsNullOrWhiteSpace(friendly) ? friendly
            : !string.IsNullOrWhiteSpace(device.Description) ? device.Description
            : device.Name;

        return new NetworkDevice(device.Name, description, addresses);
    }
}
