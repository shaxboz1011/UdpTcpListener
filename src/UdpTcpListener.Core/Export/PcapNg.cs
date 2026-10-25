using System.Buffers.Binary;
using System.Text;
using PacketDotNet;
using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.Core.Export;

/// <summary>
/// pcapng writer (Wireshark's default format). Unlike classic pcap it can mix link types:
/// one Interface Description Block is written per link type, and each packet is an
/// Enhanced Packet Block with a microsecond timestamp.
/// </summary>
public sealed class PcapNgPacketWriter : IPacketWriter
{
    private readonly Stream _stream;
    private readonly Dictionary<LinkLayers, uint> _interfaces = [];

    public PcapNgPacketWriter(Stream stream)
    {
        _stream = stream;
        WriteSectionHeader();
    }

    public void Write(PacketRecord packet)
    {
        if (!_interfaces.TryGetValue(packet.LinkType, out var interfaceId))
        {
            interfaceId = (uint)_interfaces.Count;
            _interfaces[packet.LinkType] = interfaceId;
            WriteInterfaceDescription(packet.LinkType);
        }

        var micros = (ulong)((packet.Timestamp.UtcTicks - DateTimeOffset.UnixEpoch.Ticks) / 10);
        var data = packet.RawData;
        var padded = Pad4(data.Length);
        var total = 32 + padded;

        Span<byte> header = stackalloc byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(header, PcapNg.EnhancedPacketBlock);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], interfaceId);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], (uint)(micros >> 32));
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)micros);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)Math.Max(packet.Length, data.Length));
        _stream.Write(header);
        _stream.Write(data);
        _stream.Write(stackalloc byte[padded - data.Length]);
        WriteUInt32((uint)total);
    }

    public void Dispose() => _stream.Dispose();

    private void WriteSectionHeader()
    {
        var app = Encoding.UTF8.GetBytes("UdpTcpListener");
        var optionsLength = 4 + Pad4(app.Length) + 4; // shb_userappl + opt_endofopt
        var total = 28 + optionsLength;

        Span<byte> block = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(block, PcapNg.SectionHeaderBlock);
        BinaryPrimitives.WriteUInt32LittleEndian(block[4..], (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(block[8..], PcapNg.ByteOrderMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(block[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(block[14..], 0);
        BinaryPrimitives.WriteInt64LittleEndian(block[16..], -1); // section length unknown
        _stream.Write(block);
        WriteOption(4, app);
        WriteUInt32(0); // opt_endofopt
        WriteUInt32((uint)total);
    }

    private void WriteInterfaceDescription(LinkLayers linkType)
    {
        Span<byte> block = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(block, PcapNg.InterfaceDescriptionBlock);
        BinaryPrimitives.WriteUInt32LittleEndian(block[4..], 20);
        BinaryPrimitives.WriteUInt16LittleEndian(block[8..], (ushort)linkType);
        BinaryPrimitives.WriteUInt16LittleEndian(block[10..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(block[12..], PcapPacketWriter.SnapLength);
        _stream.Write(block);
        WriteUInt32(20);
    }

    private void WriteOption(ushort code, byte[] value)
    {
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(header, code);
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], (ushort)value.Length);
        _stream.Write(header);
        _stream.Write(value);
        _stream.Write(stackalloc byte[Pad4(value.Length) - value.Length]);
    }

    private void WriteUInt32(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        _stream.Write(buffer);
    }

    private static int Pad4(int length) => (length + 3) & ~3;
}

/// <summary>pcapng reader: any byte order, multiple sections and interfaces, any timestamp resolution.</summary>
public static class PcapNg
{
    internal const uint SectionHeaderBlock = 0x0A0D0D0A;
    internal const uint InterfaceDescriptionBlock = 1;
    internal const uint ObsoletePacketBlock = 2;
    internal const uint SimplePacketBlock = 3;
    internal const uint EnhancedPacketBlock = 6;
    internal const uint ByteOrderMagic = 0x1A2B3C4D;
    private const int MaxBlockLength = 64 * 1024 * 1024;

    public static IEnumerable<PacketRecord> Read(Stream stream)
    {
        var interfaces = new List<Interface>();
        var bigEndian = false;
        var inSection = false;
        var header = new byte[8];
        long number = 0;

        while (stream.ReadAtLeast(header, 8, throwOnEndOfStream: false) == 8)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(header); // SHB type is a palindrome
            if (type == SectionHeaderBlock)
            {
                var magic = new byte[4];
                ReadExactly(stream, magic);
                bigEndian = BinaryPrimitives.ReadUInt32LittleEndian(magic) switch
                {
                    ByteOrderMagic => false,
                    0x4D3C2B1A => true,
                    _ => throw new InvalidDataException("Corrupt pcapng section header (bad byte-order magic)."),
                };
                var sectionLength = U32(header.AsSpan(4), bigEndian);
                SkipBody(stream, sectionLength, alreadyRead: 12);
                interfaces.Clear(); // interface ids are per section
                inSection = true;
                continue;
            }

            if (!inSection)
                throw new InvalidDataException("Not a pcapng file (missing section header).");

            type = U32(header, bigEndian);
            var length = U32(header.AsSpan(4), bigEndian);
            if (length < 12 || length % 4 != 0 || length > MaxBlockLength)
                throw new InvalidDataException($"Corrupt pcapng block (length {length}).");

            var body = new byte[length - 12];
            ReadExactly(stream, body);
            ReadExactly(stream, new byte[4]); // trailing length

            switch (type)
            {
                case InterfaceDescriptionBlock:
                    interfaces.Add(ParseInterface(body, bigEndian));
                    break;
                case EnhancedPacketBlock when body.Length >= 20:
                {
                    var iface = InterfaceAt(interfaces, (int)U32(body, bigEndian));
                    var ts = ((ulong)U32(body.AsSpan(4), bigEndian) << 32) | U32(body.AsSpan(8), bigEndian);
                    var captured = (int)Math.Min(U32(body.AsSpan(12), bigEndian), (uint)(body.Length - 20));
                    var original = U32(body.AsSpan(16), bigEndian);
                    number++;
                    if (Decode(iface, body.AsSpan(20, captured), ts, original, number) is { } packet)
                        yield return packet;
                    break;
                }
                case ObsoletePacketBlock when body.Length >= 20:
                {
                    var iface = InterfaceAt(interfaces, U16(body, bigEndian));
                    var ts = ((ulong)U32(body.AsSpan(4), bigEndian) << 32) | U32(body.AsSpan(8), bigEndian);
                    var captured = (int)Math.Min(U32(body.AsSpan(12), bigEndian), (uint)(body.Length - 20));
                    var original = U32(body.AsSpan(16), bigEndian);
                    number++;
                    if (Decode(iface, body.AsSpan(20, captured), ts, original, number) is { } packet)
                        yield return packet;
                    break;
                }
                case SimplePacketBlock when body.Length >= 4:
                {
                    var iface = InterfaceAt(interfaces, 0);
                    var original = U32(body, bigEndian);
                    var captured = (int)Math.Min(Math.Min(original, iface.SnapLength == 0 ? uint.MaxValue : iface.SnapLength), (uint)(body.Length - 4));
                    number++;
                    // Simple packet blocks carry no timestamp.
                    if (Decode(iface, body.AsSpan(4, captured), null, original, number) is { } packet)
                        yield return packet;
                    break;
                }
            }
        }
    }

    private static PacketRecord? Decode(Interface iface, ReadOnlySpan<byte> data, ulong? timestamp, uint original, long number)
    {
        var time = timestamp is { } ts ? ToTime(ts, iface.UnitsPerSecond) : DateTimeOffset.UnixEpoch.ToLocalTime();
        return PacketDecoder.TryDecode(iface.LinkType, data.ToArray(), time, number, (int)Math.Min(original, int.MaxValue));
    }

    internal static DateTimeOffset ToTime(ulong value, ulong unitsPerSecond)
    {
        var seconds = value / unitsPerSecond;
        var ticks = (ulong)((UInt128)(value % unitsPerSecond) * TimeSpan.TicksPerSecond / unitsPerSecond);
        return DateTimeOffset.UnixEpoch.AddSeconds(seconds).AddTicks((long)ticks).ToLocalTime();
    }

    private static Interface ParseInterface(byte[] body, bool bigEndian)
    {
        if (body.Length < 8)
            throw new InvalidDataException("Corrupt pcapng interface block.");

        var linkType = (LinkLayers)U16(body, bigEndian);
        var snapLength = U32(body.AsSpan(4), bigEndian);
        ulong unitsPerSecond = 1_000_000;

        // Options: code(2) length(2) value padded to 4.
        var offset = 8;
        while (offset + 4 <= body.Length)
        {
            var code = U16(body.AsSpan(offset), bigEndian);
            var length = U16(body.AsSpan(offset + 2), bigEndian);
            if (code == 0 || offset + 4 + length > body.Length)
                break;
            if (code == 9 && length >= 1) // if_tsresol
            {
                var resolution = body[offset + 4];
                var exponent = resolution & 0x7F;
                unitsPerSecond = (resolution & 0x80) != 0
                    ? (exponent < 64 ? 1UL << exponent : ulong.MaxValue)
                    : (exponent <= 19 ? (ulong)Math.Pow(10, exponent) : ulong.MaxValue);
            }
            offset += 4 + ((length + 3) & ~3);
        }

        return new Interface(linkType, snapLength, Math.Max(unitsPerSecond, 1));
    }

    private static Interface InterfaceAt(List<Interface> interfaces, int id) =>
        id >= 0 && id < interfaces.Count ? interfaces[id] : throw new InvalidDataException($"pcapng packet refers to unknown interface {id}.");

    private static void SkipBody(Stream stream, uint totalLength, int alreadyRead)
    {
        if (totalLength < 28 || totalLength % 4 != 0 || totalLength > MaxBlockLength)
            throw new InvalidDataException($"Corrupt pcapng section header (length {totalLength}).");
        ReadExactly(stream, new byte[totalLength - alreadyRead]);
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        if (stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) < buffer.Length)
            throw new EndOfStreamException("The pcapng file ends in the middle of a block.");
    }

    private static uint U32(ReadOnlySpan<byte> span, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(span) : BinaryPrimitives.ReadUInt32LittleEndian(span);

    private static ushort U16(ReadOnlySpan<byte> span, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(span) : BinaryPrimitives.ReadUInt16LittleEndian(span);

    private sealed record Interface(LinkLayers LinkType, uint SnapLength, ulong UnitsPerSecond);
}

/// <summary>Opens a capture file in either classic pcap or pcapng format, detected from its contents.</summary>
public static class CaptureFile
{
    public static IEnumerable<PacketRecord> Read(string path)
    {
        using var stream = File.OpenRead(path);
        foreach (var packet in Read(stream))
            yield return packet;
    }

    public static IEnumerable<PacketRecord> Read(Stream stream)
    {
        if (!stream.CanSeek)
        {
            var copy = new MemoryStream();
            stream.CopyTo(copy);
            copy.Position = 0;
            stream = copy;
        }

        var start = stream.Position;
        var magic = new byte[4];
        var read = stream.ReadAtLeast(magic, 4, throwOnEndOfStream: false);
        stream.Position = start;
        if (read < 4)
            throw new InvalidDataException("The file is too short to be a capture file.");

        return BinaryPrimitives.ReadUInt32LittleEndian(magic) == PcapNg.SectionHeaderBlock
            ? PcapNg.Read(stream)
            : PcapFileReader.Read(stream);
    }
}
