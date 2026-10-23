# Contributing to UdpTcpListener

Thanks for helping! Bug reports, features, docs and translations are all welcome.

## Getting started

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) and a capture library:
   Npcap on Windows, `libpcap` on Linux.
2. Fork and clone the repository, then run:

   ```bash
   dotnet build UdpTcpListener.slnx
   dotnet test --solution UdpTcpListener.slnx
   ```

3. Run the app with `dotnet run --project src/UdpTcpListener.App`. Live capture needs admin/root rights; see the README.

## Where things live

| Area | Location |
|---|---|
| Packet decoding, BPF filters, export/import, statistics | `src/UdpTcpListener.Core` |
| Desktop UI (Avalonia, MVVM) | `src/UdpTcpListener.App` |
| Translations | `src/UdpTcpListener.App/Assets/i18n/*.json` |
| Self-update (Velopack) | `src/UdpTcpListener.App/ViewModels/UpdateViewModel.cs` |
| Command-line tool | `src/UdpTcpListener.Cli` |
| Tests | `tests/UdpTcpListener.Core.Tests` |

Keep logic in **Core** and covered by tests. The App and CLI should stay thin.

## Adding a translation

1. Copy `src/UdpTcpListener.App/Assets/i18n/en.json` to `<code>.json`, for example `kk.json`, and translate the values. Keep `{0}` placeholders.
2. Add the language to `Localizer.Languages` in `src/UdpTcpListener.App/Localization/Localizer.cs`.
3. Any key you leave out falls back to English.

## Pull requests

- Keep each PR focused on one change, and add or adjust tests for Core changes.
- Make sure `dotnet build` (warnings are errors) and `dotnet test` pass. CI runs both on Windows and Linux.
- For UI changes, add a screenshot to the PR description.
- Use clear commit messages, e.g. `Add CSV export of selected packets`.

## Reporting bugs

Open an issue with your OS, the app version (shown in the header), the steps to reproduce, and what you expected. Don't attach captures that contain private traffic.
