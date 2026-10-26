using System.Net;
using System.Text;
using UdpTcpListener.Core.Capture;
using UdpTcpListener.Core.Decoding;
using UdpTcpListener.Core.Export;
using UdpTcpListener.Core.Formatting;
using UdpTcpListener.Core.Packets;
using UdpTcpListener.Core.Sending;

namespace UdpTcpListener.Cli;

/// <summary>The "send" and "replay" subcommands.</summary>
internal static class Commands
{
    private const string SendUsage = """
        Send a payload over UDP or TCP (a normal socket; no admin rights needed).

        Usage:
          udptcplistener send <host:port> [options]

        Options:
          -p, --protocol <p>     udp | tcp (default: udp)
          -t, --text <text>      Payload text; escapes: \n \r \t \0 \\ \xNN (default: empty)
          -x, --hex <hex>        Payload as hex, e.g. "de ad be ef" or 0xDE,0xAD
          -b, --base64 <b64>     Payload as Base64
          -F, --file <path>      Payload from a file
          -c, --count <n>        How many times to send (default 1, 0 = until Ctrl+C)
          -i, --interval <ms>    Pause between sends (default 0)
          -w, --wait <ms>        Wait for a reply after each send and print it

        Examples:
          udptcplistener send 127.0.0.1:9000 -t "hello\n" -c 10 -i 100
          udptcplistener send [::1]:502 -p tcp -x "00 01 00 00 00 06 01 03 00 00 00 02" -w 1000
        """;

    private const string ReplayUsage = """
        Replay the packets of a .pcap/.pcapng file.

        Usage:
          udptcplistener replay <file> [options]

        Where to:
              --to <host:port>     Send each packet's payload to this address over a normal socket.
                                   Without --to, UDP payloads go to their original destinations.
                                   TCP payloads need --to and are sent over one connection.
              --inject <device>    Instead: put the original frames back on this device, byte for
                                   byte (index from --list or name; needs capture rights)

        Which packets (same as capture filters):
          -p, --protocol <p>  --src <ip>  --dst <ip>  --port <n>

        Timing:
              --speed <x>          Original timing, x times faster (default 1)
              --interval <ms>      Fixed pause between packets instead
              --fast               No pauses
              --loops <n>          Repeat the whole file n times (default 1, 0 = until Ctrl+C)

        Example:
          udptcplistener replay dns.pcapng --port 53 --to 127.0.0.1:5353 --speed 10
        """;

    public static int Send(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine(SendUsage);
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            if (!HostPort.TryParse(args[0], out var host, out var port))
                throw new ArgumentException($"'{args[0]}' is not host:port (use [addr]:port for IPv6).");

            var protocol = TransportProtocol.Udp;
            byte[] payload = [];
            int count = 1, intervalMs = 0, waitMs = 0;

            for (var i = 1; i < args.Length; i++)
            {
                var arg = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {arg}.");
                switch (arg)
                {
                    case "-p" or "--protocol":
                        protocol = Next().ToLowerInvariant() switch
                        {
                            "udp" => TransportProtocol.Udp,
                            "tcp" => TransportProtocol.Tcp,
                            var other => throw new ArgumentException($"--protocol must be udp or tcp (got {other})."),
                        };
                        break;
                    case "-t" or "--text": payload = ParsePayload(Next(), PayloadEncoding.Text); break;
                    case "-x" or "--hex": payload = ParsePayload(Next(), PayloadEncoding.Hex); break;
                    case "-b" or "--base64": payload = ParsePayload(Next(), PayloadEncoding.Base64); break;
                    case "-F" or "--file": payload = File.ReadAllBytes(Next()); break;
                    case "-c" or "--count": count = NonNegative(Next(), arg); break;
                    case "-i" or "--interval": intervalMs = NonNegative(Next(), arg); break;
                    case "-w" or "--wait": waitMs = NonNegative(Next(), arg); break;
                    default: throw new ArgumentException($"Unknown option '{arg}'.");
                }
            }

            using var cts = CancelOnCtrlC();
            var options = new SendOptions
            {
                Protocol = protocol,
                Host = host,
                Port = port,
                Payload = payload,
                Count = count,
                Interval = TimeSpan.FromMilliseconds(intervalMs),
                ResponseTimeout = TimeSpan.FromMilliseconds(waitMs),
            };

            Console.Error.WriteLine($"Sending {payload.Length} byte(s) over {protocol.ToString().ToUpperInvariant()} to {host}:{port}" +
                                    (count == 0 ? " until Ctrl+C" : count > 1 ? $" x{count}" : ""));
            var printed = 0;
            var result = Run(() => PacketSender.SendAsync(options, null, cts.Token));
            if (result is null)
                return 130; // cancelled with Ctrl+C
            foreach (var response in result.Responses)
            {
                Console.WriteLine($"<- {response.From}  {response.Data.Length} byte(s)");
                Console.WriteLine(HexDump.Format(response.Data));
                printed++;
            }

            Console.Error.WriteLine($"Sent {result.Sent} packet(s), {result.BytesSent} byte(s) in {result.Elapsed.TotalSeconds:0.###} s" +
                                    (result.Errors > 0 ? $", {result.Errors} error(s)" : "") +
                                    (waitMs > 0 ? $", {printed} repl{(printed == 1 ? "y" : "ies")}" : ""));
            return result.Errors > 0 ? 1 : 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or FormatException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    public static int Replay(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine(ReplayUsage);
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            var path = Path.GetFullPath(args[0]);
            var filter = new CaptureFilter();
            var replay = new ReplayOptions();
            string? to = null, inject = null;

            for (var i = 1; i < args.Length; i++)
            {
                var arg = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {arg}.");
                switch (arg)
                {
                    case "--to": to = Next(); break;
                    case "--inject": inject = Next(); break;
                    case "-p" or "--protocol":
                        filter = filter with { Protocol = CaptureFilter.TryParseProtocol(Next(), out var p) ? p : throw new ArgumentException("--protocol must be udp, tcp or both.") };
                        break;
                    case "--src": filter = filter with { SourceHost = Next() }; break;
                    case "--dst": filter = filter with { DestinationHost = Next() }; break;
                    case "--port": filter = filter with { Port = NonNegative(Next(), arg) }; break;
                    case "--speed":
                        replay = replay with
                        {
                            Timing = ReplayTiming.Original,
                            Speed = double.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0
                                ? s : throw new ArgumentException("--speed must be a positive number."),
                        };
                        break;
                    case "--interval": replay = replay with { Timing = ReplayTiming.FixedInterval, Interval = TimeSpan.FromMilliseconds(NonNegative(Next(), arg)) }; break;
                    case "--fast": replay = replay with { Timing = ReplayTiming.AsFastAsPossible }; break;
                    case "--loops": replay = replay with { Loops = NonNegative(Next(), arg) }; break;
                    default: throw new ArgumentException($"Unknown option '{arg}'.");
                }
            }

            if (to is not null && inject is not null)
                throw new ArgumentException("Use either --to or --inject, not both.");

            var matches = filter.ToPredicate();
            var packets = CaptureFile.Read(path).Where(matches).ToList();
            if (packets.Count == 0)
            {
                Console.Error.WriteLine("No matching packets in the file.");
                return 1;
            }

            using var cts = CancelOnCtrlC();
            var progress = new ConsoleProgress();
            long sent;

            if (inject is not null)
            {
                var devices = PcapCaptureEngine.GetDevices();
                var device = int.TryParse(inject, out var index) && index >= 0 && index < devices.Count
                    ? devices[index]
                    : devices.FirstOrDefault(d => string.Equals(d.Name, inject, StringComparison.OrdinalIgnoreCase))
                      ?? throw new ArgumentException($"No device matches '{inject}'. Use --list to see devices.");
                Console.Error.WriteLine($"Injecting {packets.Count} frame(s) on {device.Description}");
                sent = Run(() => FrameInjector.InjectAsync(device.Name, packets, replay, progress, cts.Token));
            }
            else
            {
                IPEndPoint? target = null;
                if (to is not null)
                {
                    if (!HostPort.TryParse(to, out var host, out var port))
                        throw new ArgumentException($"'{to}' is not host:port.");
                    target = Run(() => Resolve(host, port));
                }
                var withPayload = packets.Count(p => p.Payload.Length > 0);
                Console.Error.WriteLine($"Replaying {withPayload} payload(s) to {(target?.ToString() ?? "their original destinations")}");
                sent = Run(() => UdpTcpListener.Core.Sending.Replay.ReplayPayloadsAsync(packets, target, replay, progress, cts.Token));
            }

            Console.Error.WriteLine($"\nDone: {sent} sent" + (progress.Errors > 0 ? $", {progress.Errors} error(s): {progress.LastError}" : ""));
            return progress.Errors > 0 ? 1 : 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Net.Sockets.SocketException
                                       or InvalidOperationException or CaptureLibraryUnavailableException or SharpPcap.PcapException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    public static DecoderRegistry LoadDecoders(string? directory)
    {
        var result = CustomProtocolLoader.LoadDirectory(directory ?? CustomProtocolLoader.DefaultDirectory);
        foreach (var error in result.Errors)
            Console.Error.WriteLine($"warning: custom protocol {error}");
        return new DecoderRegistry(result.Decoders);
    }

    public static IEnumerable<string> FormatFields(IEnumerable<DecodedField> fields, string indent)
    {
        foreach (var field in fields)
        {
            yield return $"{indent}{field.Name}: {field.Value}";
            if (field.Children is { } children)
            {
                foreach (var line in FormatFields(children, indent + "  "))
                    yield return line;
            }
        }
    }

    private static async Task<IPEndPoint> Resolve(string host, int port) =>
        IPAddress.TryParse(host, out var address)
            ? new IPEndPoint(address, port)
            : new IPEndPoint((await Dns.GetHostAddressesAsync(host)).First(), port);

    private static byte[] ParsePayload(string value, PayloadEncoding encoding) =>
        PayloadParser.TryParse(value, encoding, out var bytes, out var error) ? bytes : throw new ArgumentException(error);

    private static int NonNegative(string value, string option) =>
        int.TryParse(value, out var n) && n >= 0 ? n : throw new ArgumentException($"{option} must be a number ≥ 0.");

    /// <summary>Runs an async operation from the synchronous CLI; Ctrl+C ends it cleanly.</summary>
    private static T Run<T>(Func<Task<T>> operation)
    {
        try
        {
            return operation().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("\nCancelled.");
            return default!;
        }
    }

    private static CancellationTokenSource CancelOnCtrlC()
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        return cts;
    }

    private sealed class ConsoleProgress : IProgress<ReplayProgress>
    {
        public long Errors { get; private set; }
        public string? LastError { get; private set; }

        public void Report(ReplayProgress value)
        {
            Errors = value.Errors;
            LastError = value.LastError;
            if (!Console.IsErrorRedirected)
                Console.Error.Write($"\r{value.Sent}/{value.Total} sent" + (value.Errors > 0 ? $", {value.Errors} error(s)" : ""));
        }
    }
}
