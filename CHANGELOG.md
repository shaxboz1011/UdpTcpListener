# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.3.0] - 2026-10-25

### Added
- **Conversations** tab: every UDP/TCP conversation with packets and bytes per direction, start time and duration. Right-click or double-click a row to show only its packets, or to follow its stream.
- **Follow stream** window. TCP is reassembled in sequence order per direction, with retransmissions and overlaps removed, sequence-number wraparound handled, and data after a gap still shown. UDP is shown in arrival order. It offers text or hex dump, a direction filter, Copy and Save. The side that sent first is shown first.
- Packet right-click menu: Follow stream, show only this conversation, and **Copy as** summary line, payload (hex, hex stream, C array, Base64, text) or whole-frame hex dump.
- Column sorting and a **Columns** chooser; hidden columns are remembered.
- **pcapng** export and import. Mixed link types are kept, any byte order and timestamp resolution is read, and `.pcap`/`.pcapng` is detected from the file contents.
- **Save to disk while capturing**: pcapng files rotate by size and only the newest N are kept. The folder, size and count are set from the ⚙ button next to "Save to disk".
- CLI: `.pcapng` output; `-r/--read` to read or convert capture files (with `-p/--src/--dst/--port` filters); `--rotate-size`, `--rotate-minutes` and `--rotate-files` for long captures.

## [0.2.0] - 2026-10-24

### Added
- Setup assistant: installs the capture driver with one click and can restart the app as administrator. On Windows it downloads Npcap from npcap.com, verifies its Authenticode signature and runs the installer. On Linux it installs libpcap through apt, dnf, yum, zypper, pacman or apk, asking for the password with pkexec.
- A "Setup" button in the header, with a green or amber dot showing whether capture is ready.

### Changed
- The "Npcap is missing" error banner is replaced by the setup assistant, which opens by itself when the driver is missing, no devices are found, or a device cannot be opened.

## [0.1.0] - 2026-10-23

### Added
- Cross-platform desktop app (Windows, Linux) built with Avalonia, with light/dark themes and Uzbek, Russian and English UI.
- Live packet list with protocol colours, TCP flags, instant search and auto-scroll.
- Packet details: layer breakdown, hex dump and payload text.
- Live dashboard: packets/sec, bandwidth, activity chart, protocol mix, top hosts and services.
- Export to PCAP, CSV, JSON and text; open classic `.pcap` files.
- Automatic updates through Velopack and GitHub Releases.
- Command-line tool with flags (`--interface`, `--protocol`, `--src`, `--dst`, `--port`, `--filter`, `--output`, `--count`).
- GitHub Actions CI (Windows + Linux) and a release pipeline. A release can be started by pushing a tag or with "Run workflow" in the Actions tab.

### Fixed (compared with the original console tool)
- No more 100% CPU usage while capturing (busy `while (true)` loop removed).
- Invalid input no longer crashes the program.
- Packets on Wi-Fi, loopback, VPN and Linux `any` interfaces are no longer silently dropped.
- An unknown protocol value no longer results in an empty capture.
- IP filtering now happens in the kernel (BPF) instead of after parsing every packet.
- Log files are written to the current directory (or a chosen path) instead of the install folder.
- Malformed packets can no longer crash the capture thread.

[Unreleased]: https://github.com/shaxboz1011/UdpTcpListener/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/shaxboz1011/UdpTcpListener/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/shaxboz1011/UdpTcpListener/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/shaxboz1011/UdpTcpListener/releases/tag/v0.1.0
