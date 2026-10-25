using System.Globalization;
using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.Core.Export;

public sealed record RotationOptions
{
    public required string Directory { get; init; }
    public string BaseName { get; init; } = "capture";
    public ExportFormat Format { get; init; } = ExportFormat.PcapNg;

    /// <summary>Start a new file once the current one reaches this size. 0 = no size limit.</summary>
    public long MaxFileBytes { get; init; } = 100L * 1024 * 1024;

    /// <summary>Start a new file once the current one is this old. Null = no time limit.</summary>
    public TimeSpan? MaxFileAge { get; init; }

    /// <summary>Keep only the newest N files written by this session (ring buffer). 0 = keep all.</summary>
    public int MaxFiles { get; init; }
}

/// <summary>
/// Writes a long-running capture to disk as a series of files
/// (<c>capture_00001_20260101_120000.pcapng</c>, …), starting a new file by size or age and
/// deleting the oldest ones so disk usage stays bounded.
/// </summary>
public sealed class RotatingPacketWriter : IPacketWriter
{
    private readonly RotationOptions _options;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Queue<string> _files = new();
    private IPacketWriter? _writer;
    private CountingStream? _stream;
    private DateTimeOffset _openedAt;
    private int _index;

    public RotatingPacketWriter(RotationOptions options, Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Directory);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxFiles);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxFileBytes);
        _options = options;
        _clock = clock ?? (() => DateTimeOffset.Now);
        System.IO.Directory.CreateDirectory(options.Directory);
    }

    /// <summary>The file currently being written, if any.</summary>
    public string? CurrentFile { get; private set; }

    public int FilesCreated => _index;
    public long PacketsWritten { get; private set; }

    public void Write(PacketRecord packet)
    {
        if (_writer is null || ShouldRotate())
            OpenNext();

        _writer!.Write(packet);
        PacketsWritten++;
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _writer = null;
        _stream = null;
    }

    private bool ShouldRotate() =>
        (_options.MaxFileBytes > 0 && _stream!.BytesWritten >= _options.MaxFileBytes)
        || (_options.MaxFileAge is { } age && _clock() - _openedAt >= age);

    private void OpenNext()
    {
        _writer?.Dispose();

        _index++;
        _openedAt = _clock();
        var name = string.Create(CultureInfo.InvariantCulture,
            $"{_options.BaseName}_{_index:D5}_{_openedAt:yyyyMMdd_HHmmss}{PacketWriter.Extension(_options.Format)}");
        CurrentFile = Path.Combine(_options.Directory, name);

        _stream = new CountingStream(File.Create(CurrentFile));
        _writer = PacketWriter.Create(_stream, _options.Format);
        _files.Enqueue(CurrentFile);

        while (_options.MaxFiles > 0 && _files.Count > _options.MaxFiles)
        {
            var oldest = _files.Dequeue();
            try
            {
                File.Delete(oldest);
            }
            catch (IOException)
            {
                // Opened elsewhere (e.g. in Wireshark): leave it rather than stop the capture.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesWritten { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            BytesWritten += buffer.Length;
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
