# Roadmap

Everything stays free and open source (MIT). Items are roughly in priority order; PRs are welcome for any of them.

## v1.0: Solid foundation ✅ (this release)
- [x] Core library split out of the UI, with unit tests
- [x] Avalonia desktop app: live list, details, hex dump, search, dashboard
- [x] PCAP/CSV/JSON/TXT export and PCAP import
- [x] Uzbek / Russian / English UI, light and dark themes
- [x] Self-update through Velopack + GitHub Releases
- [x] CLI with flags and interactive mode
- [x] CI on Windows + Linux; tag-driven releases

## v1.1: Analysis
- [ ] **Conversations view**: group packets by 5-tuple with totals and duration
- [ ] **Follow TCP/UDP stream**: reassemble a conversation as text/hex
- [ ] Column sorting and a column chooser
- [ ] pcapng read/write
- [ ] Copy packet as hex / C array / Base64
- [ ] Ring-buffer capture to disk with file rotation (long-running captures)

## v1.2: Tools for developers
- [ ] **UDP/TCP sender**: craft and send packets, with repeat and rate options
- [ ] **Replay** captured packets
- [ ] Payload decoders: text, JSON, Modbus/TCP, MQTT, DNS
- [ ] Custom protocol definitions (field offsets and types in a JSON file)

## v1.3: Monitoring
- [ ] Triggers and alerts ("payload contains X", "traffic from IP Y"): desktop notification, sound, webhook, Telegram bot
- [ ] Process mapping: which application owns a port
- [ ] GeoIP / ASN lookup for public addresses

## v2.0: Remote and extensible
- [ ] Headless capture agent (CLI `serve` mode) with a secure WebSocket stream to the desktop app
- [ ] Plugin API for decoders and exporters
- [ ] macOS build
- [ ] Code-signed Windows installer
