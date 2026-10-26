using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PacketDotNet;
using UdpTcpListener.Core.Alerts;
using UdpTcpListener.Core.Geo;
using UdpTcpListener.Core.Packets;
using UdpTcpListener.Core.Processes;

namespace UdpTcpListener.Core.Tests;

public class AlertTests
{
    private static PacketRecord Packet(string src = "10.0.0.5", string dst = "8.8.8.8", ushort sport = 40000, ushort dport = 53,
                                       string payload = "", TransportProtocol protocol = TransportProtocol.Udp, double seconds = 0) => new()
    {
        Number = 1,
        Timestamp = TestPackets.Time.AddSeconds(seconds),
        Protocol = protocol,
        SourceAddress = IPAddress.Parse(src),
        DestinationAddress = IPAddress.Parse(dst),
        SourcePort = sport,
        DestinationPort = dport,
        Payload = Encoding.UTF8.GetBytes(payload),
        LinkType = LinkLayers.Ethernet,
        RawData = [],
        Length = 60 + payload.Length,
    };

    [Fact]
    public void Matches_every_condition()
    {
        var rule = new AlertRule
        {
            Name = "login", Protocol = "udp", Source = "10.0.0.0/8", Destination = "8.8.8.8", Port = 53,
            PayloadText = "PASSWORD", PayloadHex = "3d 31",
        }.Compile();

        Assert.True(rule.Matches(Packet(payload: "user password=1"), () => null));
        Assert.False(rule.Matches(Packet(payload: "user password=2"), () => null));                 // hex 3d31 missing
        Assert.False(rule.Matches(Packet(src: "192.168.1.1", payload: "password=1"), () => null)); // outside source net
        Assert.False(rule.Matches(Packet(dport: 54, payload: "password=1"), () => null));
        Assert.False(rule.Matches(Packet(protocol: TransportProtocol.Tcp, payload: "password=1"), () => null));
        Assert.True(rule.Matches(Packet(src: "10.1.1.1", sport: 53, dport: 1, payload: "password=1"), () => null));
    }

    [Fact]
    public void Empty_rule_matches_everything_and_decoded_text_is_lazy()
    {
        var calls = 0;
        Assert.True(new AlertRule { Name = "all" }.Compile().Matches(Packet(), () => { calls++; return null; }));
        Assert.Equal(0, calls);

        var dns = new AlertRule { Name = "dns", DecodedText = "query" }.Compile();
        Assert.True(dns.Matches(Packet(), () => "DNS Query A example.com"));
        Assert.False(dns.Matches(Packet(), () => null));
    }

    [Theory]
    [InlineData("{\"Name\":\"x\",\"Protocol\":\"icmp\"}")]
    [InlineData("{\"Name\":\"x\",\"Source\":\"not-an-ip\"}")]
    [InlineData("{\"Name\":\"x\",\"Port\":70000}")]
    [InlineData("{\"Name\":\"x\",\"PayloadHex\":\"zz\"}")]
    [InlineData("{\"Name\":\"x\",\"WebhookUrl\":\"ftp://x\"}")]
    [InlineData("{\"Name\":\"x\",\"TelegramBotToken\":\"123:abc\"}")]
    [InlineData("{\"Name\":\"x\",\"Threshold\":0}")]
    [InlineData("{\"Name\":\"\"}")]
    public void Rejects_invalid_rules(string json) =>
        Assert.Throws<FormatException>(() => AlertRuleStore.Parse($"[{json}]"));

    [Fact]
    public void Store_round_trips_rules()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "sub", "alerts.json");
            AlertRuleStore.Save(path, [new AlertRule { Name = "a", Port = 22, Sound = true, WebhookUrl = "https://example.com/h" }]);
            var rules = AlertRuleStore.Load(path);
            var rule = Assert.Single(rules);
            Assert.Equal(("a", 22, true, "https://example.com/h"), (rule.Name, rule.Port, rule.Sound, rule.WebhookUrl));
            Assert.Empty(AlertRuleStore.Load(Path.Combine(dir.FullName, "missing.json")));
            // Comments and trailing commas are fine in hand-written files.
            Assert.Single(AlertRuleStore.Parse("[ // ssh\n {\"name\":\"ssh\",\"port\":22,}, ]"));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Threshold_window_and_cooldown()
    {
        var engine = new AlertEngine([new AlertRule { Name = "flood", Port = 53, Threshold = 3, WindowSeconds = 1, CooldownSeconds = 5 }]);

        Assert.Empty(engine.Evaluate(Packet(seconds: 0)));
        Assert.Empty(engine.Evaluate(Packet(seconds: 0.5)));
        Assert.Empty(engine.Evaluate(Packet(seconds: 2)));      // the first two fell out of the window
        Assert.Empty(engine.Evaluate(Packet(seconds: 2.1)));
        var alert = Assert.Single(engine.Evaluate(Packet(seconds: 2.2)));
        Assert.Equal(3, alert.MatchCount);
        Assert.Contains("3 matches", alert.Message);

        for (var t = 2.3; t < 7; t += 0.1)                      // cooldown: quiet even though the threshold is met
            Assert.Empty(engine.Evaluate(Packet(seconds: t)));
        Assert.Single(engine.Evaluate(Packet(seconds: 7.3)));
        Assert.Empty(engine.Evaluate(Packet(dport: 80, seconds: 20)));
    }

    [Fact]
    public void Disabled_rules_are_skipped_and_each_rule_fires_separately()
    {
        var engine = new AlertEngine([
            new AlertRule { Name = "a", CooldownSeconds = 0 },
            new AlertRule { Name = "b", Port = 53, CooldownSeconds = 0 },
            new AlertRule { Name = "off", Enabled = false },
        ]);
        Assert.Equal(2, engine.RuleCount);
        Assert.Equal(["a", "b"], engine.Evaluate(Packet()).Select(a => a.Rule.Name));
        Assert.Equal(["a"], engine.Evaluate(Packet(dport: 80)).Select(a => a.Rule.Name));
    }

    [Fact]
    public async Task Webhook_and_telegram_receive_the_alert()
    {
        var handler = new RecordingHandler();
        var notifier = new AlertNotifier(new HttpClient(handler), "https://tg.test");
        var rule = new AlertRule { Name = "dns", WebhookUrl = "https://hooks.test/x", TelegramBotToken = "123:ABC", TelegramChatId = "-100" };
        await notifier.SendAsync(new AlertEvent(rule, Packet(payload: "hi"), TestPackets.Time, 1, "DNS Query"), TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);
        var hook = handler.Requests.Single(r => r.Url == "https://hooks.test/x");
        using var hookJson = JsonDocument.Parse(hook.Body);
        Assert.Equal("dns", hookJson.RootElement.GetProperty("rule").GetString());
        Assert.Equal("10.0.0.5:40000", hookJson.RootElement.GetProperty("source").GetString());
        Assert.Equal("DNS Query", hookJson.RootElement.GetProperty("decoded").GetString());
        Assert.Equal("6869", hookJson.RootElement.GetProperty("payloadHex").GetString());

        var tg = handler.Requests.Single(r => r.Url == "https://tg.test/bot123:ABC/sendMessage");
        using var tgJson = JsonDocument.Parse(tg.Body);
        Assert.Equal("-100", tgJson.RootElement.GetProperty("chat_id").GetString());
        Assert.Contains("dns", tgJson.RootElement.GetProperty("text").GetString());
        Assert.Contains("8.8.8.8:53", tgJson.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Delivery_failures_are_reported_not_thrown()
    {
        var handler = new RecordingHandler { Status = HttpStatusCode.Unauthorized, ResponseBody = "{\"description\":\"Unauthorized\"}" };
        var notifier = new AlertNotifier(new HttpClient(handler), "https://tg.test");
        var raised = 0;
        notifier.DeliveryFailed += (_, _, _) => raised++;
        var errors = await notifier.TestAsync(
            new AlertRule { Name = "t", WebhookUrl = "https://hooks.test/x", TelegramBotToken = "bad", TelegramChatId = "1" }, Packet(),
            TestContext.Current.CancellationToken);
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.StartsWith("Telegram:") && e.Contains("Unauthorized"));
        Assert.Contains(errors, e => e.StartsWith("webhook:"));
        Assert.Equal(0, raised); // a test reports to its caller only

        await notifier.SendAsync(new AlertEvent(new AlertRule { Name = "live", WebhookUrl = "https://hooks.test/x" }, Packet(), TestPackets.Time, 1, null),
            TestContext.Current.CancellationToken);
        Assert.Equal(1, raised);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Url, string Body)> Requests { get; } = [];
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public string ResponseBody { get; init; } = "{\"ok\":true}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Requests)
                Requests.Add((request.RequestUri!.ToString(), body));
            return new HttpResponseMessage(Status) { Content = new StringContent(ResponseBody) };
        }
    }
}

public class ProcessMappingTests
{
    private const string ProcNetTcp = """
          sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
           0: 0100007F:0035 00000000:0000 0A 00000000:00000000 00:00000000 00000000   101        0 23456 1 0000000000000000 100 0 0 10 0
           1: 0F02000A:C350 22D8B85D:01BB 01 00000000:00000000 02:00000218 00000000  1000        0 98765 2 0000000000000000 24 4 30 10 -1
        """;

    private const string ProcNetUdp6 = """
          sl  local_address                         remote_address                        st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode ref pointer drops
          10: 00000000000000000000000000000000:14E9 00000000000000000000000000000000:0000 07 00000000:00000000 00:00000000 00000000   107        0 5555 2 0000000000000000 0
          11: 0000000000000000FFFF00000100007F:1F90 00000000000000000000000000000000:0000 07 00000000:00000000 00:00000000 00000000   107        0 5556 2 0000000000000000 0
          12: B80D0120000000000000000001000000:0050 00000000000000000000000000000000:0000 07 00000000:00000000 00:00000000 00000000   107        0 5557 2 0000000000000000 0
        """;

    [Fact]
    public void Parses_proc_net_tables()
    {
        var tcp = LinuxSocketTable.ParseProcNet(ProcNetTcp, TransportProtocol.Tcp);
        Assert.Equal(2, tcp.Count);
        Assert.Equal((IPAddress.Loopback, (ushort)53, 23456L), (tcp[0].LocalAddress, tcp[0].LocalPort, tcp[0].Inode));
        Assert.Equal(IPAddress.Parse("10.0.2.15"), tcp[1].LocalAddress);
        Assert.Equal((IPAddress.Parse("93.184.216.34"), (ushort)443), (tcp[1].RemoteAddress, tcp[1].RemotePort));

        var udp6 = LinuxSocketTable.ParseProcNet(ProcNetUdp6, TransportProtocol.Udp);
        Assert.Equal(IPAddress.IPv6Any, udp6[0].LocalAddress);
        Assert.Equal(5353, udp6[0].LocalPort);
        Assert.Equal(IPAddress.Loopback, udp6[1].LocalAddress);          // ::ffff:127.0.0.1 is normalised
        Assert.Equal(IPAddress.Parse("2001:db8::1"), udp6[2].LocalAddress);
    }

    [Theory]
    [InlineData("socket:[12345]", true, 12345)]
    [InlineData("pipe:[12345]", false, 0)]
    [InlineData("/dev/null", false, 0)]
    public void Parses_socket_links(string target, bool ok, long inode)
    {
        Assert.Equal(ok, LinuxSocketTable.TryParseSocketLink(target, out var parsed));
        Assert.Equal(inode, parsed);
    }

    [Fact]
    public void Parses_windows_tables()
    {
        // One MIB_TCPROW_OWNER_PID: state 5, 192.168.1.2:50000 -> 1.1.1.1:443, pid 4242.
        var tcp = new byte[4 + 24];
        BitConverter.TryWriteBytes(tcp.AsSpan(0), 1u);
        BitConverter.TryWriteBytes(tcp.AsSpan(4), 5u);
        new byte[] { 192, 168, 1, 2 }.CopyTo(tcp, 8);
        tcp[12] = 0xC3; tcp[13] = 0x50;
        new byte[] { 1, 1, 1, 1 }.CopyTo(tcp, 16);
        tcp[20] = 0x01; tcp[21] = 0xBB;
        BitConverter.TryWriteBytes(tcp.AsSpan(24), 4242);
        var row = Assert.Single(WindowsSocketTable.ParseTcp4(tcp));
        Assert.Equal(new SocketEntry(TransportProtocol.Tcp, IPAddress.Parse("192.168.1.2"), 50000, IPAddress.Parse("1.1.1.1"), 443, 4242), row);

        // A listening socket: whatever is in the remote fields is ignored.
        BitConverter.TryWriteBytes(tcp.AsSpan(4), 2u);
        var listening = Assert.Single(WindowsSocketTable.ParseTcp4(tcp));
        Assert.Equal((IPAddress.Any, (ushort)0), (listening.RemoteAddress, listening.RemotePort));

        // One MIB_UDP6ROW_OWNER_PID on [::]:5353, pid 7; the claimed count of 2 is clamped to the data.
        var udp6 = new byte[4 + 28];
        BitConverter.TryWriteBytes(udp6.AsSpan(0), 2u);
        udp6[4 + 20] = 0x14; udp6[4 + 21] = 0xE9;
        BitConverter.TryWriteBytes(udp6.AsSpan(4 + 24), 7);
        var u = Assert.Single(WindowsSocketTable.ParseUdp6(udp6));
        Assert.Equal((IPAddress.IPv6Any, (ushort)5353, 7), (u.LocalAddress, u.LocalPort, u.ProcessId));
    }

    [Fact]
    public void Prefers_the_most_specific_socket()
    {
        var table = new FakeTable(
            new(TransportProtocol.Udp, IPAddress.Any, 53, IPAddress.Any, 0, 10),
            new(TransportProtocol.Udp, IPAddress.Parse("127.0.0.53"), 53, IPAddress.Any, 0, 11),
            new(TransportProtocol.Tcp, IPAddress.Any, 443, IPAddress.Any, 0, 20),
            new(TransportProtocol.Tcp, IPAddress.Parse("10.0.0.2"), 443, IPAddress.Parse("10.0.0.9"), 5000, 21),
            new(TransportProtocol.Tcp, IPAddress.IPv6Any, 8080, IPAddress.IPv6Any, 0, 30));
        var mapper = new ProcessMapper(table, pid => $"p{pid}");

        Assert.Equal(11, mapper.Lookup(TransportProtocol.Udp, IPAddress.Parse("10.0.0.9"), 999, IPAddress.Parse("127.0.0.53"), 53)?.Id);
        Assert.Equal(10, mapper.Lookup(TransportProtocol.Udp, IPAddress.Parse("10.0.0.2"), 53, IPAddress.Parse("10.0.0.9"), 999)?.Id);
        Assert.Equal(21, mapper.Lookup(TransportProtocol.Tcp, IPAddress.Parse("10.0.0.9"), 5000, IPAddress.Parse("10.0.0.2"), 443)?.Id);
        Assert.Equal(20, mapper.Lookup(TransportProtocol.Tcp, IPAddress.Parse("10.0.0.8"), 5000, IPAddress.Parse("10.0.0.2"), 443)?.Id);
        Assert.Equal("p30", mapper.Lookup(TransportProtocol.Tcp, IPAddress.Parse("10.0.0.8"), 1, IPAddress.Parse("10.0.0.2"), 8080)?.Name); // dual-stack [::]
        Assert.Null(mapper.Lookup(TransportProtocol.Udp, IPAddress.Parse("10.0.0.8"), 1, IPAddress.Parse("10.0.0.2"), 443));
    }

    [Fact]
    public void Misses_refresh_the_table_but_not_on_every_packet()
    {
        var table = new FakeTable();
        var mapper = new ProcessMapper(table, _ => "x") { RefreshInterval = TimeSpan.FromHours(1), MissRefreshInterval = TimeSpan.FromHours(1) };
        for (var i = 0; i < 100; i++)
            Assert.Null(mapper.Lookup(TransportProtocol.Udp, IPAddress.Loopback, 1, IPAddress.Loopback, 2));
        Assert.Equal(1, table.Reads);
    }

    [Fact]
    public void Finds_this_process_on_a_real_socket()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
            return;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var tcpPort = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        var udpPort = (ushort)((IPEndPoint)udp.Client.LocalEndPoint!).Port;

        var mapper = new ProcessMapper();
        Assert.True(mapper.IsSupported);
        var self = Environment.ProcessId;
        Assert.Equal(self, mapper.Lookup(TransportProtocol.Tcp, IPAddress.Loopback, 50000, IPAddress.Loopback, tcpPort)?.Id);
        Assert.Equal(self, mapper.Lookup(TransportProtocol.Udp, IPAddress.Loopback, udpPort, IPAddress.Loopback, 9)?.Id);
    }

    [Fact]
    public void Refreshes_in_the_background_without_blocking_lookups()
    {
        var table = new FakeTable(new SocketEntry(TransportProtocol.Udp, IPAddress.Any, 53, IPAddress.Any, 0, 10));
        var mapper = new ProcessMapper(table, _ => "dns") { RefreshInterval = TimeSpan.Zero, MissRefreshInterval = TimeSpan.Zero };
        Assert.Equal(10, mapper.Lookup(TransportProtocol.Udp, IPAddress.Loopback, 1, IPAddress.Loopback, 53)?.Id); // first read is synchronous

        table.Gate.Reset();      // the next read hangs until released
        table.Entries = [new SocketEntry(TransportProtocol.Udp, IPAddress.Any, 53, IPAddress.Any, 0, 11)];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 50; i++)   // answered from the old snapshot meanwhile
            Assert.Equal(10, mapper.Lookup(TransportProtocol.Udp, IPAddress.Loopback, 1, IPAddress.Loopback, 53)?.Id);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));

        table.Gate.Set();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (mapper.Lookup(TransportProtocol.Udp, IPAddress.Loopback, 1, IPAddress.Loopback, 53)?.Id != 11 && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
        Assert.Equal(11, mapper.Lookup(TransportProtocol.Udp, IPAddress.Loopback, 1, IPAddress.Loopback, 53)?.Id);
    }

    private sealed class FakeTable(params SocketEntry[] entries) : ISocketTable
    {
        public ManualResetEventSlim Gate { get; } = new(true);
        public SocketEntry[] Entries { get; set; } = entries;

        public int Reads { get; private set; }

        public IReadOnlyList<SocketEntry> Read()
        {
            Gate.Wait(TimeSpan.FromSeconds(10));
            Reads++;
            return Entries;
        }
    }
}

public class GeoTests
{
    private const string CountryCsv = """
        1.0.0.0,1.0.0.255,AU
        8.8.4.0,8.8.8.255,US
        93.184.216.0,93.184.216.255,US
        185.8.212.0,185.8.215.255,UZ
        2001:4860::,2001:4860:ffff:ffff:ffff:ffff:ffff:ffff,US
        """;

    private const string AsnCsv = """
        8.8.8.0,8.8.8.255,15169,Google LLC
        1.0.0.0,1.0.0.255,13335,"Cloudflare, Inc."
        185.8.212.0,185.8.215.255,8193,"Uzbektelekom ""UZ"" JSC"
        """;

    private static GeoDatabase Db() => GeoDatabase.Load(new StringReader(CountryCsv), new StringReader(AsnCsv));

    [Fact]
    public void Looks_up_country_and_asn()
    {
        var db = Db();
        Assert.Equal(new GeoInfo("US", 15169, "Google LLC"), db.Lookup(IPAddress.Parse("8.8.8.8")));
        Assert.Equal(new GeoInfo("US", null, null), db.Lookup(IPAddress.Parse("8.8.4.4")));
        Assert.Equal(new GeoInfo("AU", 13335, "Cloudflare, Inc."), db.Lookup(IPAddress.Parse("1.0.0.1")));
        Assert.Equal(new GeoInfo("UZ", 8193, "Uzbektelekom \"UZ\" JSC"), db.Lookup(IPAddress.Parse("185.8.215.255")));
        Assert.Equal("US", db.Lookup(IPAddress.Parse("2001:4860:4860::8888"))?.CountryCode);
        Assert.Equal("US", db.Lookup(IPAddress.Parse("::ffff:8.8.8.8"))?.CountryCode);
        Assert.Null(db.Lookup(IPAddress.Parse("1.0.1.0")));      // between ranges
        Assert.Null(db.Lookup(IPAddress.Parse("223.0.0.1")));    // after the last range
        Assert.Null(db.Lookup(IPAddress.Parse("192.168.1.1")));  // private
        Assert.Equal("AU · AS13335 Cloudflare, Inc.", db.Lookup(IPAddress.Parse("1.0.0.1"))!.ToString());
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("100.63.255.255", true)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("100.64.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("224.0.0.251", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("2a00:1450::1", true)]
    [InlineData("2001:db8::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("ff02::fb", false)]
    [InlineData("::1", false)]
    public void Knows_public_addresses(string address, bool expected) =>
        Assert.Equal(expected, GeoDatabase.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task Downloads_falls_back_to_last_month_and_loads_the_directory()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "dbip-asn-lite-2026-01.csv.gz"), "old");
            var handler = new GeoServer(new()
            {
                ["/free/dbip-country-lite-2026-10.csv.gz"] = Gzip(CountryCsv),
                // ASN for October is "not published yet", September is.
                ["/free/dbip-asn-lite-2026-09.csv.gz"] = Gzip(AsnCsv),
            });
            var installed = await GeoFiles.DownloadAsync(dir.FullName, new HttpClient(handler), baseUrl: "https://geo.test/free/",
                now: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), ct: TestContext.Current.CancellationToken);

            Assert.Equal(["dbip-country-lite-2026-10.csv.gz", "dbip-asn-lite-2026-09.csv.gz"], installed);
            Assert.Equal(installed.Order(), Directory.GetFiles(dir.FullName).Select(Path.GetFileName).Order()); // old file and temp files gone
            var db = GeoDatabase.LoadDirectory(dir.FullName);
            Assert.Equal(new GeoInfo("US", 15169, "Google LLC"), db.Lookup(IPAddress.Parse("8.8.8.8")));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public async Task Rejects_a_download_that_is_not_a_dbip_file()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var handler = new GeoServer(new() { ["/free/dbip-country-lite-2026-10.csv.gz"] = Gzip("<html>blocked</html>") });
            await Assert.ThrowsAsync<InvalidDataException>(() => GeoFiles.DownloadAsync(dir.FullName, new HttpClient(handler),
                baseUrl: "https://geo.test/free", now: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), ct: TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(dir.FullName));
            Assert.True(GeoDatabase.LoadDirectory(dir.FullName).IsEmpty);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Loads_plain_csv_files_dropped_in_by_hand()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "dbip-country-lite-2026-10.csv"), CountryCsv);
            var db = GeoDatabase.LoadDirectory(dir.FullName);
            Assert.True(db.HasCountries);
            Assert.False(db.HasAsns);
            Assert.Equal("UZ", db.Lookup(IPAddress.Parse("185.8.212.1"))?.CountryCode);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    private static byte[] Gzip(string text)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest))
            gz.Write(Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\n")));
        return ms.ToArray();
    }

    private sealed class GeoServer(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(files.TryGetValue(request.RequestUri!.AbsolutePath, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
