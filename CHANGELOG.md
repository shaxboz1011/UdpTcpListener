# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

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
