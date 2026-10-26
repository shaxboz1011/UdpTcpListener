using SharpPcap;
using UdpTcpListener.Core.Packets;
using UdpTcpListener.Core.Sending;

namespace UdpTcpListener.Core.Capture;

/// <summary>
/// Replays captured frames byte-for-byte onto a network interface (pcap_inject / Npcap SendPacket).
/// Unlike payload replay this keeps the original MAC/IP headers, so it needs capture rights and a
/// device whose link type matches the capture.
/// </summary>
public static class FrameInjector
{
    public static async Task<long> InjectAsync(
        string deviceName,
        IReadOnlyList<PacketRecord> packets,
        ReplayOptions options,
        IProgress<ReplayProgress>? progress = null,
        CancellationToken ct = default)
    {
        var device = CaptureDeviceList.New().FirstOrDefault(d => d.Name == deviceName)
                     ?? throw new InvalidOperationException($"Device '{deviceName}' was not found.");

        device.Open(DeviceModes.None);
        try
        {
            var linkType = device.LinkType;
            var delays = Replay.Delays(packets.Select(p => p.Timestamp).ToList(), options);
            long sent = 0, errors = 0;
            string? lastError = null;

            for (var loop = 0; options.Loops == 0 || loop < options.Loops; loop++)
            {
                for (var i = 0; i < packets.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (delays[i] > TimeSpan.Zero)
                        await Task.Delay(delays[i], ct);

                    var packet = packets[i];
                    if (packet.LinkType != linkType)
                    {
                        errors++;
                        lastError = $"Frame #{packet.Number} is {packet.LinkType}, but the device uses {linkType}.";
                    }
                    else
                    {
                        try
                        {
                            device.SendPacket(packet.RawData);
                            sent++;
                        }
                        catch (PcapException ex)
                        {
                            errors++;
                            lastError = ex.Message;
                        }
                    }

                    progress?.Report(new ReplayProgress(sent, packets.Count * (long)Math.Max(1, options.Loops), errors, lastError));
                }
            }

            return sent;
        }
        finally
        {
            device.Close();
        }
    }
}
