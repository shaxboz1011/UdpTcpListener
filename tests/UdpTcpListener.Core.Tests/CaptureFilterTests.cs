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
