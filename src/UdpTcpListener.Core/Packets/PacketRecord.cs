using System.Net;
using System.Net.Sockets;
using PacketDotNet;

namespace UdpTcpListener.Core.Packets;

public enum TransportProtocol
{
    Udp,
    Tcp,
}

/// <summary>
/// A single captured UDP or TCP packet, decoded into the fields the UI and exporters need.
/// The original frame is kept in <see cref="RawData"/> so it can be re-exported to PCAP.
/// </summary>
public sealed record PacketRecord
{
    public required long Number { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required TransportProtocol Protocol { get; init; }

    public string? SourceMac { get; init; }
    public string? DestinationMac { get; init; }
    public required IPAddress SourceAddress { get; init; }
    public required IPAddress DestinationAddress { get; init; }
    public required ushort SourcePort { get; init; }
    public required ushort DestinationPort { get; init; }

    /// <summary>TCP flags such as "SYN, ACK"; null for UDP.</summary>
    public string? TcpFlags { get; init; }

    /// <summary>TCP sequence number (used to reassemble streams); null for UDP.</summary>
    public uint? TcpSequence { get; init; }

    /// <summary>True when the TCP SYN flag is set (the stream starts at sequence + 1).</summary>
    public bool TcpSyn { get; init; }

    public required byte[] Payload { get; init; }
    public required LinkLayers LinkType { get; init; }
    public required byte[] RawData { get; init; }

    /// <summary>Original length on the wire (may exceed <see cref="RawData"/> when the snapshot was truncated).</summary>
    public int Length { get; init; }

    public string ProtocolName => Protocol == TransportProtocol.Udp ? "UDP" : "TCP";
    public string Source => FormatEndpoint(SourceAddress, SourcePort);
    public string Destination => FormatEndpoint(DestinationAddress, DestinationPort);
    public int PayloadLength => Payload.Length;

    public string Info => TcpFlags is null
        ? $"{SourcePort} → {DestinationPort}  Len={Payload.Length}"
        : $"{SourcePort} → {DestinationPort}  [{TcpFlags}]  Len={Payload.Length}";

    public static string FormatEndpoint(IPAddress address, ushort port) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";
}
