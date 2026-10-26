using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.Core.Processes;

/// <summary>One socket from the operating system's socket table.</summary>
public sealed record SocketEntry(
    TransportProtocol Protocol,
    IPAddress LocalAddress,
    ushort LocalPort,
    IPAddress RemoteAddress,
    ushort RemotePort,
    int ProcessId,
    long Inode = 0);

public sealed record ProcessInfo(int Id, string Name)
{
    public override string ToString() => $"{Name} ({Id})";
}

/// <summary>Reads the sockets of this machine together with the process that owns each one.</summary>
public interface ISocketTable
{
    IReadOnlyList<SocketEntry> Read();
}

/// <summary>
/// Answers "which application sent or received this packet?" by matching the packet's endpoints
/// against the local socket table. The table is re-read at most every <see cref="RefreshInterval"/>,
/// and a miss triggers an early refresh (new connections appear constantly). Only the first read blocks;
/// later ones run in the background so a slow read (many processes) never stalls the capture thread.
/// Thread-safe.
/// </summary>
public sealed class ProcessMapper
{
    private readonly ISocketTable _table;
    private readonly Func<int, string?> _processName;
    private readonly Lock _lock = new();
    private readonly Dictionary<int, string> _names = [];
    private Dictionary<(TransportProtocol, ushort), List<SocketEntry>> _byPort = [];
    private long? _lastRefresh;
    private long? _lastMissRefresh;
    private bool _refreshing;

    public ProcessMapper(ISocketTable? table = null, Func<int, string?>? processName = null)
    {
        _table = table ?? SocketTables.ForCurrentPlatform();
        _processName = processName ?? DefaultProcessName;
    }

    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Minimum gap between refreshes caused by unknown ports, so unmatched traffic cannot cause a refresh storm.</summary>
    public TimeSpan MissRefreshInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Re-read the table on a background thread (default). When false every refresh happens inside <see cref="Lookup(PacketRecord)"/>.</summary>
    public bool BackgroundRefresh { get; init; } = true;

    /// <summary>False when this platform has no socket table support (e.g. macOS for now).</summary>
    public bool IsSupported => _table is not NullSocketTable;

    public ProcessInfo? Lookup(PacketRecord packet) =>
        Lookup(packet.Protocol, packet.SourceAddress, packet.SourcePort, packet.DestinationAddress, packet.DestinationPort);

    public ProcessInfo? Lookup(TransportProtocol protocol, IPAddress source, ushort sourcePort, IPAddress destination, ushort destinationPort)
    {
        SocketEntry? entry;
        lock (_lock)
        {
            var now = Stopwatch.GetTimestamp();
            if (_lastRefresh is null)
                RefreshNow(now); // first use: there is nothing to answer from yet
            else if (Older(_lastRefresh, now, RefreshInterval))
                RequestRefresh(now);

            entry = Find(protocol, source, sourcePort, destination, destinationPort);
            if (entry is null && Older(_lastMissRefresh, now, MissRefreshInterval) && Older(_lastRefresh, now, MissRefreshInterval))
            {
                _lastMissRefresh = now;
                RequestRefresh(now);
                entry = Find(protocol, source, sourcePort, destination, destinationPort);
            }
            if (entry is null || entry.ProcessId <= 0)
                return null;

            if (!_names.TryGetValue(entry.ProcessId, out var name))
            {
                name = _processName(entry.ProcessId) ?? "?";
                _names[entry.ProcessId] = name;
            }
            return new ProcessInfo(entry.ProcessId, name);
        }
    }

    private static bool Older(long? since, long now, TimeSpan age) => since is not { } s || Stopwatch.GetElapsedTime(s, now) >= age;

    /// <summary>Called under the lock.</summary>
    private void RequestRefresh(long now)
    {
        if (!BackgroundRefresh)
        {
            RefreshNow(now);
            return;
        }
        if (_refreshing)
            return;
        _refreshing = true;
        _lastRefresh = now;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            var table = ReadTable();
            lock (_lock)
            {
                _refreshing = false;
                Apply(table);
            }
        }, null);
    }

    /// <summary>Called under the lock.</summary>
    private void RefreshNow(long now)
    {
        _lastRefresh = now;
        Apply(ReadTable());
    }

    private Dictionary<(TransportProtocol, ushort), List<SocketEntry>>? ReadTable()
    {
        IReadOnlyList<SocketEntry> entries;
        try
        {
            entries = _table.Read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }

        var byPort = new Dictionary<(TransportProtocol, ushort), List<SocketEntry>>();
        foreach (var e in entries)
        {
            if (!byPort.TryGetValue((e.Protocol, e.LocalPort), out var list))
                byPort[(e.Protocol, e.LocalPort)] = list = [];
            list.Add(e);
        }
        return byPort;
    }

    /// <summary>Called under the lock.</summary>
    private void Apply(Dictionary<(TransportProtocol, ushort), List<SocketEntry>>? byPort)
    {
        if (byPort is null)
            return;
        _byPort = byPort;
        // Process ids get reused, so forget names now and then.
        if (_names.Count > 4096)
            _names.Clear();
    }

    private SocketEntry? Find(TransportProtocol protocol, IPAddress src, ushort srcPort, IPAddress dst, ushort dstPort)
    {
        SocketEntry? best = null;
        var bestScore = 0;
        Consider(src, srcPort, dst, dstPort);  // we are the sender
        Consider(dst, dstPort, src, srcPort);  // we are the receiver
        return best;

        void Consider(IPAddress local, ushort localPort, IPAddress remote, ushort remotePort)
        {
            if (!_byPort.TryGetValue((protocol, localPort), out var candidates))
                return;
            foreach (var e in candidates)
            {
                var score = Score(e, local, remote, remotePort);
                if (score > bestScore)
                {
                    best = e;
                    bestScore = score;
                }
            }
        }
    }

    /// <summary>0 = no match; higher is a more specific match (connected socket beats bound beats wildcard).</summary>
    internal static int Score(SocketEntry e, IPAddress local, IPAddress remote, ushort remotePort)
    {
        int score;
        if (Same(e.LocalAddress, local))
            score = 2;
        else if (IsAny(e.LocalAddress) && (e.LocalAddress.AddressFamily == local.AddressFamily || e.LocalAddress.Equals(IPAddress.IPv6Any)))
            score = 1;
        else
            return 0;

        if (e.RemotePort != 0 || !IsAny(e.RemoteAddress))
        {
            // Connected socket: the remote end must match too.
            if (e.RemotePort != remotePort || !Same(e.RemoteAddress, remote))
                return 0;
            score += 4;
        }
        return score;
    }

    private static bool IsAny(IPAddress a) => a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any);

    private static bool Same(IPAddress a, IPAddress b) => Normalize(a).Equals(Normalize(b));

    internal static IPAddress Normalize(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

    private static string? DefaultProcessName(int pid)
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists($"/proc/{pid}/comm"))
                return File.ReadAllText($"/proc/{pid}/comm").Trim();
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

public static class SocketTables
{
    public static ISocketTable ForCurrentPlatform() =>
        OperatingSystem.IsLinux() ? new LinuxSocketTable()
        : OperatingSystem.IsWindows() ? new WindowsSocketTable()
        : new NullSocketTable();
}

internal sealed class NullSocketTable : ISocketTable
{
    public IReadOnlyList<SocketEntry> Read() => [];
}

/// <summary>
/// Linux: /proc/net/{tcp,tcp6,udp,udp6} list sockets by inode; /proc/[pid]/fd links ("socket:[inode]") tell which process owns them.
/// Other users' processes are only visible to root, which capture usually runs as anyway.
/// </summary>
public sealed class LinuxSocketTable(string procRoot = "/proc") : ISocketTable
{
    public IReadOnlyList<SocketEntry> Read()
    {
        var sockets = new List<SocketEntry>();
        foreach (var (file, protocol) in new[] { ("tcp", TransportProtocol.Tcp), ("tcp6", TransportProtocol.Tcp), ("udp", TransportProtocol.Udp), ("udp6", TransportProtocol.Udp) })
        {
            var path = Path.Combine(procRoot, "net", file);
            if (!File.Exists(path))
                continue;
            try
            {
                sockets.AddRange(ParseProcNet(File.ReadAllText(path), protocol));
            }
            catch (IOException)
            {
            }
        }

        var owners = ReadInodeOwners();
        for (var i = 0; i < sockets.Count; i++)
        {
            if (owners.TryGetValue(sockets[i].Inode, out var pid))
                sockets[i] = sockets[i] with { ProcessId = pid };
        }
        return sockets;
    }

    /// <summary>Parses one /proc/net/{tcp,udp}[6] file. Process ids are left at 0.</summary>
    public static List<SocketEntry> ParseProcNet(string text, TransportProtocol protocol)
    {
        var result = new List<SocketEntry>();
        foreach (var rawLine in text.Split('\n'))
        {
            var parts = rawLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // sl local rem st tx:rx tr:when retrnsmt uid timeout inode ...
            if (parts.Length < 10 || !parts[0].EndsWith(':'))
                continue;
            if (!TryParseEndpoint(parts[1], out var local, out var localPort) ||
                !TryParseEndpoint(parts[2], out var remote, out var remotePort) ||
                !long.TryParse(parts[9], NumberStyles.None, CultureInfo.InvariantCulture, out var inode))
                continue;
            result.Add(new SocketEntry(protocol, local, localPort, remote, remotePort, 0, inode));
        }
        return result;
    }

    /// <summary>"0100007F:0035" → 127.0.0.1:53. Addresses are 32-bit words in host (little-endian) order.</summary>
    internal static bool TryParseEndpoint(string text, out IPAddress address, out ushort port)
    {
        address = IPAddress.None;
        port = 0;
        var colon = text.IndexOf(':');
        if (colon is not (8 or 32) ||
            !ushort.TryParse(text.AsSpan(colon + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out port))
            return false;

        var bytes = new byte[colon / 2];
        for (var word = 0; word < colon / 8; word++)
        {
            if (!uint.TryParse(text.AsSpan(word * 8, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                return false;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(word * 4), value);
        }
        address = ProcessMapper.Normalize(new IPAddress(bytes));
        return true;
    }

    private Dictionary<long, int> ReadInodeOwners()
    {
        var owners = new Dictionary<long, int>();
        IEnumerable<string> processes;
        try
        {
            processes = Directory.EnumerateDirectories(procRoot);
        }
        catch (IOException)
        {
            return owners;
        }

        foreach (var dir in processes)
        {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                continue;
            try
            {
                foreach (var fd in Directory.EnumerateFileSystemEntries(Path.Combine(dir, "fd")))
                {
                    var target = new FileInfo(fd).LinkTarget;
                    if (target is not null && TryParseSocketLink(target, out var inode))
                        owners.TryAdd(inode, pid);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Process exited or belongs to another user.
            }
        }
        return owners;
    }

    internal static bool TryParseSocketLink(string target, out long inode)
    {
        inode = 0;
        return target.StartsWith("socket:[", StringComparison.Ordinal) && target.EndsWith(']') &&
               long.TryParse(target.AsSpan(8, target.Length - 9), NumberStyles.None, CultureInfo.InvariantCulture, out inode);
    }
}

/// <summary>Windows: GetExtendedTcpTable / GetExtendedUdpTable from iphlpapi.dll, which include the owning process id.</summary>
public sealed class WindowsSocketTable : ISocketTable
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;
    private const uint ErrorInsufficientBuffer = 122;

    public IReadOnlyList<SocketEntry> Read()
    {
        var result = new List<SocketEntry>();
        if (!OperatingSystem.IsWindows())
            return result;
        if (Query(isTcp: true, AfInet) is { } t4) result.AddRange(ParseTcp4(t4));
        if (Query(isTcp: true, AfInet6) is { } t6) result.AddRange(ParseTcp6(t6));
        if (Query(isTcp: false, AfInet) is { } u4) result.AddRange(ParseUdp4(u4));
        if (Query(isTcp: false, AfInet6) is { } u6) result.AddRange(ParseUdp6(u6));
        return result;
    }

    private static byte[]? Query(bool isTcp, int family)
    {
        var size = 0;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var buffer = size == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(size);
            try
            {
                var rc = isTcp
                    ? GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidAll, 0)
                    : GetExtendedUdpTable(buffer, ref size, false, family, UdpTableOwnerPid, 0);
                if (rc == 0 && buffer != IntPtr.Zero)
                {
                    var bytes = new byte[size];
                    Marshal.Copy(buffer, bytes, 0, size);
                    return bytes;
                }
                if (rc != ErrorInsufficientBuffer && !(rc == 0 && buffer == IntPtr.Zero))
                    return null;
                size += 4096; // the table can grow between the two calls
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(buffer);
            }
        }
        return null;
    }

    // Row layouts (all fields 32-bit, ports in network byte order in the low 16 bits):
    // MIB_TCPROW_OWNER_PID   state, localAddr, localPort, remoteAddr, remotePort, pid          = 24 bytes
    // MIB_TCP6ROW_OWNER_PID  localAddr[16], scope, localPort, remoteAddr[16], scope, remotePort, state, pid = 56
    // MIB_UDPROW_OWNER_PID   localAddr, localPort, pid                                         = 12
    // MIB_UDP6ROW_OWNER_PID  localAddr[16], scope, localPort, pid                              = 28
    // The remote address/port of a listening socket are undefined (may hold garbage), so they are cleared.
    internal static IEnumerable<SocketEntry> ParseTcp4(byte[] table) =>
        Rows(table, 24, r => IsListening(r) ? new SocketEntry(TransportProtocol.Tcp, V4(r[4..]), Port(r[8..]), IPAddress.Any, 0, Pid(r[20..]))
            : new SocketEntry(TransportProtocol.Tcp, V4(r[4..]), Port(r[8..]), V4(r[12..]), Port(r[16..]), Pid(r[20..])));

    internal static IEnumerable<SocketEntry> ParseTcp6(byte[] table) =>
        Rows(table, 56, r => IsListening(r[48..]) ? new SocketEntry(TransportProtocol.Tcp, V6(r[..16]), Port(r[20..]), IPAddress.IPv6Any, 0, Pid(r[52..]))
            : new SocketEntry(TransportProtocol.Tcp, V6(r[..16]), Port(r[20..]), V6(r[24..40]), Port(r[44..]), Pid(r[52..])));

    private const uint MibTcpStateListen = 2;

    private static bool IsListening(ReadOnlySpan<byte> state) => BinaryPrimitives.ReadUInt32LittleEndian(state) == MibTcpStateListen;

    internal static IEnumerable<SocketEntry> ParseUdp4(byte[] table) =>
        Rows(table, 12, r => new SocketEntry(TransportProtocol.Udp, V4(r), Port(r[4..]), IPAddress.Any, 0, Pid(r[8..])));

    internal static IEnumerable<SocketEntry> ParseUdp6(byte[] table) =>
        Rows(table, 28, r => new SocketEntry(TransportProtocol.Udp, V6(r[..16]), Port(r[20..]), IPAddress.IPv6Any, 0, Pid(r[24..])));

    private delegate SocketEntry RowParser(ReadOnlySpan<byte> row);

    private static List<SocketEntry> Rows(byte[] table, int rowSize, RowParser parse)
    {
        var list = new List<SocketEntry>();
        if (table.Length < 4)
            return list;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        for (long i = 0; i < count && 4 + (i + 1) * rowSize <= table.Length; i++)
            list.Add(parse(table.AsSpan((int)(4 + i * rowSize), rowSize)));
        return list;
    }

    private static IPAddress V4(ReadOnlySpan<byte> b) => new(b[..4]);
    private static IPAddress V6(ReadOnlySpan<byte> b) => ProcessMapper.Normalize(new IPAddress(b[..16]));
    private static ushort Port(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt16BigEndian(b);
    private static int Pid(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadInt32LittleEndian(b);

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
}
