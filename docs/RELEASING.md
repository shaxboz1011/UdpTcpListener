# Releasing

Releases are fully automated by [`.github/workflows/release.yml`](../.github/workflows/release.yml).

## Steps

1. Make sure `main` is green in CI.
2. Move the `[Unreleased]` notes in `CHANGELOG.md` under a new version heading and commit.
3. Start the release in one of two ways:
   - **GitHub UI:** go to **Actions → Release → Run workflow**, keep the branch on `main` and enter the version without the `v` (e.g. `1.2.0`). The workflow checks that the version is valid and unused, then creates the `v1.2.0` tag on that commit.
   - **git:** `git tag v1.2.0 && git push origin v1.2.0`

That's it. The workflow then:

1. Runs the test suite.
2. For **Windows (win-x64)** and then **Linux (linux-x64)**:
   - publishes the desktop app as self-contained,
   - downloads the previous release so Velopack can build a **delta** package,
   - runs `vpk pack`, which produces `Setup.exe` / `.AppImage`, a full `.nupkg`, a delta `.nupkg` and the release feed (`releases.<channel>.json`),
   - uploads everything to the GitHub Release for the tag,
   - builds the single-file CLI and attaches it as `.zip` / `.tar.gz`.

## How updates reach users

`UpdateViewModel` uses Velopack's `GithubSource` pointing at this repository:

- On start-up and then every 4 hours, it checks the latest **non-pre-release** GitHub Release for the app's channel (`win` or `linux`).
- If a newer version exists, the app shows a banner. **Update & restart** downloads the delta, or the full package if needed, applies it and relaunches.
- Development builds (`dotnet run`) are not "installed", so they never try to update.

## Versioning

- The version comes from the git tag (`v1.2.3` → `1.2.3`). Local builds get a pre-release version from [MinVer](https://github.com/adamralph/minver).
- A tag with a suffix, such as `v1.3.0-beta.1`, is published as a **GitHub pre-release**. Stable users don't receive it.
- Never delete or re-use a published version. Ship a new patch version instead.

## Testing packaging locally

```bash
dotnet tool install -g vpk --version 1.2.161
dotnet publish src/UdpTcpListener.App -c Release -r linux-x64 --self-contained -o publish -p:MinVerVersionOverride=0.0.1
vpk pack --packId UdpTcpListener --packVersion 0.0.1 --packDir publish --mainExe UdpTcpListener --outputDir Releases
```

On Linux, `vpk pack` needs `squashfs-tools`. Windows packages are best built on Windows.

## Code signing (optional, recommended later)

Unsigned Windows installers trigger SmartScreen warnings. Once you have a certificate, or use a service such as Azure Trusted Signing or SignPath (free for OSS), add `--signParams` / `--azureTrustedSignFile` to the `vpk pack` step.
