using System.Buffers.Binary;
using PacketDotNet;
using UdpTcpListener.Core.Export;
using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.Core.Tests;

public sealed class PcapNgAndRotationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "utl-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static PacketRecord Ethernet(int n, int payload = 10) =>
        PacketDecoder.TryDecode(LinkLayers.Ethernet, TestPackets.EthernetUdp("10.0.0.1", "10.0.0.2", 5000, 53, new byte[payload]),
            TestPackets.Time.AddMilliseconds(n * 1.5), n)!;

    private static PacketRecord RawIp(int n) =>
        PacketDecoder.TryDecode(LinkLayers.Raw, TestPackets.RawIpUdp("10.8.0.1", "10.8.0.2", 1194, 1194, [1, 2, 3]),
            TestPackets.Time.AddSeconds(n), n)!;

    private static byte[] WritePcapNg(params PacketRecord[] packets)
    {
        var ms = new MemoryStream();
        var writer = new PcapNgPacketWriter(ms);
        foreach (var p in packets)
            writer.Write(p);
        return ms.ToArray(); // not disposed: ToArray works on the open stream
    }

    [Fact]
    public void Pcapng_round_trips_and_keeps_microseconds()
    {
        var packets = new[] { Ethernet(1), Ethernet(2), Ethernet(3) };

        var read = CaptureFile.Read(new MemoryStream(WritePcapNg(packets))).ToList();

        Assert.Equal(3, read.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(packets[i].RawData, read[i].RawData);
            Assert.Equal(packets[i].Timestamp.UtcTicks / 10, read[i].Timestamp.UtcTicks / 10);
            Assert.Equal(i + 1, read[i].Number);
        }
    }

    [Fact]
    public void Pcapng_can_mix_link_types()
    {
        // Classic pcap would have to drop the second packet; pcapng keeps both.
        var read = CaptureFile.Read(new MemoryStream(WritePcapNg(Ethernet(1), RawIp(2), Ethernet(3)))).ToList();

        Assert.Equal([LinkLayers.Ethernet, LinkLayers.Raw, LinkLayers.Ethernet], read.Select(p => p.LinkType));
        Assert.Equal("10.8.0.1:1194", read[1].Source);
    }

    [Fact]
    public void Pcapng_starts_with_section_header()
    {
        var bytes = WritePcapNg(Ethernet(1));
        Assert.Equal(0x0A0D0D0Au, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(0x1A2B3C4Du, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)));
        Assert.Equal(0, bytes.Length % 4);
    }

    [Fact]
    public void Reads_big_endian_pcapng_with_nanosecond_resolution()
    {
        var frame = TestPackets.EthernetUdp("10.0.0.1", "10.0.0.2", 1, 2, [7]);
        var ns = (ulong)(TestPackets.Time.ToUnixTimeMilliseconds() * 1_000_000 + 123_456_789 % 1_000_000);
        var bytes = BigEndianFile(frame, ns);

        var packet = Assert.Single(CaptureFile.Read(new MemoryStream(bytes)));

        Assert.Equal(frame, packet.RawData);
        Assert.Equal((long)ns / 100, (packet.Timestamp.UtcTicks - DateTimeOffset.UnixEpoch.Ticks));
    }

    [Fact]
    public void Classic_pcap_is_still_detected()
    {
        var ms = new MemoryStream();
        using (var writer = new PcapPacketWriter(new KeepOpen(ms)))
            writer.Write(Ethernet(1));
        ms.Position = 0;

        Assert.Single(CaptureFile.Read(ms));
    }

    [Fact]
    public void Rejects_truncated_and_garbage_files()
    {
        var bytes = WritePcapNg(Ethernet(1));
        Assert.Throws<EndOfStreamException>(() => CaptureFile.Read(new MemoryStream(bytes[..^6])).ToList());
        Assert.Throws<InvalidDataException>(() => CaptureFile.Read(new MemoryStream(new byte[64])).ToList());
    }

    [Fact]
    public void Rotates_by_size_and_keeps_only_newest_files()
    {
        var options = new RotationOptions { Directory = _dir, MaxFileBytes = 1000, MaxFiles = 3 };
        using (var writer = new RotatingPacketWriter(options))
        {
            for (var i = 1; i <= 50; i++)
                writer.Write(Ethernet(i, payload: 100));

            Assert.True(writer.FilesCreated > 3);
            Assert.Equal(50, writer.PacketsWritten);
        }

        var files = Directory.GetFiles(_dir).Order().ToArray();
        Assert.Equal(3, files.Length);
        Assert.All(files, f => Assert.Matches(@"capture_\d{5}_\d{8}_\d{6}\.pcapng$", f));
        Assert.All(files, f => Assert.True(new FileInfo(f).Length < 1000 + 200));

        // The newest file holds the last packets and is readable.
        var last = CaptureFile.Read(files[^1]).ToList();
        Assert.NotEmpty(last);
        Assert.Equal(TestPackets.Time.AddMilliseconds(50 * 1.5).UtcTicks / 10, last[^1].Timestamp.UtcTicks / 10);
    }

    [Fact]
    public void Rotates_by_age()
    {
        var now = TestPackets.Time;
        var options = new RotationOptions { Directory = _dir, MaxFileBytes = 0, MaxFileAge = TimeSpan.FromMinutes(1), Format = ExportFormat.Pcap };
        using (var writer = new RotatingPacketWriter(options, () => now))
        {
            writer.Write(Ethernet(1));
            now = now.AddSeconds(30);
            writer.Write(Ethernet(2));
            now = now.AddSeconds(31);
            writer.Write(Ethernet(3));
            Assert.Equal(2, writer.FilesCreated);
        }

        var counts = Directory.GetFiles(_dir, "*.pcap").Order().Select(f => CaptureFile.Read(f).Count()).ToArray();
        Assert.Equal([2, 1], counts);
    }

    private static byte[] BigEndianFile(byte[] frame, ulong nanoseconds)
    {
        var ms = new MemoryStream();
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); ms.Write(b); }
        void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); ms.Write(b); }

        // Section header block (no options).
        U32(0x0A0D0D0A); U32(28); U32(0x1A2B3C4D); U16(1); U16(0); U32(0xFFFFFFFF); U32(0xFFFFFFFF); U32(28);
        // Interface description: Ethernet, if_tsresol = 9 (nanoseconds), end of options.
        U32(1); U32(32); U16(1); U16(0); U32(65535); U16(9); U16(1); ms.Write([9, 0, 0, 0]); U32(0); U32(32);
        // Enhanced packet block.
        var padded = (frame.Length + 3) & ~3;
        var total = (uint)(32 + padded);
        U32(6); U32(total); U32(0); U32((uint)(nanoseconds >> 32)); U32((uint)nanoseconds);
        U32((uint)frame.Length); U32((uint)frame.Length); ms.Write(frame); ms.Write(new byte[padded - frame.Length]); U32(total);
        return ms.ToArray();
    }

    private sealed class KeepOpen(Stream inner) : Stream
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
