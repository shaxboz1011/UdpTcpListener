using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.Core.Analysis;

public enum StreamDirection
{
    AToB,
    BToA,
}

/// <summary>A run of consecutive payload bytes sent in one direction.</summary>
public sealed record StreamChunk(StreamDirection Direction, DateTimeOffset Timestamp, byte[] Data);

public sealed record FollowedStream(ConversationKey Key, IReadOnlyList<StreamChunk> Chunks)
{
    public long BytesAToB => Chunks.Where(c => c.Direction == StreamDirection.AToB).Sum(c => (long)c.Data.Length);
    public long BytesBToA => Chunks.Where(c => c.Direction == StreamDirection.BToA).Sum(c => (long)c.Data.Length);
}

/// <summary>
/// "Follow stream": rebuilds the application data of one conversation.
/// TCP segments are put back in sequence order per direction, with retransmissions and
/// overlaps removed; UDP datagrams are used as they arrived.
/// Consecutive data in the same direction is merged into one chunk.
/// </summary>
public static class StreamReassembler
{
    public static FollowedStream Follow(IEnumerable<PacketRecord> packets, ConversationKey key)
    {
        var chunks = new List<ChunkBuilder>();
        var states = new[] { new DirectionState(), new DirectionState() };

        foreach (var packet in packets)
        {
            if (!key.Matches(packet))
                continue;

            var direction = key.IsAToB(packet) ? StreamDirection.AToB : StreamDirection.BToA;

            if (packet.Protocol == TransportProtocol.Udp || packet.TcpSequence is null)
            {
                Append(chunks, direction, packet.Timestamp, packet.Payload);
                continue;
            }

            var state = states[(int)direction];
            var seq = packet.TcpSequence.Value;

            if (packet.TcpSyn)
                state.Next ??= unchecked(seq + 1);

            if (packet.Payload.Length == 0)
                continue;

            if (packet.TcpSyn)
                seq = unchecked(seq + 1); // data on a SYN starts after the SYN's sequence number

            state.Next ??= seq;
            state.Pending.Add(new Segment(seq, packet.Payload, packet.Timestamp));
            Drain(state, chunks, direction);
        }

        // Anything still pending sits behind a gap (lost packets): show it in sequence order anyway.
        for (var d = 0; d < 2; d++)
        {
            var state = states[d];
            while (state.Pending.Count > 0)
            {
                var first = state.Pending.MinBy(s => (int)unchecked(s.Sequence - state.Next!.Value))!;
                state.Next = first.Sequence;
                Drain(state, chunks, (StreamDirection)d);
            }
        }

        return new FollowedStream(key, chunks.Select(c => new StreamChunk(c.Direction, c.Timestamp, c.Data.ToArray())).ToList());
    }

    private static void Drain(DirectionState state, List<ChunkBuilder> chunks, StreamDirection direction)
    {
        var progressed = true;
        while (progressed)
        {
            progressed = false;
            for (var i = 0; i < state.Pending.Count; i++)
            {
                var segment = state.Pending[i];
                var offset = (int)unchecked(state.Next!.Value - segment.Sequence); // bytes already delivered
                if (offset < 0)
                    continue; // still a hole before this segment

                state.Pending.RemoveAt(i);
                progressed = true;
                if (offset >= segment.Data.Length)
                    break; // retransmission of data we already have

                var fresh = segment.Data.AsSpan(offset).ToArray();
                Append(chunks, direction, segment.Timestamp, fresh);
                state.Next = unchecked(state.Next.Value + (uint)fresh.Length);
                break;
            }
        }
    }

    private static void Append(List<ChunkBuilder> chunks, StreamDirection direction, DateTimeOffset timestamp, byte[] data)
    {
        if (data.Length == 0)
            return;

        if (chunks.Count == 0 || chunks[^1].Direction != direction)
            chunks.Add(new ChunkBuilder(direction, timestamp));
        chunks[^1].Data.AddRange(data);
    }

    private sealed record ChunkBuilder(StreamDirection Direction, DateTimeOffset Timestamp)
    {
        public List<byte> Data { get; } = [];
    }

    private sealed record Segment(uint Sequence, byte[] Data, DateTimeOffset Timestamp);

    private sealed class DirectionState
    {
        public uint? Next;
        public readonly List<Segment> Pending = [];
    }
}
