using System.Net;
using System.Net.Sockets;
using System.Text;
using PacketDotNet;
using UdpTcpListener.Core.Packets;
using UdpTcpListener.Core.Sending;

namespace UdpTcpListener.Core.Tests;

public class SendingTests
{
    [Theory]
    [InlineData("hi\\n", new byte[] { 0x68, 0x69, 0x0a })]
    [InlineData("a\\tb\\r\\n\\0", new byte[] { 0x61, 0x09, 0x62, 0x0d, 0x0a, 0x00 })]
    [InlineData("\\x41\\xff\\\\", new byte[] { 0x41, 0xff, 0x5c })]
    [InlineData("ü", new byte[] { 0xc3, 0xbc })]
    [InlineData("trailing\\", new byte[] { 0x74, 0x72, 0x61, 0x69, 0x6c, 0x69, 0x6e, 0x67, 0x5c })]
    public void Parses_text_with_escapes(string input, byte[] expected) =>
        Assert.Equal(expected, PayloadParser.Parse(input, PayloadEncoding.Text));

    [Theory]
    [InlineData("48 65 6c", new byte[] { 0x48, 0x65, 0x6c })]
    [InlineData("0x48,0x65 ; 0X6C", new byte[] { 0x48, 0x65, 0x6c })]
    [InlineData("de:ad-BE:ef", new byte[] { 0xde, 0xad, 0xbe, 0xef })]
    [InlineData("", new byte[0])]
    public void Parses_hex(string input, byte[] expected) =>
        Assert.Equal(expected, PayloadParser.Parse(input, PayloadEncoding.Hex));

    [Theory]
    [InlineData("abc", PayloadEncoding.Hex)]       // odd digits
    [InlineData("zz", PayloadEncoding.Hex)]        // not hex
    [InlineData("\\q", PayloadEncoding.Text)]      // unknown escape
    [InlineData("!!!", PayloadEncoding.Base64)]
    public void Rejects_bad_payloads(string input, PayloadEncoding encoding)
    {
        Assert.False(PayloadParser.TryParse(input, encoding, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public async Task Sends_udp_and_collects_replies()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var echo = Task.Run(async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                var r = await server.ReceiveAsync(TestContext.Current.CancellationToken);
                await server.SendAsync(Encoding.ASCII.GetBytes("ack:" + Encoding.ASCII.GetString(r.Buffer)), r.RemoteEndPoint, TestContext.Current.CancellationToken);
            }
        }, TestContext.Current.CancellationToken);

        var progress = new List<SendProgress>();
        var result = await PacketSender.SendAsync(new SendOptions
        {
            Protocol = TransportProtocol.Udp,
            Host = "127.0.0.1",
            Port = port,
            Payload = "ping"u8.ToArray(),
            Count = 3,
            Interval = TimeSpan.FromMilliseconds(10),
            ResponseTimeout = TimeSpan.FromSeconds(2),
        }, new SyncProgress<SendProgress>(progress.Add), TestContext.Current.CancellationToken);
        await echo;

        Assert.Equal(3, result.Sent);
        Assert.Equal(12, result.BytesSent);
        Assert.Equal(0, result.Errors);
        Assert.Equal(3, result.Responses.Count);
        Assert.Equal("ack:ping", Encoding.ASCII.GetString(result.Responses[0].Data));
        Assert.Equal(3, progress.Count);
    }

    [Fact]
    public async Task Sends_tcp_over_one_connection()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            var buffer = new MemoryStream();
            await client.GetStream().CopyToAsync(buffer, TestContext.Current.CancellationToken);
            return Encoding.ASCII.GetString(buffer.ToArray());
        }, TestContext.Current.CancellationToken);

        var result = await PacketSender.SendAsync(new SendOptions
        {
            Protocol = TransportProtocol.Tcp,
            Host = "localhost",
            Port = port,
            Payload = "abc"u8.ToArray(),
            Count = 4,
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(4, result.Sent);
        Assert.Equal("abcabcabcabc", await received);
        listener.Stop();
    }

    [Fact]
    public async Task Tcp_connection_refused_is_reported()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); // nothing listens on this port now

        await Assert.ThrowsAsync<SocketException>(() => PacketSender.SendAsync(new SendOptions
        {
            Protocol = TransportProtocol.Tcp, Host = "127.0.0.1", Port = port, Payload = [1],
        }, ct: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("", 80)]
    [InlineData("127.0.0.1", 0)]
    [InlineData("127.0.0.1", 70000)]
    public async Task Validates_options(string host, int port) =>
        await Assert.ThrowsAsync<ArgumentException>(() => PacketSender.SendAsync(new SendOptions
        {
            Protocol = TransportProtocol.Udp, Host = host, Port = port, Payload = [1],
        }, ct: TestContext.Current.CancellationToken));

    [Fact]
    public void Replay_delays_follow_timing_mode()
    {
        var t0 = TestPackets.Time;
        var times = new[] { t0, t0.AddMilliseconds(100), t0.AddMilliseconds(300), t0.AddMinutes(5) };

        Assert.Equal([TimeSpan.Zero, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(10)],
            Replay.Delays(times, new ReplayOptions()));
        Assert.Equal([TimeSpan.Zero, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(10)],
            Replay.Delays(times, new ReplayOptions { Speed = 2 }));
        Assert.All(Replay.Delays(times, new ReplayOptions { Timing = ReplayTiming.FixedInterval, Interval = TimeSpan.FromMilliseconds(5) }).Skip(1),
            d => Assert.Equal(TimeSpan.FromMilliseconds(5), d));
        Assert.All(Replay.Delays(times, new ReplayOptions { Timing = ReplayTiming.AsFastAsPossible }), d => Assert.Equal(TimeSpan.Zero, d));
        Assert.Throws<ArgumentOutOfRangeException>(() => Replay.Delays(times, new ReplayOptions { Speed = 0 }));
    }

    [Fact]
    public async Task Replays_udp_payloads_to_a_new_target()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var target = (IPEndPoint)server.Client.LocalEndPoint!;
        var packets = new[] { "one", "two", "", "three" }
            .Select((text, i) => PacketDecoder.TryDecode(LinkLayers.Ethernet,
                TestPackets.EthernetUdp("10.0.0.1", "10.0.0.2", 5000, 6000, Encoding.ASCII.GetBytes(text)),
                TestPackets.Time.AddMilliseconds(i), i + 1)!)
            .ToList();

        var sent = await Replay.ReplayPayloadsAsync(packets, target, new ReplayOptions { Timing = ReplayTiming.AsFastAsPossible },
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, sent); // the empty one is skipped
        var got = new List<string>();
        for (var i = 0; i < 3; i++)
            got.Add(Encoding.ASCII.GetString((await server.ReceiveAsync(TestContext.Current.CancellationToken)).Buffer));
        Assert.Equal(["one", "two", "three"], got);
    }

    [Fact]
    public async Task Replaying_tcp_requires_a_target()
    {
        var tcp = PacketDecoder.TryDecode(LinkLayers.Ethernet, TestPackets.EthernetTcp("10.0.0.1", "10.0.0.2", 1, 2, [1]), TestPackets.Time, 1)!;
        await Assert.ThrowsAsync<ArgumentException>(() => Replay.ReplayPayloadsAsync([tcp], null, new ReplayOptions(),
            ct: TestContext.Current.CancellationToken));
    }

    /// <summary>Progress&lt;T&gt; posts to the thread pool; this one reports synchronously so the test can count.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

public class HostPortTests
{
    [Theory]
    [InlineData("127.0.0.1:53", "127.0.0.1", 53)]
    [InlineData("example.com:8080", "example.com", 8080)]
    [InlineData(" [::1]:1883 ", "::1", 1883)]
    public void Parses(string text, string host, int port)
    {
        Assert.True(HostPort.TryParse(text, out var h, out var p));
        Assert.Equal((host, port), (h, p));
    }

    [Theory]
    [InlineData("")]
    [InlineData("host")]
    [InlineData(":80")]
    [InlineData("host:0")]
    [InlineData("host:99999")]
    [InlineData("::1:53")]
    [InlineData("[::1]")]
    public void Rejects(string text) => Assert.False(HostPort.TryParse(text, out _, out _));
}
