using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PacketDotNet;
using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.Core.Export;

public enum ExportFormat
{
    Pcap,
    PcapNg,
    Csv,
    Json,
    Text,
}

/// <summary>Streams packets to a file. Writers are not thread-safe.</summary>
public interface IPacketWriter : IDisposable
{
    void Write(PacketRecord packet);
}

public static class PacketWriter
{
    public static ExportFormat FormatFromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pcap" => ExportFormat.Pcap,
        ".pcapng" => ExportFormat.PcapNg,
        ".csv" => ExportFormat.Csv,
        ".json" => ExportFormat.Json,
        _ => ExportFormat.Text,
    };

    public static string Extension(ExportFormat format) => format switch
    {
        ExportFormat.Pcap => ".pcap",
        ExportFormat.PcapNg => ".pcapng",
        ExportFormat.Csv => ".csv",
        ExportFormat.Json => ".json",
        _ => ".txt",
    };

    public static IPacketWriter Create(string path) => Create(File.Create(path), FormatFromPath(path));

    public static IPacketWriter Create(Stream stream, ExportFormat format) => format switch
    {
        ExportFormat.Pcap => new PcapPacketWriter(stream),
        ExportFormat.PcapNg => new PcapNgPacketWriter(stream),
        ExportFormat.Csv => new CsvPacketWriter(stream),
        ExportFormat.Json => new JsonPacketWriter(stream),
        _ => new TextPacketWriter(stream),
    };

    /// <summary>Writes all packets to <paramref name="path"/>, choosing the format from the file extension.</summary>
    public static int Export(string path, IEnumerable<PacketRecord> packets)
    {
        using var writer = Create(path);
        var count = 0;
        foreach (var packet in packets)
        {
            writer.Write(packet);
            count++;
        }
        return count;
    }
}

/// <summary>
/// Classic libpcap file format (microsecond timestamps), readable by Wireshark and tcpdump.
/// The link type is taken from the first packet; frames with a different link type are skipped
/// because a classic pcap file can only hold one.
/// </summary>
public sealed class PcapPacketWriter(Stream stream) : IPacketWriter
{
    public const uint Magic = 0xa1b2c3d4;
    public const int SnapLength = 262144;

    private LinkLayers? _linkType;

    public int SkippedPackets { get; private set; }

    public void Write(PacketRecord packet)
    {
        if (_linkType is null)
        {
            _linkType = packet.LinkType;
            Span<byte> header = stackalloc byte[24];
            BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 2);
            BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 4);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(header[16..], SnapLength);
            BinaryPrimitives.WriteUInt32LittleEndian(header[20..], (uint)packet.LinkType);
            stream.Write(header);
        }
        else if (_linkType != packet.LinkType)
        {
            SkippedPackets++;
            return;
        }

        var micros = (packet.Timestamp.UtcTicks - DateTimeOffset.UnixEpoch.Ticks) / (TimeSpan.TicksPerMillisecond / 1000);
        Span<byte> record = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)(micros / 1_000_000));
        BinaryPrimitives.WriteUInt32LittleEndian(record[4..], (uint)(micros % 1_000_000));
        BinaryPrimitives.WriteUInt32LittleEndian(record[8..], (uint)packet.RawData.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(record[12..], (uint)Math.Max(packet.Length, packet.RawData.Length));
        stream.Write(record);
        stream.Write(packet.RawData);
    }

    public void Dispose() => stream.Dispose();
}

public sealed class CsvPacketWriter : IPacketWriter
{
    private readonly StreamWriter _writer;

    public CsvPacketWriter(Stream stream)
    {
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        _writer.WriteLine("No,Time,Protocol,Source MAC,Source,Destination MAC,Destination,Length,Flags,Payload Length,Payload Hex");
    }

    public void Write(PacketRecord p)
    {
        _writer.Write(p.Number.ToString(CultureInfo.InvariantCulture));
        _writer.Write(',');
        _writer.Write(p.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture));
        _writer.Write(',');
        _writer.Write(p.ProtocolName);
        _writer.Write(',');
        _writer.Write(Escape(p.SourceMac));
        _writer.Write(',');
        _writer.Write(Escape(p.Source));
        _writer.Write(',');
        _writer.Write(Escape(p.DestinationMac));
        _writer.Write(',');
        _writer.Write(Escape(p.Destination));
        _writer.Write(',');
        _writer.Write(p.Length.ToString(CultureInfo.InvariantCulture));
        _writer.Write(',');
        _writer.Write(Escape(p.TcpFlags));
        _writer.Write(',');
        _writer.Write(p.Payload.Length.ToString(CultureInfo.InvariantCulture));
        _writer.Write(',');
        _writer.WriteLine(Convert.ToHexString(p.Payload));
    }

    public void Dispose() => _writer.Dispose();

    internal static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}

/// <summary>Writes a JSON array, streaming one object per packet.</summary>
public sealed class JsonPacketWriter : IPacketWriter
{
    private readonly Utf8JsonWriter _json;

    public JsonPacketWriter(Stream stream)
    {
        _json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        _json.WriteStartArray();
    }

    public void Write(PacketRecord p)
    {
        _json.WriteStartObject();
        _json.WriteNumber("number", p.Number);
        _json.WriteString("timestamp", p.Timestamp);
        _json.WriteString("protocol", p.ProtocolName);
        _json.WriteString("sourceMac", p.SourceMac);
        _json.WriteString("sourceAddress", p.SourceAddress.ToString());
        _json.WriteNumber("sourcePort", p.SourcePort);
        _json.WriteString("destinationMac", p.DestinationMac);
        _json.WriteString("destinationAddress", p.DestinationAddress.ToString());
        _json.WriteNumber("destinationPort", p.DestinationPort);
        _json.WriteNumber("length", p.Length);
        if (p.TcpFlags is not null)
            _json.WriteString("tcpFlags", p.TcpFlags);
        _json.WriteString("payloadHex", Convert.ToHexString(p.Payload));
        _json.WriteEndObject();

        if (_json.BytesPending > 64 * 1024)
            _json.Flush();
    }

    public void Dispose()
    {
        _json.WriteEndArray();
        _json.Flush();
        _json.Dispose();
    }
}

/// <summary>Human-readable log, compatible with the format of the original console tool.</summary>
public sealed class TextPacketWriter(Stream stream) : IPacketWriter
{
    private readonly StreamWriter _writer = new(stream, new UTF8Encoding(false));

    public bool AutoFlush
    {
        get => _writer.AutoFlush;
        set => _writer.AutoFlush = value;
    }

    public void Write(PacketRecord p) => _writer.WriteLine(Format(p));

    public void Dispose() => _writer.Dispose();

    public static string Format(PacketRecord p)
    {
        var src = p.SourceMac is null ? p.Source : $"{p.SourceMac.Replace(":", "")} / {p.Source}";
        var dst = p.DestinationMac is null ? p.Destination : $"{p.DestinationMac.Replace(":", "")} / {p.Destination}";
        var flags = p.TcpFlags is { Length: > 0 } ? $" [{p.TcpFlags}]" : "";
        return $"Time: {p.Timestamp.ToString("dd.MM.yyyy HH:mm:ss.fff", CultureInfo.InvariantCulture)}\n" +
               $"Protocol: {p.ProtocolName}{flags}\n" +
               $"{src} -> {dst}\n" +
               $"Data: {BitConverter.ToString(p.Payload)}\n";
    }
}
