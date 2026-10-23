using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using UdpTcpListener.Core.Packets;
using UdpTcpListener.Core.Statistics;

namespace UdpTcpListener.App.Converters;

public static class AppConverters
{
    private static readonly IBrush UdpBrush = new SolidColorBrush(Color.Parse("#14B8A6"));
    private static readonly IBrush TcpBrush = new SolidColorBrush(Color.Parse("#6366F1"));

    /// <summary>Protocol → pill colour (UDP teal, TCP indigo).</summary>
    public static readonly IValueConverter ProtocolBrush =
        new FuncValueConverter<TransportProtocol, IBrush>(p => p == TransportProtocol.Udp ? UdpBrush : TcpBrush);

    public static readonly IValueConverter Bytes =
        new FuncValueConverter<long, string>(b => ByteSize.Format(b));

    public static readonly IValueConverter Number =
        new FuncValueConverter<long, string>(n => n.ToString("N0", CultureInfo.CurrentCulture));
}
