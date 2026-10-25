using System.Text;
using System.Text.Json;
using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.Core.Decoding;

/// <summary>JSON payloads, including the body of an HTTP message.</summary>
public sealed class JsonDecoder : IPayloadDecoder
{
    private const int MaxDepth = 4;
    private const int MaxItems = 50;

    public string Name => "JSON";

    public DecodedLayer? TryDecode(PacketRecord packet)
    {
        ReadOnlySpan<byte> data = packet.Payload;
        var bodyStart = data.IndexOf("\r\n\r\n"u8);
        if (bodyStart >= 0 && LooksLikeHttp(data))
            data = data[(bodyStart + 4)..];

        var trimmed = data.Trim(" \t\r\n"u8);
        if (trimmed.IsEmpty || (trimmed[0] != (byte)'{' && trimmed[0] != (byte)'['))
            return null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(trimmed.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            var summary = root.ValueKind == JsonValueKind.Object
                ? $"object, {root.EnumerateObject().Count()} key(s)"
                : $"array, {root.GetArrayLength()} item(s)";
            return new DecodedLayer("JSON", summary, Children(root, 0));
        }
    }

    private static List<DecodedField> Children(JsonElement element, int depth)
    {
        var items = element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().Select(p => (Name: p.Name, Value: p.Value))
            : element.EnumerateArray().Select((v, i) => (Name: $"[{i}]", Value: v));

        var fields = new List<DecodedField>();
        foreach (var (name, value) in items.Take(MaxItems))
        {
            var nested = value.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
            fields.Add(new DecodedField(name,
                nested ? (value.ValueKind == JsonValueKind.Object ? "{…}" : $"[{value.GetArrayLength()}]") : Scalar(value),
                nested && depth + 1 < MaxDepth ? Children(value, depth + 1) : null));
        }
        return fields;
    }

    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => "\"" + DecodeText.Truncate(value.GetString() ?? "", 200) + "\"",
        _ => value.GetRawText(),
    };

    internal static bool LooksLikeHttp(ReadOnlySpan<byte> data) =>
        data.StartsWith("HTTP/"u8) || data.StartsWith("GET "u8) || data.StartsWith("POST "u8) || data.StartsWith("PUT "u8)
        || data.StartsWith("PATCH "u8) || data.StartsWith("DELETE "u8) || data.StartsWith("HEAD "u8) || data.StartsWith("OPTIONS "u8);
}

/// <summary>Fallback for printable payloads (HTTP, line protocols, logs): shows the first lines.</summary>
public sealed class TextDecoder : IPayloadDecoder
{
    private const int MaxLines = 20;

    public string Name => "Text";

    public DecodedLayer? TryDecode(PacketRecord packet)
    {
        if (DecodeText.AsText(packet.Payload) is not { } text || string.IsNullOrWhiteSpace(text))
            return null;

        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var fields = lines.Take(MaxLines)
            .Select((line, i) => new DecodedField($"Line {i + 1}", DecodeText.Truncate(line, 300)))
            .ToList();
        if (lines.Length > MaxLines)
            fields.Add(new DecodedField("…", $"{lines.Length - MaxLines} more line(s)"));

        var protocol = JsonDecoder.LooksLikeHttp(packet.Payload) ? "HTTP" : "Text";
        return new DecodedLayer(protocol, DecodeText.Truncate(lines[0].Trim(), 100), fields);
    }
}

/// <summary>
/// Runs the decoders on a packet: protocol-specific ones (built-in and custom) first; JSON only if
/// none matched; plain text only if nothing else did. Decoder errors never escape.
/// </summary>
public sealed class DecoderRegistry
{
    private readonly IReadOnlyList<IPayloadDecoder> _specific;
    private readonly JsonDecoder _json = new();
    private readonly TextDecoder _text = new();

    public DecoderRegistry(IEnumerable<IPayloadDecoder>? custom = null)
    {
        _specific = [.. custom ?? [], new DnsDecoder(), new MqttDecoder(), new ModbusDecoder()];
    }

    public static DecoderRegistry Default { get; } = new();

    public IReadOnlyList<IPayloadDecoder> Decoders => [.. _specific, _json, _text];

    public IReadOnlyList<DecodedLayer> Decode(PacketRecord packet)
    {
        if (packet.Payload.Length == 0)
            return [];

        var layers = new List<DecodedLayer>();
        foreach (var decoder in _specific)
        {
            if (Try(decoder, packet) is { } layer)
                layers.Add(layer);
        }

        if (layers.Count == 0 && Try(_json, packet) is { } json)
            layers.Add(json);
        if (layers.Count == 0 && Try(_text, packet) is { } text)
            layers.Add(text);
        return layers;
    }

    /// <summary>One-line summary of the first decoded layer, e.g. "DNS: Query 0x1a2b A example.com".</summary>
    public string? Summarize(PacketRecord packet) =>
        Decode(packet) is [var first, ..] ? $"{first.Protocol}: {first.Summary}" : null;

    private static DecodedLayer? Try(IPayloadDecoder decoder, PacketRecord packet)
    {
        try
        {
            return decoder.TryDecode(packet);
        }
        catch (Exception ex) when (ex is DecodeException or ArgumentException or IndexOutOfRangeException or DecoderFallbackException or FormatException)
        {
            return null; // malformed or not this protocol
        }
    }
}
