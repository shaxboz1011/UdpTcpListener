using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UdpTcpListener.App.Localization;
using UdpTcpListener.App.Services;
using UdpTcpListener.Core.Analysis;
using UdpTcpListener.Core.Formatting;
using UdpTcpListener.Core.Statistics;

namespace UdpTcpListener.App.ViewModels;

public enum StreamViewMode
{
    Text,
    HexDump,
}

public enum StreamDirectionFilter
{
    Both,
    AToB,
    BToA,
}

public sealed record StreamModeOption(StreamViewMode Value, string LabelKey)
{
    public string Label => Localizer.Instance[LabelKey];
}

public sealed record StreamDirectionOption(StreamDirectionFilter Value, string LabelKey)
{
    public string Label => Localizer.Instance[LabelKey];
}

/// <summary>One coloured block in the Follow stream window.</summary>
public sealed record StreamChunkView(bool IsAToB, string Header, string Text);

/// <summary>"Follow stream" window: the reassembled payload of one conversation as text or hex.</summary>
public sealed partial class StreamViewModel : ViewModelBase
{
    /// <summary>Rendering huge streams as text blocks would freeze the UI; show the start and say so.</summary>
    private const int MaxDisplayBytes = 2 * 1024 * 1024;

    private static Localizer L => Localizer.Instance;

    private readonly FollowedStream _stream;
    private readonly IFileDialogs? _dialogs;

    /// <summary>
    /// The conversation key orders endpoints arbitrarily; in this window "first" is whoever sent data
    /// first (usually the client), so it is shown first and in the first colour, like Wireshark.
    /// </summary>
    private readonly StreamDirection _firstDirection;

    public StreamViewModel(FollowedStream stream, IFileDialogs? dialogs)
    {
        _stream = stream;
        _dialogs = dialogs;
        _firstDirection = stream.Chunks.Count > 0 ? stream.Chunks[0].Direction : StreamDirection.AToB;
        SelectedMode = Modes[0];
        SelectedDirection = Directions[0];
    }

    public string ProtocolName => _stream.Key.Protocol == Core.Packets.TransportProtocol.Udp ? "UDP" : "TCP";
    private bool Swapped => _firstDirection == StreamDirection.BToA;

    /// <summary>The endpoint that sent first.</summary>
    public string EndpointA => Swapped ? _stream.Key.EndpointB : _stream.Key.EndpointA;

    public string EndpointB => Swapped ? _stream.Key.EndpointA : _stream.Key.EndpointB;
    public string Title => $"{ProtocolName}  {EndpointA} ⇄ {EndpointB}";
    public string BytesAToB => ByteSize.Format(Swapped ? _stream.BytesBToA : _stream.BytesAToB);
    public string BytesBToA => ByteSize.Format(Swapped ? _stream.BytesAToB : _stream.BytesBToA);
    public bool IsEmpty => _stream.Chunks.Count == 0;

    public IReadOnlyList<StreamModeOption> Modes { get; } =
    [
        new(StreamViewMode.Text, "StreamModeText"),
        new(StreamViewMode.HexDump, "StreamModeHex"),
    ];

    public IReadOnlyList<StreamDirectionOption> Directions { get; } =
    [
        new(StreamDirectionFilter.Both, "StreamBothDirections"),
        new(StreamDirectionFilter.AToB, "StreamAToB"),
        new(StreamDirectionFilter.BToA, "StreamBToA"),
    ];

    [ObservableProperty]
    public partial StreamModeOption SelectedMode { get; set; }

    [ObservableProperty]
    public partial StreamDirectionOption SelectedDirection { get; set; }

    [ObservableProperty]
    public partial bool IsTruncated { get; private set; }

    public ObservableCollection<StreamChunkView> Chunks { get; } = [];

    partial void OnSelectedModeChanged(StreamModeOption value) => Rebuild();

    partial void OnSelectedDirectionChanged(StreamDirectionOption value) => Rebuild();

    [RelayCommand]
    private Task CopyAsync() => _dialogs?.CopyTextAsync(FullText()) ?? Task.CompletedTask;

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_dialogs is null)
            return;
        var path = await _dialogs.SaveTextAsync($"stream_{_stream.Key.PortA}_{_stream.Key.PortB}.txt");
        if (path is not null)
            await File.WriteAllTextAsync(path, FullText());
    }

    /// <summary>True if the chunk was sent by the endpoint shown first (<see cref="EndpointA"/>).</summary>
    private bool FromFirst(StreamChunk chunk) => chunk.Direction == _firstDirection;

    private IEnumerable<StreamChunk> SelectedChunks() => SelectedDirection?.Value switch
    {
        StreamDirectionFilter.AToB => _stream.Chunks.Where(FromFirst),
        StreamDirectionFilter.BToA => _stream.Chunks.Where(c => !FromFirst(c)),
        _ => _stream.Chunks,
    };

    private string Render(byte[] data) => SelectedMode?.Value == StreamViewMode.HexDump
        ? HexDump.Format(data)
        : HexDump.ToReadableText(data);

    private void Rebuild()
    {
        if (SelectedMode is null || SelectedDirection is null)
            return;

        Chunks.Clear();
        var shown = 0;
        IsTruncated = false;
        foreach (var chunk in SelectedChunks())
        {
            var data = chunk.Data;
            if (shown + data.Length > MaxDisplayBytes)
            {
                data = data[..(MaxDisplayBytes - shown)];
                IsTruncated = true;
            }

            var aToB = FromFirst(chunk);
            var header = $"{(aToB ? EndpointA : EndpointB)} → {(aToB ? EndpointB : EndpointA)}  ·  " +
                         $"{chunk.Timestamp:HH:mm:ss.fff}  ·  {L.Format("StreamBytes", chunk.Data.Length)}";
            Chunks.Add(new StreamChunkView(aToB, header, Render(data)));

            shown += data.Length;
            if (IsTruncated)
                break;
        }
    }

    private string FullText()
    {
        var sb = new StringBuilder();
        foreach (var chunk in SelectedChunks())
        {
            var aToB = FromFirst(chunk);
            sb.Append("=== ").Append(aToB ? EndpointA : EndpointB).Append(" → ").Append(aToB ? EndpointB : EndpointA)
              .Append("  ").Append(chunk.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.ffffff")).AppendLine();
            sb.AppendLine(Render(chunk.Data));
        }
        return sb.ToString();
    }
}
