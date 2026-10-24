using System.Text.RegularExpressions;

namespace UdpTcpListener.Core.Setup;

/// <summary>
/// Locates the official Npcap installer. Npcap's free licence does not allow bundling it,
/// so the app downloads it from npcap.com on the user's request instead.
/// </summary>
public static partial class NpcapRelease
{
    public const string HomePage = "https://npcap.com/";
    public const string DownloadPage = "https://npcap.com/#download";

    /// <summary>Publisher the downloaded installer must be Authenticode-signed by.</summary>
    public const string ExpectedSigner = "Nmap Software LLC";

    /// <summary>Finds the newest <c>npcap-X.Y.exe</c> linked from the npcap.com page; null if none.</summary>
    public static Version? ParseLatestVersion(string html)
    {
        Version? latest = null;
        foreach (Match match in InstallerLink().Matches(html))
        {
            if (Version.TryParse(match.Groups["v"].Value, out var version) && (latest is null || version > latest))
                latest = version;
        }
        return latest;
    }

    public static Uri InstallerUrl(Version version) => new($"https://npcap.com/dist/npcap-{version}.exe");

    [GeneratedRegex(@"npcap-(?<v>\d+\.\d+(?:\.\d+)?)\.exe", RegexOptions.IgnoreCase)]
    private static partial Regex InstallerLink();
}
