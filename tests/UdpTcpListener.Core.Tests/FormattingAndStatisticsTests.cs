using PacketDotNet;
using UdpTcpListener.Core.Formatting;
using UdpTcpListener.Core.Packets;
using UdpTcpListener.Core.Statistics;

namespace UdpTcpListener.Core.Tests;

public class FormattingAndStatisticsTests
{
    [Fact]
    public void Hex_dump_formats_offset_hex_and_ascii()
    {
        var dump = HexDump.Format("Hello, World!\n0123"u8);
        var lines = dump.Split('\n');

        Assert.Equal(2, lines.Length);
        Assert.Equal("0000  48 65 6c 6c 6f 2c 20 57  6f 72 6c 64 21 0a 30 31  Hello, World!.01", lines[0]);
        Assert.StartsWith("0010  32 33 ", lines[1]);
        Assert.EndsWith(" 23", lines[1]);
    }

    [Fact]
    public void Hex_dump_of_empty_is_empty() => Assert.Equal("", HexDump.Format([]));

    [Fact]
    public void Ascii_truncates() => Assert.Equal("ab.…", HexDump.ToAscii([0x61, 0x62, 0x00, 0x63], max: 3));

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5 * 1024 * 1024, "5 MB")]
    public void Formats_byte_sizes(double bytes, string expected) => Assert.Equal(expected, ByteSize.Format(bytes));

    [Fact]
    public void Statistics_track_totals_and_top_talkers()
    {
        var stats = new TrafficStatistics();
        stats.Add(Decode(TestPackets.EthernetUdp("10.0.0.1", "10.0.0.2", 5000, 53, new byte[100])));
        stats.Add(Decode(TestPackets.EthernetUdp("10.0.0.1", "10.0.0.3", 5001, 53, new byte[100])));
        stats.Add(Decode(TestPackets.EthernetTcp("10.0.0.4", "10.0.0.5", 40000, 443, [])));

        Assert.Equal(3, stats.TotalPackets);
        Assert.Equal(2, stats.UdpPackets);
        Assert.Equal(1, stats.TcpPackets);
        Assert.Equal("10.0.0.1", stats.TopHosts(1)[0].Key);
        Assert.Equal(2, stats.TopHosts(1)[0].Packets);
        Assert.Equal("UDP/53", stats.TopPorts(1)[0].Key);

        stats.Reset();
        Assert.Equal(0, stats.TotalPackets);
        Assert.Empty(stats.TopHosts(5));
    }

    private static PacketRecord Decode(byte[] data) => PacketDecoder.TryDecode(LinkLayers.Ethernet, data, TestPackets.Time, 1)!;
}

public class ReadableTextTests
{
    [Fact]
    public void Keeps_line_breaks_and_tabs_and_masks_binary() =>
        Assert.Equal("GET / HTTP/1.1\nHost:\tx\n..\r", Formatting.HexDump.ToReadableText("GET / HTTP/1.1\r\nHost:\tx\n\0\u0001\r"u8));
}
