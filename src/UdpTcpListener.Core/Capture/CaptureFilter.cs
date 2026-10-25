using System.Net;
using System.Text;
using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.Core.Capture;

public enum ProtocolFilter
{
    Both,
    Udp,
    Tcp,
}

/// <summary>What to capture. Turned into a kernel-level BPF filter by <see cref="ToBpf"/>.</summary>
public sealed record CaptureFilter
{
    public ProtocolFilter Protocol { get; init; } = ProtocolFilter.Both;

    /// <summary>Only packets from this IPv4/IPv6 address (null or empty = any).</summary>
    public string? SourceHost { get; init; }

    /// <summary>Only packets to this IPv4/IPv6 address (null or empty = any).</summary>
    public string? DestinationHost { get; init; }

    /// <summary>Only packets where either side uses this port.</summary>
    public int? Port { get; init; }

    /// <summary>Extra raw BPF expression, ANDed with the rest (advanced users).</summary>
    public string? CustomExpression { get; init; }

    /// <summary>
    /// Builds the BPF expression. Filtering in the kernel is much cheaper than parsing every
    /// packet in managed code and then discarding it.
    /// </summary>
    /// <exception cref="ArgumentException">An address or port is invalid.</exception>
    public string ToBpf()
    {
        var sb = new StringBuilder(Protocol switch
        {
            ProtocolFilter.Udp => "udp",
            ProtocolFilter.Tcp => "tcp",
            _ => "(udp or tcp)",
        });

        if (!string.IsNullOrWhiteSpace(SourceHost))
            sb.Append(" and src host ").Append(ParseAddress(SourceHost, nameof(SourceHost)));

        if (!string.IsNullOrWhiteSpace(DestinationHost))
            sb.Append(" and dst host ").Append(ParseAddress(DestinationHost, nameof(DestinationHost)));

        if (Port is { } port)
        {
            if (port is < 0 or > ushort.MaxValue)
                throw new ArgumentException($"Port must be between 0 and {ushort.MaxValue}.", nameof(Port));
            sb.Append(" and port ").Append(port);
        }

        if (!string.IsNullOrWhiteSpace(CustomExpression))
            sb.Append(" and (").Append(CustomExpression.Trim()).Append(')');

        return sb.ToString();
    }

    /// <summary>
    /// Applies protocol, host and port in managed code, for packets that did not go through the
    /// kernel filter (e.g. read from a file). <see cref="CustomExpression"/> cannot be evaluated here.
    /// </summary>
    /// <exception cref="ArgumentException">An address or port is invalid.</exception>
    public Func<PacketRecord, bool> ToPredicate()
    {
        _ = ToBpf(); // validates addresses and port
        var src = string.IsNullOrWhiteSpace(SourceHost) ? null : IPAddress.Parse(SourceHost.Trim());
        var dst = string.IsNullOrWhiteSpace(DestinationHost) ? null : IPAddress.Parse(DestinationHost.Trim());
        var protocol = Protocol;
        var port = Port;

        return packet =>
            (protocol == ProtocolFilter.Both
             || (protocol == ProtocolFilter.Udp) == (packet.Protocol == TransportProtocol.Udp))
            && (src is null || packet.SourceAddress.Equals(src))
            && (dst is null || packet.DestinationAddress.Equals(dst))
            && (port is null || packet.SourcePort == port || packet.DestinationPort == port);
    }

    public static bool TryParseProtocol(string? value, out ProtocolFilter protocol)
    {
        switch (value?.Trim().ToUpperInvariant())
        {
            case null or "" or "BOTH" or "ALL" or "ANY":
                protocol = ProtocolFilter.Both;
                return true;
            case "UDP":
                protocol = ProtocolFilter.Udp;
                return true;
            case "TCP":
                protocol = ProtocolFilter.Tcp;
                return true;
            default:
                protocol = ProtocolFilter.Both;
                return false;
        }
    }

    private static string ParseAddress(string value, string paramName) =>
        IPAddress.TryParse(value.Trim(), out var address)
            ? address.ToString()
            : throw new ArgumentException($"'{value}' is not a valid IPv4 or IPv6 address.", paramName);
}
