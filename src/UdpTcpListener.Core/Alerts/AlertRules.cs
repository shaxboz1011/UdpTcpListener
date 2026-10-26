using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.Core.Alerts;

/// <summary>
/// "When a packet matches these conditions (N times within T seconds), do these actions."
/// Every condition that is set must match; unset conditions are ignored.
/// </summary>
public sealed class AlertRule
{
    public string Name { get; set; } = "New rule";
    public bool Enabled { get; set; } = true;

    // ---- conditions -------------------------------------------------------

    /// <summary>"udp", "tcp" or null/"any".</summary>
    public string? Protocol { get; set; }

    /// <summary>IP address or CIDR network, e.g. "10.0.0.5" or "192.168.0.0/16".</summary>
    public string? Source { get; set; }

    public string? Destination { get; set; }

    /// <summary>Either side uses this port.</summary>
    public int? Port { get; set; }

    /// <summary>Payload contains this text (case-insensitive, UTF-8/ASCII).</summary>
    public string? PayloadText { get; set; }

    /// <summary>Payload contains these bytes, written as hex ("de ad be ef").</summary>
    public string? PayloadHex { get; set; }

    /// <summary>The decoded summary contains this text, e.g. "DNS" or "Query 0x" or "Error" (case-insensitive).</summary>
    public string? DecodedText { get; set; }

    /// <summary>Fire only when this many matching packets arrive within <see cref="WindowSeconds"/>. Default 1 (every match).</summary>
    public int Threshold { get; set; } = 1;

    public double WindowSeconds { get; set; } = 10;

    /// <summary>After firing, stay quiet for this long, so a flood does not become a flood of alerts.</summary>
    public double CooldownSeconds { get; set; } = 30;

    // ---- actions ----------------------------------------------------------

    /// <summary>Show a notification in the app.</summary>
    public bool Notify { get; set; } = true;

    public bool Sound { get; set; }

    /// <summary>POST a JSON description of the alert to this URL.</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>Telegram bot token from @BotFather.</summary>
    public string? TelegramBotToken { get; set; }

    /// <summary>Telegram chat/user/channel id the bot sends to.</summary>
    public string? TelegramChatId { get; set; }

    [JsonIgnore]
    public bool HasTelegram => !string.IsNullOrWhiteSpace(TelegramBotToken) && !string.IsNullOrWhiteSpace(TelegramChatId);

    /// <summary>Human-readable list of the conditions, for the rule list.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Protocol) && !Protocol.Equals("any", StringComparison.OrdinalIgnoreCase)) parts.Add(Protocol.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(Source)) parts.Add($"src {Source}");
        if (!string.IsNullOrWhiteSpace(Destination)) parts.Add($"dst {Destination}");
        if (Port is { } port) parts.Add($"port {port}");
        if (!string.IsNullOrWhiteSpace(PayloadText)) parts.Add($"\"{PayloadText}\"");
        if (!string.IsNullOrWhiteSpace(PayloadHex)) parts.Add($"hex {PayloadHex}");
        if (!string.IsNullOrWhiteSpace(DecodedText)) parts.Add($"decoded \"{DecodedText}\"");
        if (Threshold > 1) parts.Add($"≥{Threshold} in {WindowSeconds:0.#}s");
        return parts.Count == 0 ? "every packet" : string.Join(", ", parts);
    }

    /// <exception cref="FormatException">A condition is invalid; the message says which.</exception>
    public CompiledRule Compile()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new FormatException("The rule needs a name.");

        TransportProtocol? protocol = Protocol?.Trim().ToLowerInvariant() switch
        {
            null or "" or "any" or "both" => null,
            "udp" => TransportProtocol.Udp,
            "tcp" => TransportProtocol.Tcp,
            _ => throw new FormatException($"{Name}: protocol must be udp, tcp or any."),
        };
        if (Port is < 0 or > 65535)
            throw new FormatException($"{Name}: port must be between 0 and 65535.");
        if (Threshold < 1)
            throw new FormatException($"{Name}: threshold must be at least 1.");
        if (WindowSeconds <= 0 || CooldownSeconds < 0)
            throw new FormatException($"{Name}: window must be positive and cooldown cannot be negative.");
        if (!string.IsNullOrWhiteSpace(WebhookUrl)
            && !(Uri.TryCreate(WebhookUrl.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
            throw new FormatException($"{Name}: the webhook must be an http(s) URL.");
        if (string.IsNullOrWhiteSpace(TelegramBotToken) != string.IsNullOrWhiteSpace(TelegramChatId))
            throw new FormatException($"{Name}: Telegram needs both a bot token and a chat id.");

        byte[]? hex = null;
        if (!string.IsNullOrWhiteSpace(PayloadHex))
        {
            if (!Sending.PayloadParser.TryParse(PayloadHex, Sending.PayloadEncoding.Hex, out var bytes, out var error) || bytes.Length == 0)
                throw new FormatException($"{Name}: payload hex is invalid ({error ?? "empty"}).");
            hex = bytes;
        }

        return new CompiledRule(this, protocol, ParseNetwork(Source, "source"), ParseNetwork(Destination, "destination"),
            string.IsNullOrEmpty(PayloadText) ? null : Encoding.UTF8.GetBytes(PayloadText), hex,
            string.IsNullOrWhiteSpace(DecodedText) ? null : DecodedText.Trim());
    }

    private IPNetwork? ParseNetwork(string? text, string what)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim();
        if (IPNetwork.TryParse(text, out var network))
            return network;
        if (IPAddress.TryParse(text, out var address))
            return new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
        throw new FormatException($"{Name}: {what} must be an IP address or a network like 192.168.0.0/16.");
    }
}

/// <summary>A validated rule, ready for fast matching.</summary>
public sealed class CompiledRule
{
    private readonly TransportProtocol? _protocol;
    private readonly IPNetwork? _source;
    private readonly IPNetwork? _destination;
    private readonly byte[]? _text;
    private readonly byte[]? _hex;
    private readonly string? _decoded;

    internal CompiledRule(AlertRule rule, TransportProtocol? protocol, IPNetwork? source, IPNetwork? destination,
                          byte[]? text, byte[]? hex, string? decoded)
    {
        Rule = rule;
        _protocol = protocol;
        _source = source;
        _destination = destination;
        _text = text;
        _hex = hex;
        _decoded = decoded;
    }

    public AlertRule Rule { get; }

    public bool NeedsDecoding => _decoded is not null;

    /// <param name="decodedSummary">Lazily computed decoder summary; only called when the rule needs it.</param>
    public bool Matches(PacketRecord packet, Func<string?> decodedSummary)
    {
        if (_protocol is { } protocol && packet.Protocol != protocol)
            return false;
        if (_source is { } source && !source.Contains(packet.SourceAddress))
            return false;
        if (_destination is { } destination && !destination.Contains(packet.DestinationAddress))
            return false;
        if (Rule.Port is { } port && packet.SourcePort != port && packet.DestinationPort != port)
            return false;
        if (_hex is not null && packet.Payload.AsSpan().IndexOf(_hex) < 0)
            return false;
        if (_text is not null && !ContainsIgnoreCase(packet.Payload, _text))
            return false;
        if (_decoded is not null && decodedSummary()?.Contains(_decoded, StringComparison.OrdinalIgnoreCase) != true)
            return false;
        return true;
    }

    private static bool ContainsIgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length > haystack.Length)
            return false;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var j = 0;
            while (j < needle.Length && Lower(haystack[i + j]) == Lower(needle[j]))
                j++;
            if (j == needle.Length)
                return true;
        }
        return false;

        static byte Lower(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;
    }
}

/// <summary>One fired alert.</summary>
public sealed record AlertEvent(AlertRule Rule, PacketRecord Packet, DateTimeOffset Time, int MatchCount, string? DecodedSummary)
{
    public string Title => Rule.Name;

    public string Message =>
        $"{Packet.ProtocolName} {Packet.Source} → {Packet.Destination}, {Packet.Length} B" +
        (DecodedSummary is { Length: > 0 } s ? $"\n{s}" : "") +
        (MatchCount > 1 ? $"\n{MatchCount} matches in {Rule.WindowSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s" : "");
}

/// <summary>
/// Evaluates packets against the rules, applying thresholds and cooldowns.
/// Not thread-safe: call <see cref="Evaluate"/> from one thread (or under a lock).
/// </summary>
public sealed class AlertEngine
{
    private readonly List<(CompiledRule Rule, Queue<DateTimeOffset> Hits, DateTimeOffset? LastFired)> _rules = [];
    private readonly Func<PacketRecord, string?>? _summarize;

    /// <param name="summarize">Decoder summary for rules with a "decoded text" condition.</param>
    public AlertEngine(IEnumerable<AlertRule> rules, Func<PacketRecord, string?>? summarize = null)
    {
        _summarize = summarize;
        foreach (var rule in rules.Where(r => r.Enabled))
            _rules.Add((rule.Compile(), new Queue<DateTimeOffset>(), null));
    }

    public int RuleCount => _rules.Count;

    /// <summary>Alerts fired by this packet (usually none). Time comes from the packet, so file replays behave like live captures.</summary>
    public IReadOnlyList<AlertEvent> Evaluate(PacketRecord packet)
    {
        if (_rules.Count == 0)
            return [];

        List<AlertEvent>? fired = null;
        string? summary = null;
        var summarized = false;
        string? Summary()
        {
            if (!summarized)
            {
                summary = _summarize?.Invoke(packet);
                summarized = true;
            }
            return summary;
        }

        for (var i = 0; i < _rules.Count; i++)
        {
            var (compiled, hits, lastFired) = _rules[i];
            if (!compiled.Matches(packet, Summary))
                continue;

            var now = packet.Timestamp;
            var rule = compiled.Rule;
            hits.Enqueue(now);
            var windowStart = now - TimeSpan.FromSeconds(rule.WindowSeconds);
            while (hits.Count > 0 && hits.Peek() < windowStart)
                hits.Dequeue();

            if (hits.Count < rule.Threshold)
                continue;
            if (lastFired is { } last && now - last < TimeSpan.FromSeconds(rule.CooldownSeconds))
                continue;

            (fired ??= []).Add(new AlertEvent(rule, packet, now, hits.Count, compiled.NeedsDecoding || rule.WebhookUrl is not null || rule.HasTelegram ? Summary() : null));
            _rules[i] = (compiled, hits, now);
            hits.Clear();
        }

        return fired ?? (IReadOnlyList<AlertEvent>)[];
    }
}

/// <summary>Reads and writes the rule file (JSON array), shared by the app and the CLI's --alerts.</summary>
public static class AlertRuleStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is { Length: > 0 } appData ? appData : Path.GetTempPath(),
            "UdpTcpListener", "alerts.json");

    /// <exception cref="FormatException">The file is not valid JSON or a rule is invalid.</exception>
    public static List<AlertRule> Parse(string json)
    {
        List<AlertRule>? rules;
        try
        {
            rules = JsonSerializer.Deserialize<List<AlertRule>>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Invalid alert rules JSON: {ex.Message}", ex);
        }
        rules ??= [];
        foreach (var rule in rules)
            rule.Compile(); // validate
        return rules;
    }

    public static List<AlertRule> Load(string path) => File.Exists(path) ? Parse(File.ReadAllText(path)) : [];

    public static void Save(string path, IEnumerable<AlertRule> rules)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(rules.ToList(), Json));
        File.Move(temp, path, overwrite: true);
    }
}
