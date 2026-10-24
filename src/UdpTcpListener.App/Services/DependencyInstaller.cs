using System.ComponentModel;
using System.Diagnostics;
using UdpTcpListener.Core.Setup;

namespace UdpTcpListener.App.Services;

public enum InstallOutcome
{
    Installed,
    Cancelled,
    Failed,
}

public sealed record InstallResult(InstallOutcome Outcome, string? Message = null);

/// <summary>
/// Installs the packet-capture driver with one click and relaunches the app with admin rights.
/// Windows: downloads the official Npcap installer, verifies its signature and runs it elevated.
/// Linux: installs libpcap through the system package manager, asking for the password with pkexec.
/// </summary>
public static class DependencyInstaller
{
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: the user declined the UAC prompt
    private static readonly HttpClient Http = CreateHttpClient();

    public static bool IsElevated => Environment.IsPrivilegedProcess;

    public static string DriverName => OperatingSystem.IsWindows() ? "Npcap" : "libpcap";

    public static string DriverWebsite => OperatingSystem.IsWindows() ? NpcapRelease.DownloadPage : "https://www.tcpdump.org/";

    /// <summary>Linux: the command that installs libpcap, shown so users can also run it themselves.</summary>
    public static PackageCommand? LinuxInstallCommand { get; } = OperatingSystem.IsLinux() ? LinuxPackages.LibpcapInstallCommand() : null;

    private static string? Pkexec { get; } = OperatingSystem.IsLinux() ? LinuxPackages.FindExecutable("pkexec") : null;

    /// <summary>Whether <see cref="InstallDriverAsync"/> can do the whole job on this machine.</summary>
    public static bool CanInstallDriver =>
        OperatingSystem.IsWindows() || (LinuxInstallCommand is not null && (IsElevated || Pkexec is not null));

    public static bool CanRestartElevated => OperatingSystem.IsWindows() || Pkexec is not null;

    /// <param name="progress">Download progress 0..1, or null while the size is unknown or the installer runs.</param>
    public static Task<InstallResult> InstallDriverAsync(IProgress<double?> progress, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
            return InstallNpcapAsync(progress, cancellationToken);
        if (OperatingSystem.IsLinux())
            return InstallLibpcapAsync(progress, cancellationToken);
        return Task.FromResult(new InstallResult(InstallOutcome.Failed, "Unsupported operating system."));
    }

    /// <summary>Starts a new elevated instance. The caller should exit when this returns true.</summary>
    public static bool TryRestart(bool elevated)
    {
        var target = Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage ? appImage : Environment.ProcessPath;
        if (target is null)
            return false;

        try
        {
            if (!elevated)
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = false });
                return true;
            }

            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, Verb = "runas" });
                return true;
            }

            if (Pkexec is null)
                return false;

            // pkexec clears the environment; pass through what a GUI app needs to reach the user's display.
            var psi = new ProcessStartInfo(Pkexec) { UseShellExecute = false };
            psi.ArgumentList.Add("env");
            foreach (var name in new[] { "DISPLAY", "XAUTHORITY", "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR", "APPIMAGE" })
            {
                if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                    psi.ArgumentList.Add($"{name}={value}");
            }
            psi.ArgumentList.Add(target);
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }

    private static async Task<InstallResult> InstallNpcapAsync(IProgress<double?> progress, CancellationToken ct)
    {
        progress.Report(null);
        Version? version;
        try
        {
            version = NpcapRelease.ParseLatestVersion(await Http.GetStringAsync(NpcapRelease.HomePage, ct));
        }
        catch (HttpRequestException ex)
        {
            return new InstallResult(InstallOutcome.Failed, ex.Message);
        }

        if (version is null)
            return new InstallResult(InstallOutcome.Failed, "Could not find the Npcap installer on npcap.com.");

        var directory = Path.Combine(Path.GetTempPath(), "UdpTcpListener");
        Directory.CreateDirectory(directory);
        var installer = Path.Combine(directory, $"npcap-{version}.exe");

        try
        {
            await DownloadAsync(NpcapRelease.InstallerUrl(version), installer, progress, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new InstallResult(InstallOutcome.Failed, ex.Message);
        }

        if (!OperatingSystem.IsWindows() || !Authenticode.IsSignedBy(installer, NpcapRelease.ExpectedSigner))
        {
            TryDelete(installer);
            return new InstallResult(InstallOutcome.Failed, $"The downloaded file is not signed by {NpcapRelease.ExpectedSigner}; it was deleted.");
        }

        progress.Report(null);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true, Verb = "runas" })
                                ?? throw new InvalidOperationException("The Npcap installer did not start.");
            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0
                ? new InstallResult(InstallOutcome.Installed)
                : new InstallResult(InstallOutcome.Cancelled);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new InstallResult(InstallOutcome.Cancelled);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new InstallResult(InstallOutcome.Failed, ex.Message);
        }
        finally
        {
            TryDelete(installer);
        }
    }

    private static async Task<InstallResult> InstallLibpcapAsync(IProgress<double?> progress, CancellationToken ct)
    {
        if (LinuxInstallCommand is not { } command)
            return new InstallResult(InstallOutcome.Failed, "No supported package manager was found.");

        progress.Report(null);
        var psi = IsElevated
            ? new ProcessStartInfo(command.Executable)
            : new ProcessStartInfo(Pkexec ?? throw new InvalidOperationException("pkexec is not available."));
        if (!IsElevated)
            psi.ArgumentList.Add(command.Executable);
        foreach (var argument in command.Arguments)
            psi.ArgumentList.Add(argument);
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.Environment["DEBIAN_FRONTEND"] = "noninteractive";

        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("The package manager did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(stdout, stderr);

            return process.ExitCode switch
            {
                0 => new InstallResult(InstallOutcome.Installed),
                126 or 127 when !IsElevated => new InstallResult(InstallOutcome.Cancelled), // pkexec: dismissed or not authorised
                _ => new InstallResult(InstallOutcome.Failed, LastLine(stderr.Result) ?? $"Exit code {process.ExitCode}"),
            };
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new InstallResult(InstallOutcome.Failed, ex.Message);
        }
    }

    private static async Task DownloadAsync(Uri url, string path, IProgress<double?> progress, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(path);
        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;
            progress.Report(total > 0 ? (double)received / total.Value : null);
        }
    }

    private static string? LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The installer may still be locked; the temp folder is cleaned by the OS eventually.
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UdpTcpListener (+https://github.com/shaxboz1011/UdpTcpListener)");
        return client;
    }
}
