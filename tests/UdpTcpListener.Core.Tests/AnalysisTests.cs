using System.Text;
using PacketDotNet;
using UdpTcpListener.Core.Analysis;
using UdpTcpListener.Core.Formatting;
using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.Core.Tests;

public class AnalysisTests
{
    private const string Client = "10.0.0.1";
    private const string Server = "10.0.0.2";

    private static long _number;

    private static PacketRecord Tcp(string src, string dst, ushort sp, ushort dp, string data, uint seq, bool syn = false, double ms = 0) =>
        PacketDecoder.TryDecode(LinkLayers.Ethernet,
            TestPackets.EthernetTcp(src, dst, sp, dp, Encoding.ASCII.GetBytes(data), syn: syn, ack: !syn, seq: seq),
            TestPackets.Time.AddMilliseconds(ms), Interlocked.Increment(ref _number))!;

    private static PacketRecord Udp(string src, string dst, ushort sp, ushort dp, string data, double ms = 0) =>
        PacketDecoder.TryDecode(LinkLayers.Ethernet,
            TestPackets.EthernetUdp(src, dst, sp, dp, Encoding.ASCII.GetBytes(data)),
            TestPackets.Time.AddMilliseconds(ms), Interlocked.Increment(ref _number))!;

    private static string Text(FollowedStream stream) =>
        string.Join("|", stream.Chunks.Select(c => (c.Direction == StreamDirection.AToB ? ">" : "<") + Encoding.ASCII.GetString(c.Data)));

    [Fact]
    public void Key_is_the_same_for_both_directions()
    {
        var request = Tcp(Client, Server, 40000, 80, "a", 1);
        var reply = Tcp(Server, Client, 80, 40000, "b", 1);

        var key = ConversationKey.From(request);

        Assert.Equal(key, ConversationKey.From(reply));
        Assert.True(key.IsAToB(request));
        Assert.False(key.IsAToB(reply));
        Assert.Equal("10.0.0.1:40000", key.EndpointA);
    }

    [Fact]
    public void Udp_and_tcp_on_same_ports_are_different_conversations()
    {
        Assert.NotEqual(ConversationKey.From(Tcp(Client, Server, 5000, 53, "", 1)),
                        ConversationKey.From(Udp(Client, Server, 5000, 53, "")));
    }

    [Fact]
    public void Tracker_sums_each_direction_and_duration()
    {
        var tracker = new ConversationTracker();
        tracker.Add(Tcp(Client, Server, 40000, 80, "GET", 1, ms: 0));
        tracker.Add(Tcp(Server, Client, 80, 40000, "200 OK", 1, ms: 250));
        tracker.Add(Tcp(Client, Server, 40000, 80, "", 4, ms: 500));
        tracker.Add(Udp(Client, "8.8.8.8", 5353, 53, "q", ms: 10));

        var all = tracker.Snapshot();

        Assert.Equal(2, all.Count);
        var http = all.Single(c => c.Protocol == TransportProtocol.Tcp);
        Assert.Equal(2, http.PacketsAToB);
        Assert.Equal(1, http.PacketsBToA);
        Assert.Equal(3, http.Packets);
        Assert.Equal(TimeSpan.FromMilliseconds(500), http.Duration);
        Assert.Same(all[0], http); // largest first
    }

    [Fact]
    public void Follows_tcp_request_and_response()
    {
        var packets = new[]
        {
            Tcp(Client, Server, 40000, 80, "", 1000, syn: true),
            Tcp(Server, Client, 80, 40000, "", 5000, syn: true),
            Tcp(Client, Server, 40000, 80, "GET / HTTP/1.1\r\n", 1001),
            Tcp(Client, Server, 40000, 80, "Host: x\r\n\r\n", 1017),
            Tcp(Server, Client, 80, 40000, "HTTP/1.1 200 OK\r\n", 5001),
        };

        var stream = StreamReassembler.Follow(packets, ConversationKey.From(packets[0]));

        Assert.Equal(">GET / HTTP/1.1\r\nHost: x\r\n\r\n|<HTTP/1.1 200 OK\r\n", Text(stream));
        Assert.Equal(27, stream.BytesAToB);
    }

    [Fact]
    public void Reorders_out_of_order_segments_and_drops_retransmissions()
    {
        var packets = new[]
        {
            Tcp(Client, Server, 1, 2, "", 99, syn: true),
            Tcp(Client, Server, 1, 2, "world", 106),   // arrives early
            Tcp(Client, Server, 1, 2, "hello ", 100),
            Tcp(Client, Server, 1, 2, "hello ", 100),  // retransmission
            Tcp(Client, Server, 1, 2, "lo wor", 103),  // overlapping retransmission
            Tcp(Client, Server, 1, 2, "ld!", 109),     // partly new
        };

        var stream = StreamReassembler.Follow(packets, ConversationKey.From(packets[0]));

        Assert.Equal(">hello world!", Text(stream));
    }

    [Fact]
    public void Starts_mid_stream_without_syn()
    {
        var packets = new[]
        {
            Tcp(Client, Server, 1, 2, "abc", 4_000_000_000),
            Tcp(Client, Server, 1, 2, "def", 4_000_000_003),
        };

        Assert.Equal(">abcdef", Text(StreamReassembler.Follow(packets, ConversationKey.From(packets[0]))));
    }

    [Fact]
    public void Handles_sequence_number_wraparound()
    {
        var packets = new[]
        {
            Tcp(Client, Server, 1, 2, "ab", uint.MaxValue - 1),
            Tcp(Client, Server, 1, 2, "cd", 0),
        };

        Assert.Equal(">abcd", Text(StreamReassembler.Follow(packets, ConversationKey.From(packets[0]))));
    }

    [Fact]
    public void Shows_data_after_a_gap()
    {
        var packets = new[]
        {
            Tcp(Client, Server, 1, 2, "", 0, syn: true),
            Tcp(Client, Server, 1, 2, "one", 1),
            Tcp(Client, Server, 1, 2, "three", 10), // bytes 4..9 were never captured
        };

        Assert.Equal(">onethree", Text(StreamReassembler.Follow(packets, ConversationKey.From(packets[0]))));
    }

    [Fact]
    public void Follows_udp_in_arrival_order_and_ignores_other_conversations()
    {
        var packets = new[]
        {
            Udp(Client, Server, 5000, 6000, "ping"),
            Udp(Client, Server, 5001, 6000, "other"),
            Udp(Server, Client, 6000, 5000, "pong"),
            Udp(Server, Client, 6000, 5000, "!"),
        };

        Assert.Equal(">ping|<pong!", Text(StreamReassembler.Follow(packets, ConversationKey.From(packets[0]))));
    }

    [Theory]
    [InlineData(CopyFormat.Hex, "48 69 00 ff")]
    [InlineData(CopyFormat.HexStream, "486900ff")]
    [InlineData(CopyFormat.Base64, "SGkA/w==")]
    [InlineData(CopyFormat.Text, "Hi..")]
    [InlineData(CopyFormat.CArray, "unsigned char data[4] = {\n    0x48, 0x69, 0x00, 0xff\n};")]
    public void Copies_bytes_in_each_format(CopyFormat format, string expected) =>
        Assert.Equal(expected, CopyFormats.Format(new byte[] { 0x48, 0x69, 0x00, 0xff }, format));

    [Fact]
    public void C_array_wraps_lines_and_handles_empty()
    {
        Assert.Equal("unsigned char data[0] = {};", CopyFormats.Format([], CopyFormat.CArray));
        Assert.Equal(3, CopyFormats.Format(new byte[13], CopyFormat.CArray).Split('\n').Length - 1);
    }
}
