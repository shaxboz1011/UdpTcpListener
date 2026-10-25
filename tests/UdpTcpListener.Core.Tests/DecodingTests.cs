using System.Text;
using PacketDotNet;
using UdpTcpListener.Core.Decoding;
using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.Core.Tests;

public class DecodingTests
{
    private static PacketRecord Udp(ushort srcPort, ushort dstPort, byte[] payload) =>
        PacketDecoder.TryDecode(LinkLayers.Ethernet, TestPackets.EthernetUdp("10.0.0.1", "10.0.0.2", srcPort, dstPort, payload), TestPackets.Time, 1)!;

    private static PacketRecord Tcp(ushort srcPort, ushort dstPort, byte[] payload) =>
        PacketDecoder.TryDecode(LinkLayers.Ethernet, TestPackets.EthernetTcp("10.0.0.1", "10.0.0.2", srcPort, dstPort, payload, ack: true), TestPackets.Time, 1)!;

    private static DecodedLayer Single(PacketRecord packet) => Assert.Single(DecoderRegistry.Default.Decode(packet));

    private static string? Find(IEnumerable<DecodedField> fields, string name) =>
        fields.SelectMany(Flatten).FirstOrDefault(f => f.Name == name)?.Value;

    private static IEnumerable<DecodedField> Flatten(DecodedField f) => [f, .. (f.Children ?? []).SelectMany(Flatten)];

    // ---------------------------------------------------------------- DNS

    private static byte[] DnsName(string name) =>
        [.. name.Split('.').SelectMany(l => (byte[])[(byte)l.Length, .. Encoding.ASCII.GetBytes(l)]), 0];

    private static byte[] DnsQuery(ushort id, string name, ushort type) =>
        [(byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0, .. DnsName(name), 0, (byte)type, 0, 1];

    [Fact]
    public void Decodes_dns_query()
    {
        var layer = Single(Udp(40000, 53, DnsQuery(0x1a2b, "example.com", 1)));

        Assert.Equal("DNS", layer.Protocol);
        Assert.Equal("Query 0x1a2b A example.com", layer.Summary);
        Assert.Equal("A IN", Find(layer.Fields, "example.com"));
        Assert.Equal("Yes", Find(layer.Fields, "Recursion desired"));
    }

    [Fact]
    public void Decodes_dns_response_with_compression()
    {
        byte[] response =
        [
            0x1a, 0x2b, 0x81, 0x80, 0, 1, 0, 2, 0, 0, 0, 0,
            .. DnsName("www.example.com"), 0, 5, 0, 1,                                   // question: CNAME www.example.com
            0xc0, 0x0c, 0, 5, 0, 1, 0, 0, 0x0e, 0x10, 0, 2, 0xc0, 0x10,                  // www.example.com CNAME -> example.com (pointer)
            0xc0, 0x10, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 93, 184, 216, 34,                 // example.com A 93.184.216.34
        ];

        var layer = Single(Udp(53, 40000, response));

        Assert.Equal("Response 0x1a2b CNAME www.example.com  → example.com, 93.184.216.34", layer.Summary);
        var answers = layer.Fields.Single(f => f.Name == "Answers").Children!;
        Assert.Equal("CNAME 3600s example.com", Find(answers, "www.example.com"));
        Assert.Equal("A 60s 93.184.216.34", Find(answers, "example.com"));
    }

    [Fact]
    public void Dns_over_tcp_uses_length_prefix_and_reports_errors()
    {
        byte[] body = [0, 7, 0x81, 0x83, 0, 1, 0, 0, 0, 0, 0, 0, .. DnsName("nope.test"), 0, 28, 0, 1];
        var layer = Single(Tcp(53, 40000, [(byte)(body.Length >> 8), (byte)body.Length, .. body]));

        Assert.Equal("Response 0x0007 AAAA nope.test  [No such name]", layer.Summary);
    }

    [Fact]
    public void Dns_pointer_loop_does_not_hang()
    {
        byte[] loop = [0, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xc0, 0x0c, 0, 1, 0, 1];
        Assert.DoesNotContain(DecoderRegistry.Default.Decode(Udp(40000, 53, loop)), l => l.Protocol == "DNS");
    }

    // ---------------------------------------------------------------- MQTT

    private static byte[] MqttString(string s) => [(byte)(s.Length >> 8), (byte)s.Length, .. Encoding.UTF8.GetBytes(s)];

    [Fact]
    public void Decodes_mqtt_connect_and_publish_in_one_segment()
    {
        byte[] connectBody = [.. MqttString("MQTT"), 4, 0x02, 0, 60, .. MqttString("sensor-1")];
        byte[] publishBody = [.. MqttString("home/temp"), 0, 5, .. "21.5"u8.ToArray()];
        byte[] payload = [0x10, (byte)connectBody.Length, .. connectBody, 0x32, (byte)publishBody.Length, .. publishBody];

        var layer = Single(Tcp(50000, 1883, payload));

        Assert.Equal("MQTT", layer.Protocol);
        Assert.Equal("CONNECT client=sensor-1; PUBLISH home/temp (QoS 1)", layer.Summary);
        Assert.Equal("MQTT (level 4, 3.1.1)", Find(layer.Fields, "Protocol"));
        Assert.Equal("4 bytes: 21.5", Find(layer.Fields, "Payload"));
        Assert.Equal("5", Find(layer.Fields, "Packet ID"));
    }

    [Fact]
    public void Decodes_mqtt_subscribe_and_connack()
    {
        byte[] sub = [0, 10, .. MqttString("a/#"), 1, .. MqttString("b/+"), 0];
        Assert.Equal("SUBSCRIBE a/# (QoS 1), b/+ (QoS 0)", Single(Tcp(50000, 1883, [0x82, (byte)sub.Length, .. sub])).Summary);
        Assert.Equal("CONNACK Not authorized", Single(Tcp(1883, 50000, [0x20, 2, 0, 5])).Summary);
    }

    // ---------------------------------------------------------------- Modbus

    [Fact]
    public void Decodes_modbus_read_request_and_response()
    {
        var request = Single(Tcp(50000, 502, [0, 1, 0, 0, 0, 6, 17, 3, 0, 100, 0, 2]));
        Assert.Equal("Read Holding Registers request: address 100, quantity 2", request.Summary);
        Assert.Equal("17", Find(request.Fields, "Unit ID"));

        var response = Single(Tcp(502, 50000, [0, 1, 0, 0, 0, 7, 17, 3, 4, 0x01, 0x2c, 0xff, 0xff]));
        Assert.Equal("Read Holding Registers response: 2 register(s)", response.Summary);
        Assert.Equal("300, 65535", Find(response.Fields, "Registers"));
        Assert.Equal("300 (0x012c)", Find(response.Fields, "[0]"));
    }

    [Fact]
    public void Decodes_modbus_exception_and_write_coil()
    {
        Assert.Equal("Read Coils response: exception: Illegal data address", Single(Tcp(502, 50000, [0, 9, 0, 0, 0, 3, 1, 0x81, 2])).Summary);
        Assert.Equal("Write Single Coil request: address 7 = ON", Single(Tcp(50000, 502, [0, 2, 0, 0, 0, 6, 1, 5, 0, 7, 0xff, 0])).Summary);
    }

    [Fact]
    public void Modbus_length_mismatch_is_not_modbus() =>
        Assert.DoesNotContain(DecoderRegistry.Default.Decode(Tcp(50000, 502, [0, 1, 0, 0, 0, 9, 1, 3, 0, 0])), l => l.Protocol == "Modbus/TCP");

    // ---------------------------------------------------------------- JSON / text

    [Fact]
    public void Decodes_json_and_http_json_body()
    {
        var json = Single(Udp(5000, 6000, """{"id": 7, "tags": ["a", "b"], "nested": {"ok": true}}"""u8.ToArray()));
        Assert.Equal("JSON", json.Protocol);
        Assert.Equal("object, 3 key(s)", json.Summary);
        Assert.Equal("7", Find(json.Fields, "id"));
        Assert.Equal("\"b\"", Find(json.Fields, "[1]"));
        Assert.Equal("true", Find(json.Fields, "ok"));

        var http = Single(Tcp(8080, 50000, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n[1,2]"u8.ToArray()));
        Assert.Equal("array, 2 item(s)", http.Summary);
    }

    [Fact]
    public void Text_fallback_shows_lines_and_detects_http()
    {
        var http = Single(Tcp(50000, 80, "GET /index.html HTTP/1.1\r\nHost: x\r\n\r\n"u8.ToArray()));
        Assert.Equal("HTTP", http.Protocol);
        Assert.Equal("GET /index.html HTTP/1.1", http.Summary);
        Assert.Equal("Host: x", Find(http.Fields, "Line 2"));

        Assert.Equal("Text", Single(Udp(5000, 6000, "sensor=1 temp=21.5\n"u8.ToArray())).Protocol);
        Assert.Equal("HTTP", Single(Tcp(50000, 3128, "CONNECT example.com:443 HTTP/1.1\r\n\r\n"u8.ToArray())).Protocol);
    }

    [Fact]
    public void Binary_payload_has_no_layers() =>
        Assert.Empty(DecoderRegistry.Default.Decode(Udp(5000, 6000, [0, 1, 2, 3, 0xff, 0xfe, 0x80, 0x81])));

    [Fact]
    public void Garbage_on_known_ports_never_throws()
    {
        var random = new Random(42);
        foreach (var port in new ushort[] { 53, 1883, 502, 5353 })
        {
            for (var i = 0; i < 300; i++)
            {
                var data = new byte[random.Next(0, 64)];
                random.NextBytes(data);
                DecoderRegistry.Default.Decode(Udp(40000, port, data));
                DecoderRegistry.Default.Decode(Tcp(40000, port, data));
            }
        }
    }

    // ---------------------------------------------------------------- custom protocols

    private static byte[] ExamplePayload(ushort magic = 0xCAFE) =>
        [(byte)(magic >> 8), (byte)magic, 3, 0x00, 0xd7, 1, 0x81, 5, .. "probe"u8.ToArray(), 0xaa, 0xbb];

    [Fact]
    public void Example_definition_decodes_every_field_type()
    {
        var decoder = CustomProtocolLoader.Parse(CustomProtocolLoader.ExampleDefinition);
        var layer = Assert.Single(new DecoderRegistry([decoder]).Decode(Udp(40000, 9000, ExamplePayload())));

        Assert.Equal("Example sensor", layer.Protocol);
        Assert.Equal("device 3: 21.5 °C, 1 (Warning)", layer.Summary);
        Assert.Equal("0xcafe", Find(layer.Fields, "Magic"));
        Assert.Equal("129 (armed, alarm)", Find(layer.Fields, "Flags"));
        Assert.Equal("probe", Find(layer.Fields, "Name"));
        Assert.Equal("aabb", Find(layer.Fields, "Extra"));
    }

    [Fact]
    public void Custom_protocol_does_not_match_wrong_magic_or_port()
    {
        var decoder = CustomProtocolLoader.Parse(CustomProtocolLoader.ExampleDefinition);
        Assert.Null(decoder.TryDecode(Udp(40000, 9000, ExamplePayload(0xBEEF))));
        Assert.Null(decoder.TryDecode(Udp(40000, 9001, ExamplePayload())));
        Assert.Null(decoder.TryDecode(Tcp(40000, 9000, ExamplePayload())));
    }

    [Fact]
    public void Custom_protocol_little_endian_floats_and_cstring()
    {
        var decoder = CustomProtocolLoader.Parse("""
            { "name": "LE", "transport": "any", "ports": [7000], "endian": "little",
              "fields": [ { "name": "A", "type": "u16" }, { "name": "B", "type": "u16", "endian": "big" },
                          { "name": "F", "type": "f32" }, { "name": "S", "type": "cstring" }, { "name": "On", "type": "bool" } ] }
            """);
        byte[] payload = [0x01, 0x02, 0x01, 0x02, .. BitConverter.GetBytes(1.5f), .. "hi"u8.ToArray(), 0, 1];

        var layer = decoder.TryDecode(Udp(1, 7000, payload))!;

        Assert.Equal(["513", "258", "1.5", "hi", "true"], layer.Fields.Select(f => f.Value));
        Assert.Equal("A=513, B=258, F=1.5", layer.Summary);
    }

    [Fact]
    public void Short_payload_does_not_match() =>
        Assert.DoesNotContain(new DecoderRegistry([CustomProtocolLoader.Parse(CustomProtocolLoader.ExampleDefinition)])
            .Decode(Udp(40000, 9000, [0xCA, 0xFE, 3])), l => l.Protocol == "Example sensor");

    [Theory]
    [InlineData("""{ "name": "", "ports": [1], "fields": [ { "name": "a", "type": "u8" } ] }""", "\"name\" is required")]
    [InlineData("""{ "name": "x", "ports": [1], "fields": [ { "name": "a", "type": "u24" } ] }""", "unknown type \"u24\"")]
    [InlineData("""{ "name": "x", "fields": [ { "name": "a", "type": "u8" } ] }""", "needs \"expect\"")]
    [InlineData("""{ "name": "x", "ports": [1], "fields": [ { "name": "a", "type": "string" }, { "name": "b", "type": "u8" } ] }""", "only the last field")]
    [InlineData("""{ "name": "x", "ports": [1], "fields": [ { "name": "s", "type": "bytes", "lengthField": "n" }, { "name": "n", "type": "u8" } ] }""", "earlier integer field")]
    [InlineData("""{ "name": "x", "ports": [1], "fields": [ { "name": "a", "type": "u8", "lenght": 2 } ] }""", "Invalid JSON")]
    [InlineData("""{ "name": "x", "ports": [1], "fields": [ { "name": "a", "type": "u8" }, { "name": "A", "type": "u8" } ] }""", "duplicate")]
    [InlineData("""{ "name": "x", "transport": "sctp", "ports": [1], "fields": [ { "name": "a", "type": "u8" } ] }""", "transport")]
    public void Invalid_definitions_explain_the_problem(string json, string expected)
    {
        var ex = Assert.Throws<FormatException>(() => CustomProtocolLoader.Parse(json));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Loads_a_folder_and_reports_bad_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "utl-protocols-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "good.json"), CustomProtocolLoader.ExampleDefinition);
            File.WriteAllText(Path.Combine(dir, "bad.json"), "{ not json");
            File.WriteAllText(Path.Combine(dir, "ignored.txt"), "x");

            var result = CustomProtocolLoader.LoadDirectory(dir);

            Assert.Single(result.Decoders);
            Assert.StartsWith("bad.json:", Assert.Single(result.Errors));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
