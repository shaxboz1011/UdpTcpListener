using CommunityToolkit.Mvvm.ComponentModel;
using UdpTcpListener.Core.Analysis;
using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.App.ViewModels;

/// <summary>One row of the Conversations tab; updated in place so sorting and selection survive refreshes.</summary>
public sealed partial class ConversationRow(ConversationKey key) : ObservableObject
{
    public ConversationKey Key { get; } = key;
    public TransportProtocol Protocol => Key.Protocol;
    public string ProtocolName => Key.Protocol == TransportProtocol.Udp ? "UDP" : "TCP";
    public string EndpointA => Key.EndpointA;
    public string EndpointB => Key.EndpointB;
    public System.Net.IPAddress AddressA => Key.AddressA;
    public System.Net.IPAddress AddressB => Key.AddressB;

    [ObservableProperty] public partial long Packets { get; private set; }
    [ObservableProperty] public partial long Bytes { get; private set; }
    [ObservableProperty] public partial long PacketsAToB { get; private set; }
    [ObservableProperty] public partial long BytesAToB { get; private set; }
    [ObservableProperty] public partial long PacketsBToA { get; private set; }
    [ObservableProperty] public partial long BytesBToA { get; private set; }
    [ObservableProperty] public partial DateTimeOffset Start { get; private set; }
    [ObservableProperty] public partial double DurationSeconds { get; private set; }

    /// <summary>Local process that owns the conversation, when known.</summary>
    [ObservableProperty] public partial string? Process { get; set; }

    public void Update(ConversationSummary summary)
    {
        Packets = summary.Packets;
        Bytes = summary.Bytes;
        PacketsAToB = summary.PacketsAToB;
        BytesAToB = summary.BytesAToB;
        PacketsBToA = summary.PacketsBToA;
        BytesBToA = summary.BytesBToA;
        Start = summary.Start;
        DurationSeconds = summary.Duration.TotalSeconds;
    }
}
