using System.Net;
using System.Text;
using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.Core.Decoding;

/// <summary>DNS over UDP/TCP port 53, mDNS (5353) and LLMNR (5355), including name compression.</summary>
public sealed class DnsDecoder : IPayloadDecoder
{
    private static readonly HashSet<ushort> Ports = [53, 5353, 5355];

    public string Name => "DNS";

    public DecodedLayer? TryDecode(PacketRecord packet)
    {
        if (!Ports.Contains(packet.SourcePort) && !Ports.Contains(packet.DestinationPort))
            return null;

        ReadOnlySpan<byte> data = packet.Payload;
        if (packet.Protocol == TransportProtocol.Tcp)
        {
            // DNS over TCP: 2-byte length prefix.
            if (data.Length < 2 || (data[0] << 8 | data[1]) != data.Length - 2)
                return null;
            data = data[2..];
        }
        if (data.Length < 12)
            return null;

        var r = new PayloadReader(data);
        var id = r.U16();
        var flags = r.U16();
        int qd = r.U16(), an = r.U16(), ns = r.U16(), ar = r.U16();
        if (qd > 64 || an > 256 || ns > 256 || ar > 256)
            return null; // not DNS, or not worth showing

        var isResponse = (flags & 0x8000) != 0;
        var opcode = (flags >> 11) & 0xF;
        var rcode = flags & 0xF;

        var fields = new List<DecodedField>
        {
            new("Transaction ID", $"0x{id:x4}"),
            new("Flags", $"0x{flags:x4}", [
                new("Type", isResponse ? "Response" : "Query"),
                new("Opcode", opcode switch { 0 => "Standard query (0)", 1 => "Inverse query (1)", 2 => "Status (2)", 4 => "Notify (4)", 5 => "Update (5)", _ => opcode.ToString() }),
                new("Authoritative", Bool(flags & 0x0400)),
                new("Truncated", Bool(flags & 0x0200)),
                new("Recursion desired", Bool(flags & 0x0100)),
                new("Recursion available", Bool(flags & 0x0080)),
                new("Reply code", RcodeName(rcode)),
            ]),
            new("Questions", qd.ToString()),
            new("Answer RRs", an.ToString()),
            new("Authority RRs", ns.ToString()),
            new("Additional RRs", ar.ToString()),
        };

        var questions = new List<DecodedField>();
        string? firstName = null, firstType = null;
        for (var i = 0; i < qd; i++)
        {
            var name = ReadName(data, ref r);
            var type = r.U16();
            var cls = r.U16();
            firstName ??= name;
            firstType ??= TypeName(type);
            questions.Add(new(name, $"{TypeName(type)} {ClassName(cls)}"));
        }
        if (questions.Count > 0)
            fields.Add(new("Queries", questions.Count.ToString(), questions));

        var answers = ReadRecords(data, ref r, an);
        AddSection(fields, "Answers", answers);
        AddSection(fields, "Authority", ReadRecords(data, ref r, ns));
        AddSection(fields, "Additional", ReadRecords(data, ref r, ar));

        var summary = new StringBuilder(isResponse ? "Response" : "Query");
        summary.Append($" 0x{id:x4}");
        if (firstType is not null)
            summary.Append(' ').Append(firstType).Append(' ').Append(firstName);
        if (isResponse && rcode != 0)
            summary.Append("  [").Append(RcodeName(rcode)).Append(']');
        else if (answers.Count > 0)
            summary.Append("  → ").Append(string.Join(", ", answers.Take(3).Select(a => a.Value.Split(' ', 3).Last())));

        return new DecodedLayer(packet.SourcePort == 5353 || packet.DestinationPort == 5353 ? "mDNS" : "DNS", summary.ToString(), fields);
    }

    private static void AddSection(List<DecodedField> fields, string title, List<DecodedField> records)
    {
        if (records.Count > 0)
            fields.Add(new(title, records.Count.ToString(), records));
    }

    private static List<DecodedField> ReadRecords(ReadOnlySpan<byte> message, ref PayloadReader r, int count)
    {
        var records = new List<DecodedField>(count);
        for (var i = 0; i < count; i++)
        {
            var name = ReadName(message, ref r);
            var type = r.U16();
            var cls = r.U16();
            var ttl = r.U32();
            var length = r.U16();
            var start = r.Position;
            var rdata = r.Bytes(length);

            string value;
            if (type == 41) // OPT pseudo-record: class = UDP payload size
            {
                value = $"OPT  EDNS0, UDP payload size {cls}";
            }
            else
            {
                var data = FormatRData(message, type, start, rdata);
                value = $"{TypeName(type)} {ttl}s {data}";
            }
            records.Add(new(name, value));
        }
        return records;
    }

    private static string FormatRData(ReadOnlySpan<byte> message, ushort type, int start, ReadOnlySpan<byte> rdata)
    {
        var r = new PayloadReader(message) { Position = start };
        switch (type)
        {
            case 1 when rdata.Length == 4:
            case 28 when rdata.Length == 16:
                return new IPAddress(rdata).ToString();
            case 2 or 5 or 12: // NS, CNAME, PTR
                return ReadName(message, ref r);
            case 15: // MX
                var preference = r.U16();
                return $"{preference} {ReadName(message, ref r)}";
            case 16: // TXT: length-prefixed strings
                var parts = new List<string>();
                var t = new PayloadReader(rdata);
                while (t.Remaining > 0)
                    parts.Add("\"" + Encoding.UTF8.GetString(t.Bytes(t.U8())) + "\"");
                return string.Join(" ", parts);
            case 33: // SRV
                int priority = r.U16(), weight = r.U16(), port = r.U16();
                return $"{priority} {weight} {port} {ReadName(message, ref r)}";
            case 6: // SOA
                var mname = ReadName(message, ref r);
                var rname = ReadName(message, ref r);
                return $"{mname} {rname} serial {r.U32()}";
            default:
                return Convert.ToHexStringLower(rdata);
        }
    }

    /// <summary>Reads a possibly compressed domain name, guarding against pointer loops.</summary>
    internal static string ReadName(ReadOnlySpan<byte> message, ref PayloadReader r)
    {
        var labels = new List<string>();
        var position = r.Position;
        var jumped = false;
        var jumps = 0;

        while (true)
        {
            if (position >= message.Length)
                throw new DecodeException("DNS name runs past the end of the message.");

            var length = message[position];
            if (length == 0)
            {
                position++;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= message.Length || ++jumps > 32)
                    throw new DecodeException("Invalid DNS name compression pointer.");
                var target = ((length & 0x3F) << 8) | message[position + 1];
                if (!jumped)
                    r.Position = position + 2;
                jumped = true;
                position = target;
                continue;
            }

            if ((length & 0xC0) != 0 || position + 1 + length > message.Length)
                throw new DecodeException("Invalid DNS label.");

            labels.Add(Encoding.ASCII.GetString(message.Slice(position + 1, length)));
            position += 1 + length;
            if (labels.Sum(l => l.Length + 1) > 255)
                throw new DecodeException("DNS name is too long.");
        }

        if (!jumped)
            r.Position = position;
        return labels.Count == 0 ? "<Root>" : string.Join('.', labels);
    }

    private static string Bool(int bit) => bit != 0 ? "Yes" : "No";

    internal static string TypeName(ushort type) => type switch
    {
        1 => "A", 2 => "NS", 5 => "CNAME", 6 => "SOA", 12 => "PTR", 15 => "MX", 16 => "TXT",
        28 => "AAAA", 33 => "SRV", 41 => "OPT", 43 => "DS", 46 => "RRSIG", 47 => "NSEC", 48 => "DNSKEY",
        64 => "SVCB", 65 => "HTTPS", 255 => "ANY",
        _ => $"TYPE{type}",
    };

    private static string ClassName(ushort cls) => (cls & 0x7FFF) switch
    {
        1 => "IN", 3 => "CH", 4 => "HS", 255 => "ANY", var c => $"CLASS{c}",
    };

    private static string RcodeName(int rcode) => rcode switch
    {
        0 => "No error", 1 => "Format error", 2 => "Server failure", 3 => "No such name",
        4 => "Not implemented", 5 => "Refused", _ => $"RCODE {rcode}",
    };
}
