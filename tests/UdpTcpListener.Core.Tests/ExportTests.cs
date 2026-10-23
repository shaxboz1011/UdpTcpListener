using System.Text;
using System.Text.Json;
using PacketDotNet;
using UdpTcpListener.Core.Export;
using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.Core.Tests;

public class ExportTests
{
    private static PacketRecord[] SamplePackets() =>
    [
        PacketDecoder.TryDecode(LinkLayers.Ethernet, TestPackets.EthernetUdp("10.0.0.1", "10.0.0.2", 5000, 53, "hi"u8.ToArray()), TestPackets.Time, 1)!,
        PacketDecoder.TryDecode(LinkLayers.Ethernet, TestPackets.EthernetTcp("10.0.0.3", "10.0.0.4", 40000, 443, [], syn: true), TestPackets.Time.AddMilliseconds(1.5), 2)!,
    ];

    [Fact]
    public void Pcap_round_trips()
    {
        var packets = SamplePackets();
        using var ms = new MemoryStream();
        using (var writer = new PcapPacketWriter(new NonClosingStream(ms)))
        {
            foreach (var p in packets)
                writer.Write(p);
        }

        ms.Position = 0;
        var read = PcapFileReader.Read(ms).ToList();

        Assert.Equal(2, read.Count);
        for (var i = 0; i < packets.Length; i++)
        {
            Assert.Equal(packets[i].RawData, read[i].RawData);
            Assert.Equal(packets[i].Source, read[i].Source);
            Assert.Equal(packets[i].Timestamp.UtcTicks / 10, read[i].Timestamp.UtcTicks / 10);
            Assert.Equal(i + 1, read[i].Number);
        }
    }

    [Fact]
    public void Pcap_header_is_standard()
    {
        using var ms = new MemoryStream();
        using (var writer = new PcapPacketWriter(new NonClosingStream(ms)))
            writer.Write(SamplePackets()[0]);

        var bytes = ms.ToArray();
        Assert.Equal(new byte[] { 0xd4, 0xc3, 0xb2, 0xa1, 2, 0, 4, 0 }, bytes[..8]);
        Assert.Equal(1u, BitConverter.ToUInt32(bytes, 20)); // DLT_EN10MB
    }

    [Fact]
    public void Pcap_reader_rejects_pcapng()
    {
        using var ms = new MemoryStream([0x0a, 0x0d, 0x0d, 0x0a, .. new byte[20]]);
        Assert.Throws<NotSupportedException>(() => PcapFileReader.Read(ms).ToList());
    }

    [Fact]
    public void Csv_has_header_and_rows()
    {
        var text = WriteToString(ExportFormat.Csv);
        var lines = text.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        Assert.Equal(3, lines.Length);
        Assert.StartsWith("No,Time,Protocol", lines[0].TrimStart('﻿'));
        Assert.EndsWith(",2,6869", lines[1]);
        Assert.Contains(",SYN,", lines[2]);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData(null, "")]
    public void Csv_escapes(string? input, string expected) => Assert.Equal(expected, CsvPacketWriter.Escape(input));

    [Fact]
    public void Json_is_a_valid_array()
    {
        using var doc = JsonDocument.Parse(WriteToString(ExportFormat.Json));

        Assert.Equal(2, doc.RootElement.GetArrayLength());
        Assert.Equal("UDP", doc.RootElement[0].GetProperty("protocol").GetString());
        Assert.Equal(53, doc.RootElement[0].GetProperty("destinationPort").GetInt32());
        Assert.Equal("SYN", doc.RootElement[1].GetProperty("tcpFlags").GetString());
    }

    [Fact]
    public void Text_keeps_original_log_format()
    {
        var text = WriteToString(ExportFormat.Text);

        Assert.Contains("Protocol: UDP\n001122334455 / 10.0.0.1:5000 -> 66778899AABB / 10.0.0.2:53\nData: 68-69\n", text);
    }

    [Theory]
    [InlineData("a.pcap", ExportFormat.Pcap)]
    [InlineData("a.CSV", ExportFormat.Csv)]
    [InlineData("a.json", ExportFormat.Json)]
    [InlineData("a.txt", ExportFormat.Text)]
    [InlineData("noext", ExportFormat.Text)]
    public void Picks_format_from_extension(string path, ExportFormat expected) =>
        Assert.Equal(expected, PacketWriter.FormatFromPath(path));

    private static string WriteToString(ExportFormat format)
    {
        using var ms = new MemoryStream();
        using (var writer = PacketWriter.Create(new NonClosingStream(ms), format))
        {
            foreach (var p in SamplePackets())
                writer.Write(p);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) => inner.Flush();
    }
}
