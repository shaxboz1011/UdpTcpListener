namespace UdpTcpListener.Core.Setup;

/// <summary>A package-manager command, e.g. <c>apt-get install -y libpcap0.8</c>.</summary>
public sealed record PackageCommand(string Executable, IReadOnlyList<string> Arguments)
{
    /// <summary>What a user would type in a terminal.</summary>
    public string DisplayText => $"sudo {Path.GetFileName(Executable)} {string.Join(' ', Arguments)}";
}

public static class LinuxPackages
{
    private static readonly string[] SearchDirectories = ["/usr/bin", "/bin", "/usr/sbin", "/sbin", "/usr/local/bin"];

    // Order matters: dnf before yum (yum is often a dnf shim), apt-get before anything Debian-ish.
    private static readonly (string Tool, string[] Arguments)[] LibpcapCommands =
    [
        ("apt-get", ["install", "-y", "libpcap0.8"]),
        ("dnf", ["install", "-y", "libpcap"]),
        ("yum", ["install", "-y", "libpcap"]),
        ("zypper", ["--non-interactive", "install", "libpcap1"]),
        ("pacman", ["-S", "--noconfirm", "--needed", "libpcap"]),
        ("apk", ["add", "libpcap"]),
    ];

    /// <summary>The command that installs libpcap with the first package manager found, or null.</summary>
    public static PackageCommand? LibpcapInstallCommand(Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        foreach (var (tool, arguments) in LibpcapCommands)
        {
            if (FindExecutable(tool, fileExists) is { } path)
                return new PackageCommand(path, arguments);
        }
        return null;
    }

    /// <summary>Absolute path of <paramref name="name"/> in the usual system directories, or null.</summary>
    public static string? FindExecutable(string name, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        return SearchDirectories.Select(dir => Path.Combine(dir, name)).FirstOrDefault(fileExists);
    }
}
