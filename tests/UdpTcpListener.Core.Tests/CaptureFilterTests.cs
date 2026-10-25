using UdpTcpListener.Core.Capture;

namespace UdpTcpListener.Core.Tests;

public class CaptureFilterTests
{
    [Theory]
    [InlineData(ProtocolFilter.Both, "(udp or tcp)")]
    [InlineData(ProtocolFilter.Udp, "udp")]
    [InlineData(ProtocolFilter.Tcp, "tcp")]
    public void Builds_protocol_only(ProtocolFilter protocol, string expected) =>
        Assert.Equal(expected, new CaptureFilter { Protocol = protocol }.ToBpf());

    [Fact]
    public void Builds_full_filter()
    {
        var filter = new CaptureFilter
        {
            Protocol = ProtocolFilter.Udp,
            SourceHost = " 10.0.0.1 ",
            DestinationHost = "fe80::1",
            Port = 53,
            CustomExpression = "len > 100",
        };

        Assert.Equal("udp and src host 10.0.0.1 and dst host fe80::1 and port 53 and (len > 100)", filter.ToBpf());
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("10.0.0.1 or 1=1")]
    public void Rejects_invalid_addresses(string host) =>
        Assert.Throws<ArgumentException>(() => new CaptureFilter { SourceHost = host }.ToBpf());

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Rejects_invalid_ports(int port) =>
        Assert.Throws<ArgumentException>(() => new CaptureFilter { Port = port }.ToBpf());

    [Theory]
    [InlineData("udp", true, ProtocolFilter.Udp)]
    [InlineData(" Tcp ", true, ProtocolFilter.Tcp)]
    [InlineData("", true, ProtocolFilter.Both)]
    [InlineData("all", true, ProtocolFilter.Both)]
    [InlineData("icmp", false, ProtocolFilter.Both)]
    public void Parses_protocol(string input, bool ok, ProtocolFilter expected)
    {
        Assert.Equal(ok, CaptureFilter.TryParseProtocol(input, out var protocol));
        Assert.Equal(expected, protocol);
    }
}

public class CaptureFilterPredicateTests
{
    private static Packets.PacketRecord Udp(string src, string dst, ushort sp, ushort dp) =>
        Packets.PacketDecoder.TryDecode(PacketDotNet.LinkLayers.Ethernet, TestPackets.EthernetUdp(src, dst, sp, dp, [1]), TestPackets.Time, 1)!;

    private static Packets.PacketRecord Tcp(string src, string dst, ushort sp, ushort dp) =>
        Packets.PacketDecoder.TryDecode(PacketDotNet.LinkLayers.Ethernet, TestPackets.EthernetTcp(src, dst, sp, dp, [1]), TestPackets.Time, 1)!;

    [Fact]
    public void Predicate_matches_like_the_bpf_filter()
    {
        var match = new CaptureFilter { Protocol = ProtocolFilter.Udp, SourceHost = "10.0.0.1", Port = 53 }.ToPredicate();

        Assert.True(match(Udp("10.0.0.1", "8.8.8.8", 5000, 53)));
        Assert.True(match(Udp("10.0.0.1", "8.8.8.8", 53, 5000)));
        Assert.False(match(Tcp("10.0.0.1", "8.8.8.8", 5000, 53)));   // protocol
        Assert.False(match(Udp("10.0.0.2", "8.8.8.8", 5000, 53)));   // source
        Assert.False(match(Udp("10.0.0.1", "8.8.8.8", 5000, 54)));   // port
    }

    [Fact]
    public void Empty_filter_matches_everything() =>
        Assert.True(new CaptureFilter().ToPredicate()(Tcp("1.1.1.1", "2.2.2.2", 1, 2)));

    [Fact]
    public void Predicate_validates_input() =>
        Assert.Throws<ArgumentException>(() => new CaptureFilter { DestinationHost = "nope" }.ToPredicate());
}
