using System.Buffers.Binary;
using System.Text;
using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.Core.Decoding;

/// <summary>A decoded field; <see cref="Children"/> holds nested fields (e.g. one DNS answer).</summary>
public sealed record DecodedField(string Name, string Value, IReadOnlyList<DecodedField>? Children = null);

/// <summary>An application-layer protocol found in a packet's payload.</summary>
public sealed record DecodedLayer(string Protocol, string Summary, IReadOnlyList<DecodedField> Fields);

public interface IPayloadDecoder
{
    string Name { get; }

    /// <summary>Returns null when the payload is not this protocol. May throw on malformed data; the registry catches it.</summary>
    DecodedLayer? TryDecode(PacketRecord packet);
}

/// <summary>Thrown by <see cref="PayloadReader"/> when a field runs past the end of the payload.</summary>
public sealed class DecodeException(string message) : Exception(message);

/// <summary>Bounds-checked reader over a payload. Big-endian by default (network order).</summary>
public ref struct PayloadReader(ReadOnlySpan<byte> data, bool bigEndian = true)
{
    private readonly ReadOnlySpan<byte> _data = data;

    public int Position { get; set; }
    public readonly int Length => _data.Length;
    public readonly int Remaining => _data.Length - Position;
    public readonly ReadOnlySpan<byte> Data => _data;
    public bool BigEndian { get; set; } = bigEndian;

    public byte U8() => Take(1)[0];
    public sbyte I8() => (sbyte)Take(1)[0];
    public ushort U16() => BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(Take(2)) : BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public short I16() => BigEndian ? BinaryPrimitives.ReadInt16BigEndian(Take(2)) : BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public uint U32() => BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(Take(4)) : BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int I32() => BigEndian ? BinaryPrimitives.ReadInt32BigEndian(Take(4)) : BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public ulong U64() => BigEndian ? BinaryPrimitives.ReadUInt64BigEndian(Take(8)) : BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public long I64() => BigEndian ? BinaryPrimitives.ReadInt64BigEndian(Take(8)) : BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public float F32() => BigEndian ? BinaryPrimitives.ReadSingleBigEndian(Take(4)) : BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public double F64() => BigEndian ? BinaryPrimitives.ReadDoubleBigEndian(Take(8)) : BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

    public ReadOnlySpan<byte> Bytes(int count) => Take(count);

    public ReadOnlySpan<byte> Rest() => Take(Remaining);

    public ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining)
            throw new DecodeException($"Field at offset {Position} needs {count} bytes, only {Remaining} left.");
        var span = _data.Slice(Position, count);
        Position += count;
        return span;
    }
}

internal static class DecodeText
{
    /// <summary>Text if (almost) all bytes are printable UTF-8/ASCII, otherwise null.</summary>
    public static string? AsText(ReadOnlySpan<byte> data, double minPrintable = 0.95)
    {
        if (data.IsEmpty)
            return null;

        var printable = 0;
        foreach (var b in data)
        {
            if (b is >= 0x20 and < 0x7F or (byte)'\r' or (byte)'\n' or (byte)'\t' or >= 0x80)
                printable++;
        }
        if (printable < data.Length * minPrintable)
            return null;

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    public static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    /// <summary>Text when printable, otherwise hex; truncated for display.</summary>
    public static string Show(ReadOnlySpan<byte> data, int max = 256) =>
        AsText(data) is { } text
            ? Truncate(text.ReplaceLineEndings("⏎"), max)
            : Truncate(Convert.ToHexStringLower(data), max * 2);
}
