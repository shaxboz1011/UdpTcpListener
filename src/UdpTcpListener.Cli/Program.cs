using System.Reflection;
using UdpTcpListener.Core.Capture;
using UdpTcpListener.Core.Export;
using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.Cli;

internal static class Program
{
    private const string Usage = """
        UdpTcpListener CLI - capture UDP/TCP packets from the command line.

        Usage:
          udptcplistener [options]

        Options:
          -l, --list               List capture devices and exit
          -i, --interface <n>      Device index (from --list) or device name
          -p, --protocol <p>       udp | tcp | both (default: both)
              --src <ip>           Only packets from this IP
              --dst <ip>           Only packets to this IP
              --port <n>           Only packets where either side uses this port
          -f, --filter <bpf>       Extra BPF expression, e.g. "len > 100"
          -o, --output <file>      Output file; format from extension: .pcap .csv .json .txt
                                   (default: <timestamp>.txt in the current directory)
          -c, --count <n>          Stop after n packets
          -q, --quiet              Do not print packets to the console
          -v, --version            Show version
          -h, --help               Show this help

        Without --interface the tool asks interactively, like the original version.
        Capturing usually needs administrator/root rights (or CAP_NET_RAW on Linux).
        """;

    private static int Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine("Run with --help for usage.");
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(Usage);
            return 0;
        }

        if (options.ShowVersion)
        {
            Console.WriteLine(typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
            return 0;
        }

        IReadOnlyList<NetworkDevice> devices;
        try
        {
            devices = PcapCaptureEngine.GetDevices();
        }
        catch (CaptureLibraryUnavailableException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }

        if (devices.Count == 0)
        {
            Console.Error.WriteLine("No capture devices found. Try running as administrator/root.");
            return 1;
        }

        if (options.ListDevices)
        {
            PrintDevices(devices);
            return 0;
        }

        try
        {
            return Run(options, devices);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
        catch (Exception ex) when (ex is SharpPcap.PcapException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Run(Options options, IReadOnlyList<NetworkDevice> devices)
    {
        var interactive = options.Interface is null && !Console.IsInputRedirected;
        var device = options.Interface is not null ? ResolveDevice(options.Interface, devices)
            : interactive ? PromptDevice(devices)
            : throw new ArgumentException("--interface is required when input is redirected.");

        var filter = options.Filter;
        if (interactive)
            filter = PromptFilter(filter);

        var bpf = filter.ToBpf(); // validate before opening the output file
        var outputPath = Path.GetFullPath(options.Output ?? $"{DateTime.Now:yyyyMMdd_HHmmss}.txt");

        using var writer = PacketWriter.Create(outputPath);
        if (writer is TextPacketWriter text)
            text.AutoFlush = true;

        using var engine = new PcapCaptureEngine();
        using var done = new ManualResetEventSlim();
        var writerLock = new Lock();
        long written = 0;

        engine.PacketCaptured += (_, packet) =>
        {
            lock (writerLock)
            {
                if (done.IsSet)
                    return;

                writer.Write(packet);
                written++;
                if (!options.Quiet)
                    Console.WriteLine(TextPacketWriter.Format(packet));

                if (options.Count is { } max && written >= max)
                    done.Set();
            }
        };

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // let us flush and close the file
            done.Set();
        };

        engine.Start(device.Name, filter);
        Console.Error.WriteLine($"Listening on {device.Description} [{bpf}]");
        Console.Error.WriteLine($"Saving to {outputPath}");
        Console.Error.WriteLine("Press Ctrl+C to stop.");

        done.Wait();
        engine.Stop();

        lock (writerLock)
        {
            var stats = engine.GetStatistics();
            Console.Error.WriteLine($"\nStopped. {written} packet(s) saved to {outputPath}" +
                                    (stats is { Dropped: > 0 } ? $" ({stats.Dropped} dropped by the kernel)" : ""));
        }

        return 0;
    }

    private static void PrintDevices(IReadOnlyList<NetworkDevice> devices)
    {
        for (var i = 0; i < devices.Count; i++)
            Console.WriteLine($"{i,3}) {devices[i].DisplayName}\n     {devices[i].Name}");
    }

    private static NetworkDevice ResolveDevice(string value, IReadOnlyList<NetworkDevice> devices)
    {
        if (int.TryParse(value, out var index))
        {
            return index >= 0 && index < devices.Count
                ? devices[index]
                : throw new ArgumentException($"Device index {index} is out of range (0-{devices.Count - 1}).");
        }

        return devices.FirstOrDefault(d => string.Equals(d.Name, value, StringComparison.OrdinalIgnoreCase))
               ?? devices.FirstOrDefault(d => d.Description.Contains(value, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"No device matches '{value}'. Use --list to see devices.");
    }

    private static NetworkDevice PromptDevice(IReadOnlyList<NetworkDevice> devices)
    {
        Console.WriteLine("Available devices:");
        PrintDevices(devices);

        while (true)
        {
            Console.Write("Select device index: ");
            var input = Console.ReadLine();
            if (input is null)
                throw new ArgumentException("No device selected.");
            if (int.TryParse(input, out var index) && index >= 0 && index < devices.Count)
                return devices[index];
            Console.WriteLine($"Please enter a number between 0 and {devices.Count - 1}.");
        }
    }

    private static CaptureFilter PromptFilter(CaptureFilter filter)
    {
        filter = filter with { SourceHost = Prompt("Enter Source IP (leave empty = any): ", ValidateIp) };
        filter = filter with { DestinationHost = Prompt("Enter Destination IP (leave empty = any): ", ValidateIp) };

        var protocol = Prompt("Enter Protocol (UDP / TCP / BOTH, empty = BOTH): ",
            v => CaptureFilter.TryParseProtocol(v, out _) ? null : "Please enter UDP, TCP or BOTH.");
        CaptureFilter.TryParseProtocol(protocol, out var parsed);
        return filter with { Protocol = parsed };
    }

    private static string? ValidateIp(string value) =>
        System.Net.IPAddress.TryParse(value, out _) ? null : "Not a valid IPv4/IPv6 address.";

    private static string? Prompt(string question, Func<string, string?> validate)
    {
        while (true)
        {
            Console.Write(question);
            var value = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(value))
                return null;
            if (validate(value) is not { } error)
                return value;
            Console.WriteLine(error);
        }
    }

    private sealed record Options
    {
        public bool ShowHelp { get; private init; }
        public bool ShowVersion { get; private init; }
        public bool ListDevices { get; private init; }
        public bool Quiet { get; private init; }
        public string? Interface { get; private init; }
        public string? Output { get; private init; }
        public long? Count { get; private init; }
        public CaptureFilter Filter { get; private init; } = new();

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {arg}.");

                o = arg switch
                {
                    "-h" or "--help" or "/?" => o with { ShowHelp = true },
                    "-v" or "--version" => o with { ShowVersion = true },
                    "-l" or "--list" => o with { ListDevices = true },
                    "-q" or "--quiet" => o with { Quiet = true },
                    "-i" or "--interface" => o with { Interface = Next() },
                    "-o" or "--output" => o with { Output = Next() },
                    "-c" or "--count" => o with { Count = long.TryParse(Next(), out var c) && c > 0 ? c : throw new ArgumentException("--count must be a positive number.") },
                    "-p" or "--protocol" => o with { Filter = o.Filter with { Protocol = CaptureFilter.TryParseProtocol(Next(), out var p) ? p : throw new ArgumentException("--protocol must be udp, tcp or both.") } },
                    "--src" => o with { Filter = o.Filter with { SourceHost = Next() } },
                    "--dst" => o with { Filter = o.Filter with { DestinationHost = Next() } },
                    "--port" => o with { Filter = o.Filter with { Port = int.TryParse(Next(), out var port) ? port : throw new ArgumentException("--port must be a number.") } },
                    "-f" or "--filter" => o with { Filter = o.Filter with { CustomExpression = Next() } },
                    _ => throw new ArgumentException($"Unknown option '{arg}'."),
                };
            }
            return o;
        }
    }
}
