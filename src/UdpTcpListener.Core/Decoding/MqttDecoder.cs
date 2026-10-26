using System.Text;
using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.Core.Decoding;

/// <summary>MQTT 3.1.1 / 5.0 control packets on TCP port 1883 (several per segment are supported).</summary>
public sealed class MqttDecoder : IPayloadDecoder
{
    public const ushort Port = 1883;

    private static readonly string[] TypeNames =
    [
        "Reserved", "CONNECT", "CONNACK", "PUBLISH", "PUBACK", "PUBREC", "PUBREL", "PUBCOMP",
        "SUBSCRIBE", "SUBACK", "UNSUBSCRIBE", "UNSUBACK", "PINGREQ", "PINGRESP", "DISCONNECT", "AUTH",
    ];

    public string Name => "MQTT";

    public DecodedLayer? TryDecode(PacketRecord packet)
    {
        if (packet.Protocol != TransportProtocol.Tcp
            || (packet.SourcePort != Port && packet.DestinationPort != Port)
            || packet.Payload.Length < 2)
            return null;

        var r = new PayloadReader(packet.Payload);
        var messages = new List<DecodedField>();
        var summaries = new List<string>();

        while (r.Remaining >= 2 && messages.Count < 50)
        {
            var header = r.U8();
            var type = header >> 4;
            var flags = header & 0x0F;
            var remaining = ReadVarInt(ref r);
            if (type == 0 || remaining > r.Remaining)
                return messages.Count == 0 ? null : Result(messages, summaries); // not MQTT, or a segment boundary

            var body = new PayloadReader(r.Bytes(remaining));
            var fields = new List<DecodedField>();
            var summary = TypeNames[type];

            switch (type)
            {
                case 1: // CONNECT
                {
                    var protocol = Utf8(ref body);
                    var level = body.U8();
                    var connectFlags = body.U8();
                    var keepAlive = body.U16();
                    if (level >= 5)
                        body.Bytes(ReadVarInt(ref body)); // properties
                    var clientId = Utf8(ref body);
                    fields.Add(new("Protocol", $"{protocol} (level {level}{(level == 4 ? ", 3.1.1" : level == 5 ? ", 5.0" : "")})"));
                    fields.Add(new("Client ID", clientId.Length == 0 ? "(empty)" : clientId));
                    fields.Add(new("Keep alive", $"{keepAlive} s"));
                    fields.Add(new("Clean session", (connectFlags & 0x02) != 0 ? "Yes" : "No"));
                    fields.Add(new("Username", (connectFlags & 0x80) != 0 ? "present" : "none"));
                    summary += $" client={clientId}";
                    break;
                }
                case 2: // CONNACK
                {
                    var ack = body.U8();
                    var code = body.U8();
                    fields.Add(new("Session present", (ack & 1) != 0 ? "Yes" : "No"));
                    fields.Add(new("Return code", ConnackName(code)));
                    summary += $" {ConnackName(code)}";
                    break;
                }
                case 3: // PUBLISH
                {
                    var qos = (flags >> 1) & 3;
                    var topic = Utf8(ref body);
                    fields.Add(new("Topic", topic));
                    fields.Add(new("QoS", qos.ToString()));
                    fields.Add(new("Retain", (flags & 1) != 0 ? "Yes" : "No"));
                    fields.Add(new("Duplicate", (flags & 8) != 0 ? "Yes" : "No"));
                    if (qos > 0)
                        fields.Add(new("Packet ID", body.U16().ToString()));
                    var payload = body.Rest();
                    fields.Add(new("Payload", $"{payload.Length} bytes: {DecodeText.Show(payload)}"));
                    summary += $" {topic}" + (qos > 0 ? $" (QoS {qos})" : "");
                    break;
                }
                case 8 or 10: // SUBSCRIBE, UNSUBSCRIBE
                {
                    fields.Add(new("Packet ID", body.U16().ToString()));
                    var topics = new List<string>();
                    while (body.Remaining >= 2)
                    {
                        var filter = Utf8(ref body);
                        if (type == 8 && body.Remaining > 0)
                            filter += $" (QoS {body.U8() & 3})";
                        topics.Add(filter);
                    }
                    fields.Add(new("Topics", string.Join(", ", topics)));
                    summary += " " + string.Join(", ", topics);
                    break;
                }
                case 4 or 5 or 6 or 7 or 9 or 11:
                    if (body.Remaining >= 2)
                        fields.Add(new("Packet ID", body.U16().ToString()));
                    break;
            }

            messages.Add(new DecodedField(TypeNames[type], $"{remaining} bytes", fields));
            summaries.Add(summary);
        }

        return messages.Count == 0 ? null : Result(messages, summaries);
    }

    private static DecodedLayer Result(List<DecodedField> messages, List<string> summaries) =>
        new("MQTT", string.Join("; ", summaries.Take(3)) + (summaries.Count > 3 ? $" (+{summaries.Count - 3})" : ""), messages);

    private static int ReadVarInt(ref PayloadReader r)
    {
        var value = 0;
        for (var shift = 0; shift < 28; shift += 7)
        {
            var b = r.U8();
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
        }
        throw new DecodeException("MQTT variable-length integer is too long.");
    }

    private static string Utf8(ref PayloadReader r) => Encoding.UTF8.GetString(r.Bytes(r.U16()));

    private static string ConnackName(byte code) => code switch
    {
        0 => "Accepted", 1 => "Unacceptable protocol version", 2 => "Identifier rejected",
        3 => "Server unavailable", 4 => "Bad user name or password", 5 => "Not authorized",
        0x80 => "Unspecified error", 0x86 => "Bad user name or password", 0x87 => "Not authorized",
        _ => $"0x{code:x2}",
    };
}
