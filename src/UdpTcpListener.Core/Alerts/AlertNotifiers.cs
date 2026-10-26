using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UdpTcpListener.Core.Alerts;

/// <summary>
/// Sends alerts to webhooks and Telegram. Calls run in the background and never throw;
/// failures are reported through <see cref="DeliveryFailed"/>.
/// </summary>
public sealed class AlertNotifier(HttpClient? http = null, string telegramApiBase = "https://api.telegram.org")
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    private readonly string _telegramApiBase = telegramApiBase.TrimEnd('/');

    /// <summary>(rule name, channel, error message).</summary>
    public event Action<string, string, string>? DeliveryFailed;

    /// <summary>Sends to every remote channel the rule configures. Completes when all deliveries finish.</summary>
    public Task SendAsync(AlertEvent alert, CancellationToken ct = default) =>
        SendCoreAsync(alert, (channel, error) => DeliveryFailed?.Invoke(alert.Rule.Name, channel, error), ct);

    /// <summary>
    /// Sends a test message through the rule's channels and returns the errors (empty = all delivered).
    /// Failures are only returned, not raised through <see cref="DeliveryFailed"/>.
    /// </summary>
    public async Task<IReadOnlyList<string>> TestAsync(AlertRule rule, Packets.PacketRecord sample, CancellationToken ct = default)
    {
        var errors = new List<string>();
        await SendCoreAsync(new AlertEvent(rule, sample, DateTimeOffset.Now, 1, "Test alert from UdpTcpListener"),
            (channel, error) => { lock (errors) errors.Add($"{channel}: {error}"); }, ct);
        return errors;
    }

    private Task SendCoreAsync(AlertEvent alert, Action<string, string> failed, CancellationToken ct)
    {
        var tasks = new List<Task>(2);
        if (!string.IsNullOrWhiteSpace(alert.Rule.WebhookUrl))
            tasks.Add(Deliver("webhook", () => PostWebhookAsync(alert, ct), failed));
        if (alert.Rule.HasTelegram)
            tasks.Add(Deliver("Telegram", () => PostTelegramAsync(alert, ct), failed));
        return Task.WhenAll(tasks);
    }

    private static async Task Deliver(string channel, Func<Task> send, Action<string, string> failed)
    {
        try
        {
            await send();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            failed(channel, ex.Message);
        }
    }

    private async Task PostWebhookAsync(AlertEvent alert, CancellationToken ct)
    {
        var p = alert.Packet;
        var body = new WebhookPayload(
            alert.Rule.Name, alert.Time, p.ProtocolName, p.Source, p.Destination, p.Length, alert.MatchCount,
            alert.DecodedSummary, Convert.ToHexStringLower(p.Payload.AsSpan(0, Math.Min(p.Payload.Length, 1024))));
        using var response = await _http.PostAsync(alert.Rule.WebhookUrl!.Trim(), Json(body), ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task PostTelegramAsync(AlertEvent alert, CancellationToken ct)
    {
        var url = $"{_telegramApiBase}/bot{alert.Rule.TelegramBotToken!.Trim()}/sendMessage";
        var text = $"🔔 {alert.Title}\n{alert.Message}\n{alert.Time:yyyy-MM-dd HH:mm:ss}";
        using var response = await _http.PostAsync(url, Json(new TelegramMessage(alert.Rule.TelegramChatId!.Trim(), text)), ct);
        if (!response.IsSuccessStatusCode)
        {
            // Telegram explains problems (wrong token, bot not in chat) in the body.
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(detail, 200)}");
        }
    }

    // Buffered (with Content-Length) rather than streamed: simple webhook receivers do not accept chunked bodies.
    private static StringContent Json<T>(T body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    internal sealed record WebhookPayload(
        [property: JsonPropertyName("rule")] string Rule,
        [property: JsonPropertyName("time")] DateTimeOffset Time,
        [property: JsonPropertyName("protocol")] string Protocol,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("destination")] string Destination,
        [property: JsonPropertyName("length")] int Length,
        [property: JsonPropertyName("matches")] int Matches,
        [property: JsonPropertyName("decoded")] string? Decoded,
        [property: JsonPropertyName("payloadHex")] string PayloadHex);

    internal sealed record TelegramMessage(
        [property: JsonPropertyName("chat_id")] string ChatId,
        [property: JsonPropertyName("text")] string Text);
}
