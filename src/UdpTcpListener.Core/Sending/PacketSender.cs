using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.Core.Sending;

public sealed record SendOptions
{
    public required TransportProtocol Protocol { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required byte[] Payload { get; init; }

    /// <summary>How many times to send the payload (0 = until cancelled).</summary>
    public int Count { get; init; } = 1;

    /// <summary>Pause between sends.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.Zero;

    /// <summary>Wait this long for a reply after each send (UDP datagram or TCP data). Zero = don't wait.</summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.Zero;
}

public sealed record SendProgress(long Sent, long BytesSent, long Errors, string? LastError);

public sealed record SendResponse(DateTimeOffset Timestamp, string From, byte[] Data);

public sealed record SendResult(long Sent, long BytesSent, long Errors, IReadOnlyList<SendResponse> Responses, TimeSpan Elapsed);

/// <summary>
/// Sends a payload over a normal UDP or TCP socket (no raw packets, no admin rights needed).
/// TCP keeps one connection open for all repetitions.
/// </summary>
public static class PacketSender
{
    private const int MaxResponses = 1000;

    public static async Task<SendResult> SendAsync(SendOptions options, IProgress<SendProgress>? progress = null, CancellationToken ct = default)
    {
        Validate(options);
        var endpoint = await ResolveAsync(options.Host, options.Port, ct);
        var stopwatch = Stopwatch.StartNew();
        var responses = new List<SendResponse>();
        long sent = 0, bytes = 0, errors = 0;
        string? lastError = null;

        using var udp = options.Protocol == TransportProtocol.Udp ? new UdpClient(endpoint.AddressFamily) : null;
        using var tcp = options.Protocol == TransportProtocol.Tcp ? new TcpClient(endpoint.AddressFamily) : null;
        if (tcp is not null)
        {
            await tcp.ConnectAsync(endpoint, ct);
            tcp.NoDelay = true;
        }

        for (var i = 0; options.Count == 0 || i < options.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i > 0 && options.Interval > TimeSpan.Zero)
                await Task.Delay(options.Interval, ct);

            try
            {
                if (udp is not null)
                    await udp.SendAsync(options.Payload, endpoint, ct);
                else
                    await tcp!.GetStream().WriteAsync(options.Payload, ct);

                sent++;
                bytes += options.Payload.Length;

                if (options.ResponseTimeout > TimeSpan.Zero
                    && await ReceiveAsync(udp, tcp, options.ResponseTimeout, ct) is { } response
                    && responses.Count < MaxResponses)
                {
                    responses.Add(response);
                }
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                errors++;
                lastError = ex.Message;
                if (tcp is not null)
                    break; // the connection is gone; further writes would fail the same way
            }

            progress?.Report(new SendProgress(sent, bytes, errors, lastError));
        }

        return new SendResult(sent, bytes, errors, responses, stopwatch.Elapsed);
    }

    internal static async Task<IPEndPoint> ResolveAsync(string host, int port, CancellationToken ct)
    {
        if (IPAddress.TryParse(host.Trim(), out var address))
            return new IPEndPoint(address, port);

        var addresses = await Dns.GetHostAddressesAsync(host.Trim(), ct);
        var chosen = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault()
                     ?? throw new SocketException((int)SocketError.HostNotFound);
        return new IPEndPoint(chosen, port);
    }

    private static void Validate(SendOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Host))
            throw new ArgumentException("Host is required.", nameof(options));
        if (options.Port is < 1 or > ushort.MaxValue)
            throw new ArgumentException("Port must be between 1 and 65535.", nameof(options));
        if (options.Count < 0)
            throw new ArgumentException("Count cannot be negative.", nameof(options));
    }

    private static async Task<SendResponse?> ReceiveAsync(UdpClient? udp, TcpClient? tcp, TimeSpan timeout, CancellationToken ct)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(timeout);
        try
        {
            if (udp is not null)
            {
                var result = await udp.ReceiveAsync(timer.Token);
                return new SendResponse(DateTimeOffset.Now, result.RemoteEndPoint.ToString(), result.Buffer);
            }

            var buffer = new byte[64 * 1024];
            var read = await tcp!.GetStream().ReadAsync(buffer, timer.Token);
            return read == 0 ? null : new SendResponse(DateTimeOffset.Now, tcp.Client.RemoteEndPoint?.ToString() ?? "", buffer[..read]);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // no reply within the timeout
        }
    }
}
