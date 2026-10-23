using PacketDotNet;

namespace UdpTcpListener.Core.Packets;

/// <summary>One protocol layer of a packet, as shown in the details tree.</summary>
public sealed record PacketLayer(string Name, IReadOnlyList<PacketField> Fields);

public sealed record PacketField(string Name, string Value);

public static class PacketDecoder
{
    /// <summary>
    /// Decodes a raw frame into a <see cref="PacketRecord"/>. Works for any link layer libpcap
    /// reports (Ethernet, Wi-Fi, Linux "any", loopback, raw IP), not only Ethernet.
    /// Returns null for frames that are not UDP or TCP over IPv4/IPv6.
    /// </summary>
    public static PacketRecord? TryDecode(LinkLayers linkType, byte[] data, DateTimeOffset timestamp, long number, int? wireLength = null)
    {
        // PacketDotNet parses lazily, so malformed frames can throw from any property access, not just ParsePacket.
        try
        {
            return Decode(linkType, data, timestamp, number, wireLength);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static PacketRecord? Decode(LinkLayers linkType, byte[] data, DateTimeOffset timestamp, long number, int? wireLength)
    {
        var parsed = Packet.ParsePacket(linkType, data);
        var ip = parsed.Extract<IPPacket>();
        if (ip is null)
            return null;

        var ethernet = parsed.Extract<EthernetPacket>();

        TransportProtocol protocol;
        ushort srcPort, dstPort;
        string? flags = null;
        byte[] payload;

        if (ip.Extract<UdpPacket>() is { } udp)
        {
            protocol = TransportProtocol.Udp;
            srcPort = udp.SourcePort;
            dstPort = udp.DestinationPort;
            payload = udp.PayloadData ?? [];
        }
        else if (ip.Extract<TcpPacket>() is { } tcp)
        {
            protocol = TransportProtocol.Tcp;
            srcPort = tcp.SourcePort;
            dstPort = tcp.DestinationPort;
            flags = FormatTcpFlags(tcp);
            payload = tcp.PayloadData ?? [];
        }
        else
        {
            return null;
        }

        return new PacketRecord
        {
            Number = number,
            Timestamp = timestamp,
            Protocol = protocol,
            SourceMac = ethernet is null ? null : FormatMac(ethernet.SourceHardwareAddress.GetAddressBytes()),
            DestinationMac = ethernet is null ? null : FormatMac(ethernet.DestinationHardwareAddress.GetAddressBytes()),
            SourceAddress = ip.SourceAddress,
            DestinationAddress = ip.DestinationAddress,
            SourcePort = srcPort,
            DestinationPort = dstPort,
            TcpFlags = flags,
            Payload = payload,
            LinkType = linkType,
            RawData = data,
            Length = wireLength ?? data.Length,
        };
    }

    /// <summary>Builds the layer-by-layer breakdown shown in the packet details pane.</summary>
    public static IReadOnlyList<PacketLayer> DescribeLayers(PacketRecord record)
    {
        var layers = new List<PacketLayer>
        {
            new("Frame",
            [
                new("Number", record.Number.ToString()),
                new("Arrival time", record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.ffffff zzz")),
                new("Length", $"{record.Length} bytes"),
                new("Captured", $"{record.RawData.Length} bytes"),
                new("Link type", record.LinkType.ToString()),
            ]),
        };

        try
        {
            AddProtocolLayers(layers, record);
        }
        catch (Exception)
        {
            // Malformed frame: show whatever layers decoded cleanly.
        }

        return layers;
    }

    private static void AddProtocolLayers(List<PacketLayer> layers, PacketRecord record)
    {
        var parsed = Packet.ParsePacket(record.LinkType, record.RawData);

        if (parsed.Extract<EthernetPacket>() is { } eth)
        {
            layers.Add(new("Ethernet II",
            [
                new("Source", FormatMac(eth.SourceHardwareAddress.GetAddressBytes())),
                new("Destination", FormatMac(eth.DestinationHardwareAddress.GetAddressBytes())),
                new("Type", eth.Type.ToString()),
            ]));
        }

        switch (parsed.Extract<IPPacket>())
        {
            case IPv4Packet v4:
                layers.Add(new("Internet Protocol Version 4",
                [
                    new("Source", v4.SourceAddress.ToString()),
                    new("Destination", v4.DestinationAddress.ToString()),
                    new("Header length", $"{v4.HeaderLength * 4} bytes"),
                    new("Total length", v4.TotalLength.ToString()),
                    new("Identification", $"0x{v4.Id:x4} ({v4.Id})"),
                    new("Time to live", v4.TimeToLive.ToString()),
                    new("Protocol", v4.Protocol.ToString()),
                    new("Header checksum", $"0x{v4.Checksum:x4}"),
                ]));
                break;
            case IPv6Packet v6:
                layers.Add(new("Internet Protocol Version 6",
                [
                    new("Source", v6.SourceAddress.ToString()),
                    new("Destination", v6.DestinationAddress.ToString()),
                    new("Payload length", v6.PayloadLength.ToString()),
                    new("Next header", v6.NextHeader.ToString()),
                    new("Hop limit", v6.HopLimit.ToString()),
                    new("Flow label", $"0x{v6.FlowLabel:x5}"),
                ]));
                break;
        }

        if (parsed.Extract<UdpPacket>() is { } udp)
        {
            layers.Add(new("User Datagram Protocol",
            [
                new("Source port", udp.SourcePort.ToString()),
                new("Destination port", udp.DestinationPort.ToString()),
                new("Length", udp.Length.ToString()),
                new("Checksum", $"0x{udp.Checksum:x4}"),
                new("Payload", $"{record.Payload.Length} bytes"),
            ]));
        }
        else if (parsed.Extract<TcpPacket>() is { } tcp)
        {
            layers.Add(new("Transmission Control Protocol",
            [
                new("Source port", tcp.SourcePort.ToString()),
                new("Destination port", tcp.DestinationPort.ToString()),
                new("Sequence number", tcp.SequenceNumber.ToString()),
                new("Acknowledgment number", tcp.AcknowledgmentNumber.ToString()),
                new("Flags", FormatTcpFlags(tcp) is { Length: > 0 } f ? f : "(none)"),
                new("Window size", tcp.WindowSize.ToString()),
                new("Checksum", $"0x{tcp.Checksum:x4}"),
                new("Payload", $"{record.Payload.Length} bytes"),
            ]));
        }
    }

    internal static string FormatTcpFlags(TcpPacket tcp)
    {
        var flags = new List<string>(4);
        if (tcp.Synchronize) flags.Add("SYN");
        if (tcp.Acknowledgment) flags.Add("ACK");
        if (tcp.Push) flags.Add("PSH");
        if (tcp.Finished) flags.Add("FIN");
        if (tcp.Reset) flags.Add("RST");
        if (tcp.Urgent) flags.Add("URG");
        return string.Join(", ", flags);
    }

    internal static string FormatMac(byte[] bytes) => Convert.ToHexString(bytes) switch
    {
        { Length: 12 } hex => string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))),
        var hex => hex,
    };
}
