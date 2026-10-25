using System.Net;
using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.Core.Analysis;

/// <summary>
/// Identifies a conversation by protocol and its two endpoints, independent of direction:
/// A is always the endpoint that sorts first, so both directions map to the same key.
/// </summary>
public readonly record struct ConversationKey(TransportProtocol Protocol, IPAddress AddressA, ushort PortA, IPAddress AddressB, ushort PortB)
{
    public string EndpointA => PacketRecord.FormatEndpoint(AddressA, PortA);
    public string EndpointB => PacketRecord.FormatEndpoint(AddressB, PortB);

    public static ConversationKey From(PacketRecord packet)
    {
        var src = (packet.SourceAddress, packet.SourcePort);
        var dst = (packet.DestinationAddress, packet.DestinationPort);
        return Compare(src, dst) <= 0
            ? new(packet.Protocol, src.SourceAddress, src.SourcePort, dst.DestinationAddress, dst.DestinationPort)
            : new(packet.Protocol, dst.DestinationAddress, dst.DestinationPort, src.SourceAddress, src.SourcePort);
    }

    /// <summary>True if <paramref name="packet"/> travels from A to B.</summary>
    public bool IsAToB(PacketRecord packet) =>
        packet.SourcePort == PortA && packet.SourceAddress.Equals(AddressA)
        && packet.DestinationPort == PortB && packet.DestinationAddress.Equals(AddressB);

    public bool Matches(PacketRecord packet) => packet.Protocol == Protocol && From(packet) == this;

    private static int Compare((IPAddress Address, ushort Port) x, (IPAddress Address, ushort Port) y)
    {
        var bytes = x.Address.GetAddressBytes().AsSpan().SequenceCompareTo(y.Address.GetAddressBytes());
        return bytes != 0 ? bytes : x.Port.CompareTo(y.Port);
    }
}

/// <summary>Totals for one conversation at a point in time.</summary>
public sealed record ConversationSummary(
    ConversationKey Key,
    long PacketsAToB,
    long BytesAToB,
    long PacketsBToA,
    long BytesBToA,
    DateTimeOffset Start,
    DateTimeOffset End)
{
    public string ProtocolName => Key.Protocol == TransportProtocol.Udp ? "UDP" : "TCP";
    public TransportProtocol Protocol => Key.Protocol;
    public string EndpointA => Key.EndpointA;
    public string EndpointB => Key.EndpointB;
    public long Packets => PacketsAToB + PacketsBToA;
    public long Bytes => BytesAToB + BytesBToA;
    public TimeSpan Duration => End - Start;
}

/// <summary>Groups packets into conversations. Not thread-safe: feed it from a single thread.</summary>
public sealed class ConversationTracker
{
    private readonly Dictionary<ConversationKey, Entry> _entries = [];

    public int Count => _entries.Count;

    public void Add(PacketRecord packet)
    {
        var key = ConversationKey.From(packet);
        if (!_entries.TryGetValue(key, out var entry))
            _entries[key] = entry = new Entry { Start = packet.Timestamp, End = packet.Timestamp };

        if (key.IsAToB(packet))
        {
            entry.PacketsAToB++;
            entry.BytesAToB += packet.Length;
        }
        else
        {
            entry.PacketsBToA++;
            entry.BytesBToA += packet.Length;
        }

        if (packet.Timestamp < entry.Start)
            entry.Start = packet.Timestamp;
        if (packet.Timestamp > entry.End)
            entry.End = packet.Timestamp;
    }

    /// <summary>Conversations ordered by total bytes, largest first.</summary>
    public IReadOnlyList<ConversationSummary> Snapshot() =>
        _entries.Select(kv => new ConversationSummary(kv.Key, kv.Value.PacketsAToB, kv.Value.BytesAToB,
                                                      kv.Value.PacketsBToA, kv.Value.BytesBToA, kv.Value.Start, kv.Value.End))
                .OrderByDescending(c => c.Bytes)
                .ThenBy(c => c.Start)
                .ToList();

    public void Clear() => _entries.Clear();

    private sealed class Entry
    {
        public long PacketsAToB;
        public long BytesAToB;
        public long PacketsBToA;
        public long BytesBToA;
        public DateTimeOffset Start;
        public DateTimeOffset End;
    }
}
