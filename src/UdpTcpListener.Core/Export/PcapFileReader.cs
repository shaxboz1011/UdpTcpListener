using System.Buffers.Binary;
using PacketDotNet;
using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.Core.Export;

/// <summary>
/// Reads classic libpcap files (both byte orders, micro- and nanosecond variants).
/// Non UDP/TCP frames are skipped but still advance the frame number, so numbers match Wireshark.
/// </summary>
public static class PcapFileReader
{
    private const uint MagicMicro = 0xa1b2c3d4;
    private const uint MagicNano = 0xa1b23c4d;
    private const uint PcapNgMagic = 0x0a0d0d0a;
    private const int MaxRecordLength = 64 * 1024 * 1024;

    public static IEnumerable<PacketRecord> Read(string path)
    {
        using var stream = File.OpenRead(path);
        foreach (var packet in Read(stream))
            yield return packet;
    }

    public static IEnumerable<PacketRecord> Read(Stream stream)
    {
        var header = new byte[24];
        if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
            throw new InvalidDataException("File is too short to be a pcap file.");

        var magicLe = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var magicBe = BinaryPrimitives.ReadUInt32BigEndian(header);
        bool bigEndian, nanos;

        if (magicLe is MagicMicro or MagicNano)
        {
            bigEndian = false;
            nanos = magicLe == MagicNano;
        }
        else if (magicBe is MagicMicro or MagicNano)
        {
            bigEndian = true;
            nanos = magicBe == MagicNano;
        }
        else if (magicLe == PcapNgMagic)
        {
            throw new NotSupportedException("pcapng files are not supported yet. Save the capture as 'Wireshark/tcpdump - pcap' instead.");
        }
        else
        {
            throw new InvalidDataException("Not a pcap file (unknown magic number).");
        }

        var linkType = (LinkLayers)ReadUInt32(header.AsSpan(20), bigEndian);
        var recordHeader = new byte[16];
        long number = 0;

        while (stream.ReadAtLeast(recordHeader, recordHeader.Length, throwOnEndOfStream: false) == recordHeader.Length)
        {
            var seconds = ReadUInt32(recordHeader, bigEndian);
            var fraction = ReadUInt32(recordHeader.AsSpan(4), bigEndian);
            var capturedLength = ReadUInt32(recordHeader.AsSpan(8), bigEndian);
            var originalLength = ReadUInt32(recordHeader.AsSpan(12), bigEndian);

            if (capturedLength > MaxRecordLength)
                throw new InvalidDataException($"Corrupt pcap record #{number + 1} (length {capturedLength}).");

            var data = new byte[capturedLength];
            if (stream.ReadAtLeast(data, data.Length, throwOnEndOfStream: false) < data.Length)
                yield break; // truncated last record

            number++;
            var ticks = nanos ? fraction / 100 : fraction * 10L;
            var timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(ticks).ToLocalTime();

            var record = PacketDecoder.TryDecode(linkType, data, timestamp, number, (int)Math.Min(originalLength, int.MaxValue));
            if (record is not null)
                yield return record;
        }
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> span, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(span) : BinaryPrimitives.ReadUInt32LittleEndian(span);
}
