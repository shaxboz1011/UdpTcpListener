using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.Core.Decoding;

/// <summary>
/// A user-defined binary protocol, loaded from a JSON file. Fields are read in order from the start
/// of the payload. See docs/CUSTOM_PROTOCOLS.md for the format.
/// </summary>
public sealed class CustomProtocolDefinition
{
    public string Name { get; set; } = "";

    /// <summary>"udp", "tcp" or "any".</summary>
    public string Transport { get; set; } = "any";

    /// <summary>Matches when either side uses one of these ports. Empty = any port (then use "expect" on a field).</summary>
    public List<int> Ports { get; set; } = [];

    /// <summary>"big" (network order, default) or "little".</summary>
    public string Endian { get; set; } = "big";

    public List<CustomField> Fields { get; set; } = [];

    /// <summary>Optional summary template, e.g. "{Device}: {Temperature}".</summary>
    public string? Summary { get; set; }
}

public sealed class CustomField
{
    public string Name { get; set; } = "";

    /// <summary>u8 i8 u16 i16 u32 i32 u64 i64 f32 f64 bool string cstring bytes.</summary>
    public string Type { get; set; } = "";

    /// <summary>Overrides the protocol's byte order for this field.</summary>
    public string? Endian { get; set; }

    /// <summary>Fixed length in bytes for string/bytes.</summary>
    public int? Length { get; set; }

    /// <summary>Name of an earlier integer field holding the length of this string/bytes field.</summary>
    public string? LengthField { get; set; }

    /// <summary>Multiplier for numbers (e.g. 0.1 to turn 215 into 21.5).</summary>
    public double? Scale { get; set; }

    public string? Unit { get; set; }

    /// <summary>"dec" (default) or "hex" for integers.</summary>
    public string? Format { get; set; }

    /// <summary>Value names for integers: { "0": "OK", "1": "Error" }.</summary>
    public Dictionary<string, string>? Enum { get; set; }

    /// <summary>Bit names for flag fields: { "0": "armed", "7": "alarm" } (bit 0 = least significant).</summary>
    public Dictionary<string, string>? Bits { get; set; }

    /// <summary>Required value (e.g. a magic number "0xCAFE"); if it differs the protocol does not match.</summary>
    public string? Expect { get; set; }
}

public sealed class CustomProtocolDecoder : IPayloadDecoder
{
    private static readonly HashSet<string> IntegerTypes = ["u8", "i8", "u16", "i16", "u32", "i32", "u64", "i64"];
    private static readonly HashSet<string> AllTypes = [.. IntegerTypes, "f32", "f64", "bool", "string", "cstring", "bytes"];

    private readonly CustomProtocolDefinition _definition;

    private CustomProtocolDecoder(CustomProtocolDefinition definition) => _definition = definition;

    public string Name => _definition.Name;

    public CustomProtocolDefinition Definition => _definition;

    /// <exception cref="FormatException">The definition is invalid; the message says why.</exception>
    public static CustomProtocolDecoder Create(CustomProtocolDefinition definition)
    {
        Validate(definition);
        return new CustomProtocolDecoder(definition);
    }

    public DecodedLayer? TryDecode(PacketRecord packet)
    {
        var transport = _definition.Transport.ToLowerInvariant();
        if ((transport == "udp" && packet.Protocol != TransportProtocol.Udp)
            || (transport == "tcp" && packet.Protocol != TransportProtocol.Tcp))
            return null;
        if (_definition.Ports.Count > 0 && !_definition.Ports.Contains(packet.SourcePort) && !_definition.Ports.Contains(packet.DestinationPort))
            return null;

        var reader = new PayloadReader(packet.Payload);
        var defaultBigEndian = !_definition.Endian.Equals("little", StringComparison.OrdinalIgnoreCase);
        var numbers = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var displays = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fields = new List<DecodedField>();

        foreach (var field in _definition.Fields)
        {
            reader.BigEndian = field.Endian is null ? defaultBigEndian : !field.Endian.Equals("little", StringComparison.OrdinalIgnoreCase);
            var type = field.Type.ToLowerInvariant();
            string display;

            if (IntegerTypes.Contains(type))
            {
                var value = ReadInteger(ref reader, type);
                if (field.Expect is not null && value != ParseNumber(field.Expect))
                    return null; // signature mismatch: not this protocol
                numbers[field.Name] = value;
                display = FormatInteger(field, value);
            }
            else
            {
                display = type switch
                {
                    "f32" => FormatReal(field, reader.F32()),
                    "f64" => FormatReal(field, reader.F64()),
                    "bool" => reader.U8() != 0 ? "true" : "false",
                    "string" => Encoding.UTF8.GetString(reader.Bytes(LengthOf(field, numbers, reader.Remaining))).TrimEnd('\0'),
                    "cstring" => ReadCString(ref reader),
                    "bytes" => Convert.ToHexStringLower(reader.Bytes(LengthOf(field, numbers, reader.Remaining))),
                    _ => throw new DecodeException($"Unknown type {type}."),
                };
                if (field.Expect is not null && !string.Equals(display, field.Expect, StringComparison.Ordinal))
                    return null;
            }

            displays[field.Name] = display;
            fields.Add(new DecodedField(field.Name, display));
        }

        if (reader.Remaining > 0)
            fields.Add(new DecodedField("(trailing)", $"{reader.Remaining} byte(s): {Convert.ToHexStringLower(reader.Rest())}"));

        var summary = _definition.Summary is { Length: > 0 } template
            ? FillTemplate(template, displays)
            : string.Join(", ", fields.Take(3).Select(f => $"{f.Name}={f.Value}"));
        return new DecodedLayer(_definition.Name, summary, fields);
    }

    private static decimal ReadInteger(ref PayloadReader reader, string type) => type switch
    {
        "u8" => reader.U8(),
        "i8" => reader.I8(),
        "u16" => reader.U16(),
        "i16" => reader.I16(),
        "u32" => reader.U32(),
        "i32" => reader.I32(),
        "u64" => reader.U64(),
        _ => reader.I64(),
    };

    private static string FormatInteger(CustomField field, decimal value)
    {
        string text;
        if (field.Scale is { } scale)
            text = (value * (decimal)scale).ToString("0.######", CultureInfo.InvariantCulture);
        else if (string.Equals(field.Format, "hex", StringComparison.OrdinalIgnoreCase) && value >= 0)
            text = "0x" + ((ulong)value).ToString("x" + HexWidth(field.Type), CultureInfo.InvariantCulture);
        else
            text = value.ToString(CultureInfo.InvariantCulture);

        if (field.Unit is { Length: > 0 } unit)
            text += " " + unit;

        var key = value.ToString(CultureInfo.InvariantCulture);
        if (field.Enum is not null && field.Enum.TryGetValue(key, out var name))
            text += $" ({name})";

        if (field.Bits is not null && value >= 0)
        {
            var bits = (ulong)value;
            var set = field.Bits
                .Where(kv => int.TryParse(kv.Key, out var bit) && bit is >= 0 and < 64 && (bits & (1UL << bit)) != 0)
                .Select(kv => kv.Value)
                .ToList();
            text += set.Count > 0 ? $" ({string.Join(", ", set)})" : " (none)";
        }
        return text;
    }

    private static string FormatReal(CustomField field, double value)
    {
        if (field.Scale is { } scale)
            value *= scale;
        var text = value.ToString("0.######", CultureInfo.InvariantCulture);
        return field.Unit is { Length: > 0 } unit ? $"{text} {unit}" : text;
    }

    private static int HexWidth(string type) => type.ToLowerInvariant() switch
    {
        "u8" or "i8" => 2, "u16" or "i16" => 4, "u32" or "i32" => 8, _ => 16,
    };

    private static int LengthOf(CustomField field, Dictionary<string, decimal> numbers, int remaining)
    {
        if (field.Length is { } length)
            return length;
        if (field.LengthField is { } name)
        {
            var value = numbers[name];
            return value is >= 0 and <= int.MaxValue ? (int)value : throw new DecodeException($"Length field {name} is {value}.");
        }
        return remaining; // last field: the rest of the payload
    }

    private static string ReadCString(ref PayloadReader reader)
    {
        var rest = reader.Data[reader.Position..];
        var end = rest.IndexOf((byte)0);
        if (end < 0)
            throw new DecodeException("cstring has no terminating zero byte.");
        var text = Encoding.UTF8.GetString(rest[..end]);
        reader.Position += end + 1;
        return text;
    }

    private static string FillTemplate(string template, Dictionary<string, string> values)
    {
        var sb = new StringBuilder(template.Length);
        for (var i = 0; i < template.Length; i++)
        {
            var close = template[i] == '{' ? template.IndexOf('}', i) : -1;
            if (close > i && values.TryGetValue(template[(i + 1)..close], out var value))
            {
                sb.Append(value);
                i = close;
            }
            else
            {
                sb.Append(template[i]);
            }
        }
        return sb.ToString();
    }

    internal static decimal ParseNumber(string text)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            return hex;
        if (decimal.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return number;
        throw new FormatException($"'{text}' is not a number (use e.g. 42 or 0x2A).");
    }

    private static void Validate(CustomProtocolDefinition d)
    {
        if (string.IsNullOrWhiteSpace(d.Name))
            throw new FormatException("\"name\" is required.");
        if (d.Transport.ToLowerInvariant() is not ("udp" or "tcp" or "any"))
            throw new FormatException($"\"transport\" must be udp, tcp or any (got \"{d.Transport}\").");
        if (d.Endian.ToLowerInvariant() is not ("big" or "little"))
            throw new FormatException($"\"endian\" must be big or little (got \"{d.Endian}\").");
        if (d.Ports.Any(p => p is < 1 or > 65535))
            throw new FormatException("\"ports\" must be between 1 and 65535.");
        if (d.Fields.Count == 0)
            throw new FormatException("\"fields\" must list at least one field.");
        if (d.Ports.Count == 0 && !d.Fields.Any(f => f.Expect is not null))
            throw new FormatException("Without \"ports\", at least one field needs \"expect\" (a magic value), or every packet would match.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < d.Fields.Count; i++)
        {
            var f = d.Fields[i];
            var where = $"field #{i + 1} ({(string.IsNullOrWhiteSpace(f.Name) ? "unnamed" : f.Name)})";
            if (string.IsNullOrWhiteSpace(f.Name))
                throw new FormatException($"{where}: \"name\" is required.");
            if (!seen.Add(f.Name))
                throw new FormatException($"{where}: duplicate field name.");

            var type = f.Type.ToLowerInvariant();
            if (!AllTypes.Contains(type))
                throw new FormatException($"{where}: unknown type \"{f.Type}\". Use one of: {string.Join(", ", AllTypes)}.");
            if (f.Endian is not null && f.Endian.ToLowerInvariant() is not ("big" or "little"))
                throw new FormatException($"{where}: \"endian\" must be big or little.");
            if (f.Length is < 0)
                throw new FormatException($"{where}: \"length\" cannot be negative.");

            var sized = type is "string" or "bytes";
            if (!sized && (f.Length is not null || f.LengthField is not null))
                throw new FormatException($"{where}: \"length\"/\"lengthField\" only apply to string and bytes.");
            if (sized && f.Length is null && f.LengthField is null && i != d.Fields.Count - 1)
                throw new FormatException($"{where}: needs \"length\" or \"lengthField\" (only the last field may take the rest of the payload).");
            if (f.LengthField is { } lf)
            {
                var source = d.Fields.Take(i).FirstOrDefault(x => x.Name.Equals(lf, StringComparison.OrdinalIgnoreCase));
                if (source is null || !IntegerTypes.Contains(source.Type.ToLowerInvariant()))
                    throw new FormatException($"{where}: \"lengthField\" must name an earlier integer field.");
            }
            if (f.Expect is not null && IntegerTypes.Contains(type))
                ParseNumber(f.Expect);
        }
    }
}

/// <summary>Loads <c>*.json</c> protocol definitions from a folder.</summary>
public static class CustomProtocolLoader
{
    public sealed record LoadResult(IReadOnlyList<CustomProtocolDecoder> Decoders, IReadOnlyList<string> Errors);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, // catch typos like "lenght"
    };

    /// <summary>Per-user folder for protocol definitions, shared by the desktop app and the CLI.</summary>
    public static string DefaultDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is { Length: > 0 } appData
            ? appData
            : Path.GetTempPath(), "UdpTcpListener", "protocols");

    public static CustomProtocolDecoder Parse(string json)
    {
        CustomProtocolDefinition? definition;
        try
        {
            definition = JsonSerializer.Deserialize<CustomProtocolDefinition>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Invalid JSON: {ex.Message}", ex);
        }
        return CustomProtocolDecoder.Create(definition ?? throw new FormatException("The file is empty."));
    }

    public static LoadResult LoadDirectory(string directory)
    {
        var decoders = new List<CustomProtocolDecoder>();
        var errors = new List<string>();
        if (!Directory.Exists(directory))
            return new LoadResult(decoders, errors);

        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                decoders.Add(Parse(File.ReadAllText(file)));
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return new LoadResult(decoders, errors);
    }

    /// <summary>A commented example that shows every feature; written to the protocols folder on first run.</summary>
    public const string ExampleDefinition = """
        // Example custom protocol. Copy this file, change it, and press "Reload" in the app.
        // Full reference: https://github.com/shaxboz1011/UdpTcpListener/blob/main/docs/CUSTOM_PROTOCOLS.md
        {
          "name": "Example sensor",
          "transport": "udp",
          "ports": [9000],
          "endian": "big",
          "summary": "device {Device}: {Temperature}, {Status}",
          "fields": [
            { "name": "Magic",       "type": "u16", "format": "hex", "expect": "0xCAFE" },
            { "name": "Device",      "type": "u8" },
            { "name": "Temperature", "type": "i16", "scale": 0.1, "unit": "°C" },
            { "name": "Status",      "type": "u8",  "enum": { "0": "OK", "1": "Warning", "2": "Error" } },
            { "name": "Flags",       "type": "u8",  "bits": { "0": "armed", "7": "alarm" } },
            { "name": "NameLength",  "type": "u8" },
            { "name": "Name",        "type": "string", "lengthField": "NameLength" },
            { "name": "Extra",       "type": "bytes" }
          ]
        }
        """;
}
