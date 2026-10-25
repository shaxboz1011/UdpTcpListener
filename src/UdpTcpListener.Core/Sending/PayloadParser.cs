using System.Globalization;
using System.Text;

namespace UdpTcpListener.Core.Sending;

public enum PayloadEncoding
{
    /// <summary>UTF-8 text with C-style escapes: \n \r \t \0 \\ \xNN.</summary>
    Text,
    /// <summary>Hex bytes; spaces, colons, dashes, commas and 0x prefixes are ignored.</summary>
    Hex,
    Base64,
}

public static class PayloadParser
{
    /// <exception cref="FormatException">The input is not valid for <paramref name="encoding"/>.</exception>
    public static byte[] Parse(string input, PayloadEncoding encoding) => encoding switch
    {
        PayloadEncoding.Text => ParseText(input),
        PayloadEncoding.Hex => ParseHex(input),
        PayloadEncoding.Base64 => Convert.FromBase64String(input.Trim()),
        _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
    };

    public static bool TryParse(string input, PayloadEncoding encoding, out byte[] bytes, out string? error)
    {
        try
        {
            bytes = Parse(input, encoding);
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            bytes = [];
            error = ex.Message;
            return false;
        }
    }

    private static byte[] ParseText(string input)
    {
        var bytes = new List<byte>(input.Length);
        Span<byte> utf8 = stackalloc byte[4];

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c != '\\' || i == input.Length - 1)
            {
                // Encode one code point (surrogate pairs included) as UTF-8.
                var length = char.IsHighSurrogate(c) && i + 1 < input.Length
                    ? Encoding.UTF8.GetBytes(input.AsSpan(i++, 2), utf8)
                    : Encoding.UTF8.GetBytes(input.AsSpan(i, 1), utf8);
                bytes.AddRange(utf8[..length]);
                continue;
            }

            var next = input[++i];
            switch (next)
            {
                case 'n': bytes.Add((byte)'\n'); break;
                case 'r': bytes.Add((byte)'\r'); break;
                case 't': bytes.Add((byte)'\t'); break;
                case '0': bytes.Add(0); break;
                case '\\': bytes.Add((byte)'\\'); break;
                case 'x' when i + 2 < input.Length
                              && byte.TryParse(input.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b):
                    bytes.Add(b);
                    i += 2;
                    break;
                default:
                    throw new FormatException($"Unknown escape sequence '\\{next}' at position {i}. Use \\n \\r \\t \\0 \\\\ or \\xNN.");
            }
        }

        return [.. bytes];
    }

    private static byte[] ParseHex(string input)
    {
        var digits = new StringBuilder(input.Length);
        var text = input.Replace("0x", " ", StringComparison.OrdinalIgnoreCase);
        foreach (var c in text)
        {
            if (char.IsAsciiHexDigit(c))
                digits.Append(c);
            else if (!char.IsWhiteSpace(c) && c is not (':' or '-' or ',' or ';'))
                throw new FormatException($"'{c}' is not a hex digit.");
        }

        if (digits.Length % 2 != 0)
            throw new FormatException("Hex input has an odd number of digits.");
        return Convert.FromHexString(digits.ToString());
    }
}
