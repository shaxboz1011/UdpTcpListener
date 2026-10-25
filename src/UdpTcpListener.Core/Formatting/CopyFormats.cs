using System.Text;

namespace UdpTcpListener.Core.Formatting;

public enum CopyFormat
{
    /// <summary><c>48 65 6c 6c 6f</c></summary>
    Hex,
    /// <summary><c>48656c6c6f</c></summary>
    HexStream,
    /// <summary><c>unsigned char data[] = { 0x48, ... };</c></summary>
    CArray,
    Base64,
    /// <summary>Printable ASCII, other bytes as '.'</summary>
    Text,
    /// <summary>Offset / hex / ASCII dump.</summary>
    HexDump,
}

/// <summary>Formats bytes for the clipboard ("Copy as…").</summary>
public static class CopyFormats
{
    public static string Format(ReadOnlySpan<byte> data, CopyFormat format) => format switch
    {
        CopyFormat.Hex => string.Join(' ', data.ToArray().Select(b => b.ToString("x2"))),
        CopyFormat.HexStream => Convert.ToHexStringLower(data),
        CopyFormat.CArray => ToCArray(data),
        CopyFormat.Base64 => Convert.ToBase64String(data),
        CopyFormat.Text => HexDump.ToAscii(data),
        CopyFormat.HexDump => HexDump.Format(data),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static string ToCArray(ReadOnlySpan<byte> data)
    {
        const int perLine = 12;
        var sb = new StringBuilder(data.Length * 6 + 64);
        sb.Append("unsigned char data[").Append(data.Length).Append("] = {");
        for (var i = 0; i < data.Length; i++)
        {
            sb.Append(i % perLine == 0 ? "\n    " : " ");
            sb.Append("0x").Append(data[i].ToString("x2"));
            if (i < data.Length - 1)
                sb.Append(',');
        }
        sb.Append(data.Length > 0 ? "\n};" : "};");
        return sb.ToString();
    }
}
