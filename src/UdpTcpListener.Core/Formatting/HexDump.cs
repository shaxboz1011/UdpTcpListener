using System.Text;

namespace UdpTcpListener.Core.Formatting;

public static class HexDump
{
    /// <summary>
    /// Classic offset / hex / ASCII dump:
    /// <c>0000  48 65 6c 6c 6f 20 57 6f  72 6c 64 0a              Hello World.</c>
    /// </summary>
    public static string Format(ReadOnlySpan<byte> data, int bytesPerLine = 16)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytesPerLine, 1);
        if (data.IsEmpty)
            return string.Empty;

        var half = bytesPerLine / 2;
        var sb = new StringBuilder((data.Length / bytesPerLine + 1) * (bytesPerLine * 4 + 10));

        for (var offset = 0; offset < data.Length; offset += bytesPerLine)
        {
            var line = data.Slice(offset, Math.Min(bytesPerLine, data.Length - offset));
            sb.Append(offset.ToString("x4")).Append("  ");

            for (var i = 0; i < bytesPerLine; i++)
            {
                if (i == half && half > 0)
                    sb.Append(' ');
                sb.Append(i < line.Length ? line[i].ToString("x2") : "  ").Append(' ');
            }

            sb.Append(' ');
            foreach (var b in line)
                sb.Append(ToPrintable(b));

            if (offset + bytesPerLine < data.Length)
                sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Payload as printable ASCII with non-printable bytes replaced by '.', truncated to <paramref name="max"/> chars.</summary>
    public static string ToAscii(ReadOnlySpan<byte> data, int max = int.MaxValue)
    {
        var length = Math.Min(data.Length, max);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = ToPrintable(data[i]);
        return length < data.Length ? new string(chars) + "…" : new string(chars);
    }

    /// <summary>
    /// Payload as readable text for "Follow stream": printable ASCII plus tabs and line breaks
    /// (CRLF becomes a single newline); every other byte becomes '.'.
    /// </summary>
    public static string ToReadableText(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length);
        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            if (b == '\r' && i + 1 < data.Length && data[i + 1] == '\n')
                continue;
            sb.Append(b is (byte)'\n' or (byte)'\t' or (byte)'\r' ? (char)b : ToPrintable(b));
        }
        return sb.ToString();
    }

    private static char ToPrintable(byte b) => b is >= 0x20 and < 0x7f ? (char)b : '.';
}
