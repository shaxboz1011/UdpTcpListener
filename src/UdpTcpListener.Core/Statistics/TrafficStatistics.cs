using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.Core.Statistics;

public sealed record TalkerStat(string Key, long Packets, long Bytes);

/// <summary>Running totals for the dashboard. Not thread-safe: feed it from a single thread.</summary>
public sealed class TrafficStatistics
{
    private readonly Dictionary<string, (long Packets, long Bytes)> _hosts = [];
    private readonly Dictionary<string, (long Packets, long Bytes)> _ports = [];

    public long TotalPackets { get; private set; }
    public long TotalBytes { get; private set; }
    public long UdpPackets { get; private set; }
    public long TcpPackets { get; private set; }

    public void Add(PacketRecord packet)
    {
        TotalPackets++;
        TotalBytes += packet.Length;
        if (packet.Protocol == TransportProtocol.Udp)
            UdpPackets++;
        else
            TcpPackets++;

        Bump(_hosts, packet.SourceAddress.ToString(), packet.Length);
        Bump(_hosts, packet.DestinationAddress.ToString(), packet.Length);

        // Count the lower (usually well-known / service) port once per packet.
        var servicePort = Math.Min(packet.SourcePort, packet.DestinationPort);
        Bump(_ports, $"{packet.ProtocolName}/{servicePort}", packet.Length);
    }

    public IReadOnlyList<TalkerStat> TopHosts(int count) => Top(_hosts, count);

    public IReadOnlyList<TalkerStat> TopPorts(int count) => Top(_ports, count);

    public void Reset()
    {
        TotalPackets = TotalBytes = UdpPackets = TcpPackets = 0;
        _hosts.Clear();
        _ports.Clear();
    }

    private static void Bump(Dictionary<string, (long Packets, long Bytes)> map, string key, int bytes)
    {
        map.TryGetValue(key, out var current);
        map[key] = (current.Packets + 1, current.Bytes + bytes);
    }

    private static List<TalkerStat> Top(Dictionary<string, (long Packets, long Bytes)> map, int count) =>
        map.OrderByDescending(kv => kv.Value.Bytes)
           .ThenBy(kv => kv.Key, StringComparer.Ordinal)
           .Take(count)
           .Select(kv => new TalkerStat(kv.Key, kv.Value.Packets, kv.Value.Bytes))
           .ToList();
}

public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(double bytes)
    {
        var unit = 0;
        while (Math.Abs(bytes) >= 1024 && unit < Units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:0} {Units[unit]}" : $"{bytes:0.##} {Units[unit]}";
    }
}
