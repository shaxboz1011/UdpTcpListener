using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.Core.Decoding;

/// <summary>Modbus/TCP (MBAP header + PDU) on port 502.</summary>
public sealed class ModbusDecoder : IPayloadDecoder
{
    public const ushort Port = 502;
    private const int MaxValuesShown = 32;

    public string Name => "Modbus/TCP";

    public DecodedLayer? TryDecode(PacketRecord packet)
    {
        if (packet.SourcePort != Port && packet.DestinationPort != Port)
            return null;

        var r = new PayloadReader(packet.Payload);
        if (r.Remaining < 8)
            return null;

        var transaction = r.U16();
        var protocol = r.U16();
        var length = r.U16();
        if (protocol != 0 || length < 2 || length != r.Remaining)
            return null;

        var unit = r.U8();
        var function = r.U8();
        var isRequest = packet.DestinationPort == Port;
        var isException = (function & 0x80) != 0;
        var code = function & 0x7F;
        var direction = isRequest ? "request" : "response";

        var fields = new List<DecodedField>
        {
            new("Transaction ID", transaction.ToString()),
            new("Unit ID", unit.ToString()),
            new("Function", $"{FunctionName(code)} ({code})"),
        };
        string detail;

        if (isException)
        {
            var exception = r.U8();
            fields.Add(new("Exception", $"{ExceptionName(exception)} ({exception})"));
            detail = $"exception: {ExceptionName(exception)}";
        }
        else
        {
            detail = (code, isRequest) switch
            {
                (1 or 2 or 3 or 4, true) => ReadRequest(ref r, fields),
                (3 or 4, false) => RegistersResponse(ref r, fields),
                (1 or 2, false) => BitsResponse(ref r, fields),
                (5 or 6, _) => WriteSingle(ref r, fields, code),
                (15 or 16, true) => WriteMultipleRequest(ref r, fields, code),
                (15 or 16, false) => ReadRequest(ref r, fields), // echo of address + quantity
                _ => RawData(ref r, fields),
            };
        }

        return new DecodedLayer("Modbus/TCP", $"{FunctionName(code)} {direction}" + (detail.Length > 0 ? $": {detail}" : ""), fields);
    }

    private static string ReadRequest(ref PayloadReader r, List<DecodedField> fields)
    {
        var address = r.U16();
        var quantity = r.U16();
        fields.Add(new("Start address", address.ToString()));
        fields.Add(new("Quantity", quantity.ToString()));
        return $"address {address}, quantity {quantity}";
    }

    private static string RegistersResponse(ref PayloadReader r, List<DecodedField> fields)
    {
        var count = r.U8();
        var registers = new List<ushort>();
        for (var i = 0; i < count / 2; i++)
            registers.Add(r.U16());
        fields.Add(new("Byte count", count.ToString()));
        fields.Add(new("Registers", Values(registers.Select(v => v.ToString())),
            registers.Take(MaxValuesShown).Select((v, i) => new DecodedField($"[{i}]", $"{v} (0x{v:x4})")).ToList()));
        return $"{registers.Count} register(s)";
    }

    private static string BitsResponse(ref PayloadReader r, List<DecodedField> fields)
    {
        var count = r.U8();
        var bytes = r.Bytes(count);
        fields.Add(new("Byte count", count.ToString()));
        fields.Add(new("Status", Convert.ToHexStringLower(bytes)));
        return $"{count} byte(s) of status";
    }

    private static string WriteSingle(ref PayloadReader r, List<DecodedField> fields, int code)
    {
        var address = r.U16();
        var value = r.U16();
        var shown = code == 5 ? (value == 0xFF00 ? "ON" : value == 0 ? "OFF" : $"0x{value:x4}") : value.ToString();
        fields.Add(new("Address", address.ToString()));
        fields.Add(new("Value", shown));
        return $"address {address} = {shown}";
    }

    private static string WriteMultipleRequest(ref PayloadReader r, List<DecodedField> fields, int code)
    {
        var summary = ReadRequest(ref r, fields);
        var count = r.U8();
        var data = r.Bytes(count);
        if (code == 16)
        {
            var values = new List<string>();
            for (var i = 0; i + 1 < data.Length; i += 2)
                values.Add(((data[i] << 8) | data[i + 1]).ToString());
            fields.Add(new("Values", Values(values)));
        }
        else
        {
            fields.Add(new("Values", Convert.ToHexStringLower(data)));
        }
        return summary;
    }

    private static string RawData(ref PayloadReader r, List<DecodedField> fields)
    {
        if (r.Remaining > 0)
            fields.Add(new("Data", Convert.ToHexStringLower(r.Rest())));
        return "";
    }

    private static string Values(IEnumerable<string> values)
    {
        var list = values.ToList();
        return string.Join(", ", list.Take(MaxValuesShown)) + (list.Count > MaxValuesShown ? $" … (+{list.Count - MaxValuesShown})" : "");
    }

    private static string FunctionName(int code) => code switch
    {
        1 => "Read Coils", 2 => "Read Discrete Inputs", 3 => "Read Holding Registers", 4 => "Read Input Registers",
        5 => "Write Single Coil", 6 => "Write Single Register", 7 => "Read Exception Status", 8 => "Diagnostics",
        15 => "Write Multiple Coils", 16 => "Write Multiple Registers", 17 => "Report Server ID",
        22 => "Mask Write Register", 23 => "Read/Write Multiple Registers", 43 => "Encapsulated Interface",
        _ => $"Function {code}",
    };

    private static string ExceptionName(byte code) => code switch
    {
        1 => "Illegal function", 2 => "Illegal data address", 3 => "Illegal data value", 4 => "Server device failure",
        5 => "Acknowledge", 6 => "Server device busy", 10 => "Gateway path unavailable", 11 => "Gateway target failed to respond",
        _ => $"Exception {code}",
    };
}
