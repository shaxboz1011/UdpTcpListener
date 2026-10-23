using System.Net;
using System.Net.NetworkInformation;
using PacketDotNet;

namespace UdpTcpListener.Core.Tests;

internal static class TestPackets
{
    public static readonly DateTimeOffset Time = new(2026, 1, 2, 3, 4, 5, 678, TimeSpan.Zero);

    public static byte[] EthernetUdp(string src, string dst, ushort srcPort, ushort dstPort, byte[] payload)
    {
        var udp = new UdpPacket(srcPort, dstPort) { PayloadData = payload };
        var ip = CreateIp(src, dst);
        ip.PayloadPacket = udp;
        var eth = new EthernetPacket(
            PhysicalAddress.Parse("00-11-22-33-44-55"),
            PhysicalAddress.Parse("66-77-88-99-AA-BB"),
            ip is IPv6Packet ? EthernetType.IPv6 : EthernetType.IPv4) { PayloadPacket = ip };
        udp.UpdateCalculatedValues();
        ip.UpdateCalculatedValues();
        return eth.Bytes;
    }

    public static byte[] EthernetTcp(string src, string dst, ushort srcPort, ushort dstPort, byte[] payload, bool syn = false, bool ack = false)
    {
        var tcp = new TcpPacket(srcPort, dstPort) { PayloadData = payload, Synchronize = syn, Acknowledgment = ack };
        var ip = CreateIp(src, dst);
        ip.PayloadPacket = tcp;
        var eth = new EthernetPacket(
            PhysicalAddress.Parse("00-11-22-33-44-55"),
            PhysicalAddress.Parse("66-77-88-99-AA-BB"),
            EthernetType.IPv4) { PayloadPacket = ip };
        tcp.UpdateCalculatedValues();
        ip.UpdateCalculatedValues();
        return eth.Bytes;
    }

    /// <summary>Raw IP frame without a link-layer header (DLT_RAW), as seen on tun devices.</summary>
    public static byte[] RawIpUdp(string src, string dst, ushort srcPort, ushort dstPort, byte[] payload)
    {
        var udp = new UdpPacket(srcPort, dstPort) { PayloadData = payload };
        var ip = CreateIp(src, dst);
        ip.PayloadPacket = udp;
        udp.UpdateCalculatedValues();
        ip.UpdateCalculatedValues();
        return ip.Bytes;
    }

    private static IPPacket CreateIp(string src, string dst)
    {
        var s = IPAddress.Parse(src);
        var d = IPAddress.Parse(dst);
        return s.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? new IPv6Packet(s, d)
            : new IPv4Packet(s, d);
    }
}
