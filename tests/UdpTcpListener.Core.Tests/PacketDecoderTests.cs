using PacketDotNet;
using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.Core.Tests;

public class PacketDecoderTests
{
    [Fact]
    public void Decodes_ethernet_udp()
    {
        var data = TestPackets.EthernetUdp("10.0.0.1", "10.0.0.2", 5000, 53, [1, 2, 3]);

        var record = PacketDecoder.TryDecode(LinkLayers.Ethernet, data, TestPackets.Time, 7);

        Assert.NotNull(record);
        Assert.Equal(7, record.Number);
        Assert.Equal(TransportProtocol.Udp, record.Protocol);
        Assert.Equal("10.0.0.1:5000", record.Source);
        Assert.Equal("10.0.0.2:53", record.Destination);
        Assert.Equal("00:11:22:33:44:55", record.SourceMac);
        Assert.Equal("66:77:88:99:AA:BB", record.DestinationMac);
        Assert.Equal([1, 2, 3], record.Payload);
        Assert.Null(record.TcpFlags);
        Assert.Equal(data.Length, record.Length);
    }

    [Fact]
    public void Decodes_tcp_flags()
    {
        var data = TestPackets.EthernetTcp("192.168.1.10", "192.168.1.20", 40000, 443, [], syn: true, ack: true);

        var record = PacketDecoder.TryDecode(LinkLayers.Ethernet, data, TestPackets.Time, 1);

        Assert.NotNull(record);
        Assert.Equal(TransportProtocol.Tcp, record.Protocol);
        Assert.Equal("SYN, ACK", record.TcpFlags);
        Assert.Contains("[SYN, ACK]", record.Info);
    }

    [Fact]
    public void Decodes_ipv6_with_bracketed_endpoints()
    {
        var data = TestPackets.EthernetUdp("fe80::1", "fe80::2", 1, 2, [9]);

        var record = PacketDecoder.TryDecode(LinkLayers.Ethernet, data, TestPackets.Time, 1);

        Assert.NotNull(record);
        Assert.Equal("[fe80::1]:1", record.Source);
        Assert.Equal("[fe80::2]:2", record.Destination);
    }

    [Fact]
    public void Decodes_non_ethernet_link_layers()
    {
        // The original tool dropped everything that was not Ethernet (Wi-Fi, loopback, tun, Linux "any").
        var data = TestPackets.RawIpUdp("10.8.0.1", "10.8.0.2", 1194, 1194, [0xAA]);

        var record = PacketDecoder.TryDecode(LinkLayers.Raw, data, TestPackets.Time, 1);

        Assert.NotNull(record);
        Assert.Null(record.SourceMac);
        Assert.Equal("10.8.0.1:1194", record.Source);
    }

    [Fact]
    public void Returns_null_for_garbage()
    {
        Assert.Null(PacketDecoder.TryDecode(LinkLayers.Ethernet, [1, 2, 3], TestPackets.Time, 1));
    }

    [Fact]
    public void Describes_all_layers()
    {
        var data = TestPackets.EthernetTcp("10.0.0.1", "10.0.0.2", 1234, 80, "GET /"u8.ToArray(), syn: false, ack: true);
        var record = PacketDecoder.TryDecode(LinkLayers.Ethernet, data, TestPackets.Time, 1)!;

        var layers = PacketDecoder.DescribeLayers(record);

        Assert.Equal(["Frame", "Ethernet II", "Internet Protocol Version 4", "Transmission Control Protocol"], layers.Select(l => l.Name));
        Assert.Contains(layers[3].Fields, f => f is { Name: "Flags", Value: "ACK" });
    }
}
