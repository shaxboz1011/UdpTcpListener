using UdpTcpListener.Core.Setup;

namespace UdpTcpListener.Core.Tests;

public class SetupTests
{
    [Fact]
    public void Picks_newest_npcap_installer_from_page()
    {
        const string html = """
            <a href="dist/npcap-1.79.exe">old</a>
            <a href="dist/npcap-1.82.exe">Npcap 1.82 installer</a>
            <a href="dist/npcap-9.99-oem.exe">OEM build (not matched)</a>
            <a href="https://npcap.com/dist/npcap-1.8.exe">1.8 &lt; 1.82</a>
            """;

        Assert.Equal(new Version(1, 82), NpcapRelease.ParseLatestVersion(html));
    }

    [Fact]
    public void Compares_versions_numerically_not_as_text() =>
        Assert.Equal(new Version(1, 100), NpcapRelease.ParseLatestVersion("npcap-1.99.exe npcap-1.100.exe"));

    [Fact]
    public void Returns_null_when_page_has_no_installer() =>
        Assert.Null(NpcapRelease.ParseLatestVersion("<html>maintenance</html>"));

    [Fact]
    public void Builds_official_installer_url() =>
        Assert.Equal("https://npcap.com/dist/npcap-1.82.exe", NpcapRelease.InstallerUrl(new Version(1, 82)).ToString());

    [Theory]
    [InlineData("/usr/bin/apt-get", "sudo apt-get install -y libpcap0.8")]
    [InlineData("/usr/bin/dnf", "sudo dnf install -y libpcap")]
    [InlineData("/usr/bin/pacman", "sudo pacman -S --noconfirm --needed libpcap")]
    [InlineData("/usr/bin/zypper", "sudo zypper --non-interactive install libpcap1")]
    [InlineData("/sbin/apk", "sudo apk add libpcap")]
    public void Detects_package_manager(string present, string expected)
    {
        var command = LinuxPackages.LibpcapInstallCommand(path => path == present);

        Assert.NotNull(command);
        Assert.Equal(present, command.Executable);
        Assert.Equal(expected, command.DisplayText);
    }

    [Fact]
    public void Prefers_dnf_over_yum_shim()
    {
        var command = LinuxPackages.LibpcapInstallCommand(path => path is "/usr/bin/dnf" or "/usr/bin/yum");
        Assert.Equal("/usr/bin/dnf", command?.Executable);
    }

    [Fact]
    public void Returns_null_without_known_package_manager() =>
        Assert.Null(LinuxPackages.LibpcapInstallCommand(_ => false));
}
