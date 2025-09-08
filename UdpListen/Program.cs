using PacketDotNet;
using PacketDotNet.Ieee80211;
using SharpPcap;
using System;
using System.IO;

class Program
{
    static void Main(string[] args)
    {
        // 1. List devices
        var devices = CaptureDeviceList.Instance;
        if (devices.Count < 1)
        {
            Console.WriteLine("No devices found.");
            return;
        }

        Console.WriteLine("Available devices:");
        for (int i = 0; i < devices.Count; i++)
        {
            Console.WriteLine($"{i}) {devices[i].Description}");
        }

        Console.Write("Select device index: ");
        int devIndex = int.Parse(Console.ReadLine() ?? "0");
        var device = devices[devIndex];

        // 2. User input
        Console.Write("Enter Source IP (leave empty = any): ");
        string? sourceIp = Console.ReadLine();

        Console.Write("Enter Destination IP (leave empty = any): ");
        string? destIp = Console.ReadLine();

        Console.Write("Enter Protocol (UDP / TCP / BOTH): ");
        string? protocolChoice = Console.ReadLine()?.ToUpper() ?? "BOTH";

        // 3. Auto filename with timestamp
        string timestampFile = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string fileName = $"{timestampFile}.txt";
        string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);

        using StreamWriter writer = new(filePath, append: true);

        // 4. Open device
        device.Open(DeviceModes.Promiscuous);

        // ✅ Apply BPF filter
        if (protocolChoice == "UDP")
            device.Filter = "udp";
        else if (protocolChoice == "TCP")
            device.Filter = "tcp";
        else
            device.Filter = "udp or tcp";

        Console.WriteLine($"Listening for {protocolChoice} packets... Saving to {fileName}");
        Console.WriteLine("Press Ctrl+C to stop.\n");

        // 5. Packet handler
        device.OnPacketArrival += (sender, e) =>
        {
            try
            {
                var rawPacket = e.GetPacket();
                var parsedPacket = Packet.ParsePacket(rawPacket.LinkLayerType, rawPacket.Data);
                var ethPacket = parsedPacket.Extract<EthernetPacket>();
                if (ethPacket == null) return;

                var ipPacket = ethPacket.Extract<IPPacket>();
                if (ipPacket == null) return;

                // --- UDP ---
                var udpPacket = ipPacket.Extract<UdpPacket>();
                if (udpPacket != null && (protocolChoice == "UDP" || protocolChoice == "BOTH"))
                {
                    LogPacket(writer, ethPacket, ipPacket, udpPacket.SourcePort, udpPacket.DestinationPort, udpPacket.PayloadData, "UDP", sourceIp, destIp);
                    return;
                }

                // --- TCP ---
                var tcpPacket = ipPacket.Extract<TcpPacket>();
                if (tcpPacket != null && (protocolChoice == "TCP" || protocolChoice == "BOTH"))
                {
                    LogPacket(writer, ethPacket, ipPacket, tcpPacket.SourcePort, tcpPacket.DestinationPort, tcpPacket.PayloadData, "TCP", sourceIp, destIp);
                    return;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing packet: {ex.Message}");
            }
        };

        // 6. Start capture
        device.StartCapture();

        // Stop on Ctrl+C
        Console.CancelKeyPress += (sender, e) =>
        {
            Console.WriteLine("\nStopping capture...");
            device.StopCapture();
            device.Close();
        };

        // Prevent exit
        while (true) { }
    }

    static void LogPacket(StreamWriter writer, EthernetPacket ethPacket, IPPacket ipPacket,
                          int srcPort, int dstPort, byte[]? payload, string proto,
                          string? sourceIp, string? destIp)
    {
        bool matchSource = string.IsNullOrEmpty(sourceIp) || ipPacket.SourceAddress.ToString() == sourceIp;
        bool matchDest = string.IsNullOrEmpty(destIp) || ipPacket.DestinationAddress.ToString() == destIp;

        if (!(matchSource && matchDest)) return;

        string timestamp = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss");

        string srcMac = ethPacket.SourceHardwareAddress.ToString().Replace(":", "");
        string dstMac = ethPacket.DestinationHardwareAddress.ToString().Replace(":", "");

        string src = $"{srcMac} / {ipPacket.SourceAddress}:{srcPort}";
        string dst = $"{dstMac} / {ipPacket.DestinationAddress}:{dstPort}";

        string payloadHex = payload != null ? BitConverter.ToString(payload) : "";

        string log = $"Time: {timestamp}\nProtocol: {proto}\n{src} -> {dst}\nData: {payloadHex}\n";

        Console.WriteLine(log);
        writer.WriteLine(log);
        writer.Flush();
    }
}
