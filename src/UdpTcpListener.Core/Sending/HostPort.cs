namespace UdpTcpListener.Core.Sending;

public static class HostPort
{
    /// <summary>Parses "host:port", "1.2.3.4:53" or "[::1]:53".</summary>
    public static bool TryParse(string? text, out string host, out int port)
    {
        host = "";
        port = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = text.Trim();
        string hostPart, portPart;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf("]:", StringComparison.Ordinal);
            if (close < 0)
                return false;
            hostPart = text[1..close];
            portPart = text[(close + 2)..];
        }
        else
        {
            var colon = text.LastIndexOf(':');
            if (colon <= 0 || text.IndexOf(':') != colon) // a bare IPv6 address needs brackets
                return false;
            hostPart = text[..colon];
            portPart = text[(colon + 1)..];
        }

        if (hostPart.Length == 0 || !int.TryParse(portPart, out port) || port is < 1 or > 65535)
            return false;
        host = hostPart;
        return true;
    }
}
