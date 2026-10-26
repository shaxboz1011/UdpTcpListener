using System.Net;
using UdpTcpListener.Core.Alerts;
using UdpTcpListener.Core.Decoding;
using UdpTcpListener.Core.Geo;
using UdpTcpListener.Core.Packets;
using UdpTcpListener.Core.Processes;

namespace UdpTcpListener.Cli;

/// <summary>Alerts, process names and GeoIP for the capture commands.</summary>
internal sealed class Monitoring
{
    private readonly AlertEngine? _alerts;
    private readonly AlertNotifier _notifier = new();
    private readonly List<Task> _deliveries = [];
    private readonly ProcessMapper? _processes;
    private readonly GeoDatabase? _geo;

    private Monitoring(AlertEngine? alerts, ProcessMapper? processes, GeoDatabase? geo)
    {
        _alerts = alerts;
        _processes = processes;
        _geo = geo;
        _notifier.DeliveryFailed += (rule, channel, error) => Console.Error.WriteLine($"alert '{rule}': {channel} failed: {error}");
    }

    public long AlertCount { get; private set; }

    /// <exception cref="ArgumentException">The rule file is missing or invalid.</exception>
    public static Monitoring? Create(string? alertsFile, bool processes, bool geo, string? geoDirectory, DecoderRegistry? decoders)
    {
        if (alertsFile is null && !processes && !geo)
            return null;

        AlertEngine? engine = null;
        if (alertsFile is not null)
        {
            if (!File.Exists(alertsFile))
                throw new ArgumentException($"Alert rule file not found: {alertsFile}");
            try
            {
                var registry = decoders ?? DecoderRegistry.Default;
                engine = new AlertEngine(AlertRuleStore.Load(alertsFile), p => registry.Summarize(p));
            }
            catch (FormatException ex)
            {
                throw new ArgumentException(ex.Message);
            }
            Console.Error.WriteLine($"Watching {engine.RuleCount} alert rule(s) from {Path.GetFullPath(alertsFile)}");
        }

        ProcessMapper? mapper = null;
        if (processes)
        {
            mapper = new ProcessMapper();
            if (!mapper.IsSupported)
                Console.Error.WriteLine("note: process names are not available on this platform.");
        }

        GeoDatabase? db = null;
        if (geo)
        {
            var dir = geoDirectory ?? GeoFiles.DefaultDirectory;
            db = GeoDatabase.LoadDirectory(dir);
            if (db.IsEmpty)
                Console.Error.WriteLine($"note: no GeoIP database in {dir}. Run 'udptcplistener geoip update' first.");
        }

        return new Monitoring(engine, mapper, db);
    }

    /// <summary>Extra text for the console line, e.g. " [curl 1234] dst US · AS15169 Google LLC".</summary>
    public string Annotate(PacketRecord packet)
    {
        var text = "";
        if (_processes?.Lookup(packet) is { } process)
            text += $"  [{process}]";
        if (_geo is { IsEmpty: false })
        {
            if (_geo.Lookup(packet.SourceAddress) is { } src) text += $"  src {src}";
            if (_geo.Lookup(packet.DestinationAddress) is { } dst) text += $"  dst {dst}";
        }
        return text;
    }

    public void Evaluate(PacketRecord packet)
    {
        if (_alerts is null)
            return;
        foreach (var alert in _alerts.Evaluate(packet))
        {
            AlertCount++;
            Console.Error.WriteLine($"{(alert.Rule.Sound ? "\a" : "")}ALERT [{alert.Title}] {alert.Time.LocalDateTime:HH:mm:ss} {alert.Message.Replace("\n", " | ")}");
            var delivery = _notifier.SendAsync(alert);
            lock (_deliveries)
            {
                _deliveries.RemoveAll(t => t.IsCompleted);
                _deliveries.Add(delivery);
            }
        }
    }

    /// <summary>Waits (up to the timeout) for webhooks and Telegram messages that are still being sent.</summary>
    public void Flush(TimeSpan timeout)
    {
        Task[] pending;
        lock (_deliveries)
            pending = [.. _deliveries];
        if (pending.Length > 0)
            Task.WaitAll(pending, timeout);
    }

    // ---- geoip subcommand ---------------------------------------------------

    private const string GeoUsage = """
        Usage:
          udptcplistener geoip update [--dir <folder>]   download the free DB-IP Lite country + ASN databases
          udptcplistener geoip <ip> [<ip>...] [--dir <folder>]   look addresses up

        The databases are stored in the app's data folder (shared with the desktop app):
          {0}
        IP geolocation by DB-IP (https://db-ip.com), licensed CC BY 4.0.
        """;

    public static int GeoCommand(string[] args)
    {
        var dir = GeoFiles.DefaultDirectory;
        var rest = new List<string>();
        var help = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--dir" && i + 1 < args.Length)
                dir = args[++i];
            else if (args[i] is "-h" or "--help")
                help = true;
            else
                rest.Add(args[i]);
        }
        if (help || rest.Count == 0)
        {
            Console.WriteLine(string.Format(GeoUsage, GeoFiles.DefaultDirectory));
            return help ? 0 : 2;
        }

        if (rest[0] == "update")
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                var files = GeoFiles.DownloadAsync(dir, http, new Progress<string>(f => Console.Error.WriteLine($"Downloading {f}…"))).GetAwaiter().GetResult();
                var db = GeoDatabase.LoadDirectory(dir);
                Console.Error.WriteLine($"Installed {string.Join(", ", files)} in {dir} ({db.RangeCount:N0} ranges).");
                Console.Error.WriteLine(GeoDatabase.Attribution);
                return 0;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }
        }

        var database = GeoDatabase.LoadDirectory(dir);
        if (database.IsEmpty)
        {
            Console.Error.WriteLine($"error: no GeoIP database in {dir}. Run 'udptcplistener geoip update' first.");
            return 1;
        }
        var exit = 0;
        foreach (var value in rest)
        {
            if (!IPAddress.TryParse(value, out var address))
            {
                Console.Error.WriteLine($"error: '{value}' is not an IP address.");
                exit = 2;
                continue;
            }
            var info = database.Lookup(address);
            Console.WriteLine($"{value,-40} {(info is not null ? info.ToString() : GeoDatabase.IsPublic(address) ? "unknown" : "private / not routable")}");
        }
        return exit;
    }
}
