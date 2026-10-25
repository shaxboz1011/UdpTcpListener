using System.Net;
using System.Net.Sockets;
using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.Core.Sending;

public enum ReplayTiming
{
    /// <summary>Keep the gaps between packets, divided by <see cref="ReplayOptions.Speed"/>.</summary>
    Original,
    /// <summary>A fixed pause between packets.</summary>
    FixedInterval,
    /// <summary>No pauses.</summary>
    AsFastAsPossible,
}

public sealed record ReplayOptions
{
    public ReplayTiming Timing { get; init; } = ReplayTiming.Original;

    /// <summary>Speed-up for <see cref="ReplayTiming.Original"/> (2 = twice as fast).</summary>
    public double Speed { get; init; } = 1;

    public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Gaps longer than this are shortened, so a long idle period does not stall the replay.</summary>
    public TimeSpan MaxGap { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How many times to replay the whole list (0 = until cancelled).</summary>
    public int Loops { get; init; } = 1;
}

public sealed record ReplayProgress(long Sent, long Total, long Errors, string? LastError);

/// <summary>Replays captured packets, either as payloads over normal sockets or as raw frames.</summary>
public static class Replay
{
    /// <summary>The pause before each packet (the first is always zero).</summary>
    public static IReadOnlyList<TimeSpan> Delays(IReadOnlyList<DateTimeOffset> timestamps, ReplayOptions options)
    {
        if (options.Speed <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Speed must be positive.");

        var delays = new TimeSpan[timestamps.Count];
        for (var i = 1; i < timestamps.Count; i++)
        {
            delays[i] = options.Timing switch
            {
                ReplayTiming.FixedInterval => options.Interval,
                ReplayTiming.AsFastAsPossible => TimeSpan.Zero,
                _ => Clamp((timestamps[i] - timestamps[i - 1]) / options.Speed, options.MaxGap),
            };
        }
        return delays;
    }

    /// <summary>
    /// Sends each packet's payload again over a normal socket. UDP packets are sent as datagrams to
    /// <paramref name="target"/> (or each packet's original destination when null). TCP payloads are
    /// written in order over one connection to <paramref name="target"/>, which is required for TCP.
    /// Packets without payload (handshakes, ACKs) are skipped.
    /// </summary>
    public static async Task<long> ReplayPayloadsAsync(
        IReadOnlyList<PacketRecord> packets,
        IPEndPoint? target,
        ReplayOptions options,
        IProgress<ReplayProgress>? progress = null,
        CancellationToken ct = default)
    {
        var items = packets.Where(p => p.Payload.Length > 0).ToList();
        if (items.Any(p => p.Protocol == TransportProtocol.Tcp) && target is null)
            throw new ArgumentException("Replaying TCP payloads needs a target host and port.", nameof(target));

        var delays = Delays(items.Select(p => p.Timestamp).ToList(), options);
        // One socket per address family, created on demand: a dual-mode IPv6 socket fails where IPv6 is disabled.
        var udpSockets = new Dictionary<AddressFamily, UdpClient>();
        TcpClient? tcp = null;
        long sent = 0, errors = 0;
        string? lastError = null;

        try
        {
            for (var loop = 0; options.Loops == 0 || loop < options.Loops; loop++)
            {
                for (var i = 0; i < items.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (delays[i] > TimeSpan.Zero)
                        await Task.Delay(delays[i], ct);

                    var packet = items[i];
                    try
                    {
                        if (packet.Protocol == TransportProtocol.Udp)
                        {
                            var to = target ?? new IPEndPoint(packet.DestinationAddress, packet.DestinationPort);
                            if (!udpSockets.TryGetValue(to.AddressFamily, out var udp))
                                udpSockets[to.AddressFamily] = udp = new UdpClient(to.AddressFamily);
                            await udp.SendAsync(packet.Payload, to, ct);
                        }
                        else
                        {
                            if (tcp is null)
                            {
                                tcp = new TcpClient(target!.AddressFamily) { NoDelay = true };
                                await tcp.ConnectAsync(target, ct);
                            }
                            await tcp.GetStream().WriteAsync(packet.Payload, ct);
                        }
                        sent++;
                    }
                    catch (Exception ex) when (ex is SocketException or IOException)
                    {
                        errors++;
                        lastError = ex.Message;
                        tcp?.Dispose();
                        tcp = null; // reconnect on the next TCP payload
                    }

                    progress?.Report(new ReplayProgress(sent, items.Count * (long)Math.Max(1, options.Loops), errors, lastError));
                }
            }
        }
        finally
        {
            tcp?.Dispose();
            foreach (var udp in udpSockets.Values)
                udp.Dispose();
        }

        return sent;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan max) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value > max ? max : value;
}
