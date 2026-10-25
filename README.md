<p align="center">
  <img src="src/UdpTcpListener.App/Assets/logo.png" width="96" alt="UdpTcpListener logo" />
</p>

<h1 align="center">UdpTcpListener</h1>

<p align="center">
  A fast, friendly UDP/TCP packet capture tool for <b>Windows</b> and <b>Linux</b>, with a modern desktop UI, a scriptable CLI and automatic updates.
</p>

<p align="center">
  <a href="https://github.com/shaxboz1011/UdpTcpListener/actions/workflows/ci.yml"><img src="https://github.com/shaxboz1011/UdpTcpListener/actions/workflows/ci.yml/badge.svg" alt="CI" /></a>
  <a href="https://github.com/shaxboz1011/UdpTcpListener/releases/latest"><img src="https://img.shields.io/github/v/release/shaxboz1011/UdpTcpListener?sort=semver" alt="Latest release" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="MIT license" /></a>
  <img src="https://img.shields.io/badge/.NET-10-512BD4" alt=".NET 10" />
</p>

<p align="center">
  <b>English</b> · <a href="README.uz.md">O'zbekcha</a>
</p>

![UdpTcpListener — dark theme](docs/screenshots/main-dark.png)

## Features

- **Live packet list.** It stays smooth with hundreds of thousands of rows. UDP and TCP are colour-coded, and TCP flags (SYN, ACK, PSH, FIN…) are shown.
- **Kernel-level filters.** Filter by protocol, source/destination IP (IPv4 and IPv6) and port. Advanced users can add a raw BPF expression.
- **Instant search** across IPs, ports, protocols, flags and payload text.
- **Packet details.** Each packet is broken down layer by layer (Frame → Ethernet → IP → UDP/TCP), with a hex dump and a payload-as-text view.
- **Live dashboard.** It shows packets/sec, bandwidth, a 60-second activity chart, the protocol mix, top hosts and top services.
- **Conversations.** Every UDP/TCP conversation with packets and bytes per direction and duration. Show only one conversation's packets with one click.
- **Follow stream.** Rebuilds what a TCP or UDP conversation actually said. TCP is put back in order, with retransmissions removed. Shown as text or a hex dump, with each side in its own colour.
- **Copy as** hex, C array, Base64 or text, plus column sorting and a column chooser.
- **Export** to **pcapng** or **pcap** (open them in Wireshark), CSV, JSON or a plain-text log. You can also **open** `.pcapng` and `.pcap` files.
- **Protocol decoders.** DNS, MQTT, Modbus/TCP, JSON and HTTP are decoded automatically, and the result appears in the Info column and the details pane.
- **Your own protocols.** Describe a binary protocol in a small JSON file (field types, scaling, units, enums, bit flags, magic numbers) and it is decoded like a built-in one. See [docs/CUSTOM_PROTOCOLS.md](docs/CUSTOM_PROTOCOLS.md).
- **Send and replay.** Craft a UDP/TCP payload (text with escapes, hex or Base64) and send it once or many times, optionally waiting for replies. "Send again" re-sends a captured packet, and Replay re-sends a set of packets with their original timing (sped up if you like) or injects the original frames onto a network card.
- **Save to disk while capturing**, for long captures. Files rotate by size and only the newest N are kept, so disk use stays bounded.
- **Works on any link layer:** Ethernet, Wi-Fi, loopback, VPN/tun and Linux `any`.
- **One-click setup.** If the capture driver is missing, the built-in setup assistant installs it for you. On Windows it downloads the official Npcap installer and checks its signature; on Linux it installs libpcap with your package manager. It can also restart the app as administrator.
- **Light/dark themes** and **three languages:** O'zbekcha, Русский and English.
- **Self-updating.** Installed copies check GitHub Releases, download only the delta and restart into the new version with one click.
- **CLI** for servers and scripts: `udptcplistener -i eth0 -p udp --port 53 -o dns.pcap`.

![Search and payload view — light theme](docs/screenshots/search-light.png)

![Follow stream](docs/screenshots/follow-stream.png)

![Custom protocol decoded in the details pane](docs/screenshots/decoders.png)

![Replay window](docs/screenshots/replay.png)

## Install

Download the latest release from the **[Releases page](https://github.com/shaxboz1011/UdpTcpListener/releases/latest)**.

| Platform | Desktop app | Command line |
|---|---|---|
| Windows 10/11 (x64) | `UdpTcpListener-win-Setup.exe` | `udptcplistener-cli-*-win-x64.zip` |
| Linux (x64) | `UdpTcpListener.AppImage` | `udptcplistener-cli-*-linux-x64.tar.gz` |

The desktop app updates itself. When a new version is published you'll see a banner with an **Update & restart** button.

### Requirements

Packet capture needs a capture driver and sometimes elevated rights. **The app's setup assistant (the "Setup" button) handles both with one click:**

![Setup assistant](docs/screenshots/setup-assistant.png)

To do it by hand:

- **Windows**: install **[Npcap](https://npcap.com/#download)** and tick *"Install Npcap in WinPcap API-compatible Mode"*. Npcap's licence doesn't allow it to be bundled, so the app shows a download link if it is missing.
- **Linux**: install libpcap (`sudo apt install libpcap0.8`, `sudo dnf install libpcap`). Then either run as root, or give the binary capture rights once:

  ```bash
  # CLI
  sudo setcap cap_net_raw,cap_net_admin=eip ./udptcplistener

  # Desktop AppImage (AppImages cannot keep file capabilities, so run it with sudo)
  sudo -E ./UdpTcpListener.AppImage
  ```

## Command line

```text
udptcplistener [options]

  -l, --list               List capture devices and exit
  -i, --interface <n>      Device index (from --list) or device name
  -p, --protocol <p>       udp | tcp | both (default: both)
      --src <ip>           Only packets from this IP
      --dst <ip>           Only packets to this IP
      --port <n>           Only packets where either side uses this port
  -f, --filter <bpf>       Extra BPF expression, e.g. "len > 100"
  -o, --output <file>      .pcapng .pcap .csv .json or .txt (default: <timestamp>.txt)
  -r, --read <file>        Read a .pcap/.pcapng file instead of capturing (with -o: convert it)
  -c, --count <n>          Stop after n packets
  -q, --quiet              Do not print packets to the console
      --rotate-size <MB>   Long captures: start a new file after this many megabytes
      --rotate-minutes <n> ... or after this many minutes
      --rotate-files <n>   Keep only the newest n files (ring buffer)
  -d, --decode             Also print decoded protocols (DNS, MQTT, Modbus, JSON, custom)

udptcplistener send <host:port> [-p udp|tcp] [-t text | -x hex | -b base64 | -F file]
                    [-c count] [-i interval-ms] [-w wait-for-reply-ms]
udptcplistener replay <file> [--to host:port | --inject device] [--speed x | --interval ms | --fast] [--loops n]
```

Examples:

```bash
# Capture DNS into rotating 100 MB files, keeping the newest 10
udptcplistener -i eth0 -p udp --port 53 --rotate-size 100 --rotate-files 10 -o dns.pcapng

# Convert a capture to CSV, keeping only traffic from one host
udptcplistener -r capture.pcapng --src 192.168.1.10 -o host.csv

# Send a Modbus/TCP request and print the reply
udptcplistener send 192.168.1.50:502 -p tcp -x "00 01 00 00 00 06 01 03 00 00 00 02" -w 1000

# Replay captured DNS queries against a test server, 10x faster
udptcplistener replay dns.pcapng --port 53 --to 127.0.0.1:5353 --speed 10
```

If you run it without `--interface`, it asks for the device and filters interactively, like the original tool.

## Build from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/shaxboz1011/UdpTcpListener.git
cd UdpTcpListener
dotnet test --solution UdpTcpListener.slnx           # unit tests
dotnet run --project src/UdpTcpListener.App          # desktop app
dotnet run --project src/UdpTcpListener.Cli -- -l    # CLI
```

### Project layout

```text
src/
  UdpTcpListener.Core/   capture engine, decoder, BPF filter builder, exporters, statistics (no UI)
  UdpTcpListener.App/    Avalonia desktop app (MVVM, CommunityToolkit.Mvvm, Velopack updates)
  UdpTcpListener.Cli/    command-line tool
tests/
  UdpTcpListener.Core.Tests/
```

## Releasing

Start a release in either of these two ways:

- **From GitHub:** go to **Actions → Release → Run workflow** on `main` and enter a version, e.g. `1.0.0`. The workflow creates the `v1.0.0` tag for you.
- **From git:** `git tag v1.0.0 && git push origin v1.0.0`

GitHub Actions does the rest: it runs the tests, packages Windows and Linux with Velopack, publishes the GitHub Release and attaches the CLI archives. Existing installs then update themselves.

Tags such as `v1.1.0-beta.1` become pre-releases and are not offered to stable users. See [docs/RELEASING.md](docs/RELEASING.md).

## Roadmap

See [docs/ROADMAP.md](docs/ROADMAP.md). Highlights: conversations and "follow stream", a UDP/TCP sender and replay tool, protocol decoders (Modbus, MQTT, custom), alerts and webhooks, and a remote capture agent.

## Contributing

Contributions are welcome, including translations. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Legal

Only capture traffic on networks you own or are authorised to monitor.

## License

[MIT](LICENSE) © shaxboz1011 and contributors
