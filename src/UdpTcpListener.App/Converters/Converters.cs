using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using UdpTcpListener.Core.Decoding;
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

    /// <summary>Decoders used by <see cref="PacketInfo"/>; replaced when custom protocols are reloaded.</summary>
    public static DecoderRegistry Decoders { get; set; } = DecoderRegistry.Default;

    /// <summary>Info column: the decoded summary when a decoder recognises the payload (like Wireshark), else ports/flags/length.</summary>
    public static readonly IValueConverter PacketInfo =
        new FuncValueConverter<PacketRecord, string>(p => p is null ? "" : Decoders.Summarize(p) ?? p.Info);

    public static readonly IValueConverter Bytes =
        new FuncValueConverter<long, string>(b => ByteSize.Format(b));

    public static readonly IValueConverter Number =
        new FuncValueConverter<long, string>(n => n.ToString("N0", CultureInfo.CurrentCulture));
}
