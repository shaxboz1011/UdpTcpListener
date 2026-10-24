using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UdpTcpListener.App.Localization;
using UdpTcpListener.App.Services;

namespace UdpTcpListener.App.ViewModels;

/// <summary>
/// "Setup assistant" card: shows what packet capture needs (driver, admin rights) and
/// fixes each item with one button.
/// </summary>
public sealed partial class SetupViewModel : ViewModelBase
{
    private static Localizer L => Localizer.Instance;

    private readonly Func<string, Task> _openUrl;

    public SetupViewModel(Func<string, Task> openUrl)
    {
        _openUrl = openUrl;
        Localizer.Instance.LanguageChanged += (_, _) => OnPropertyChanged(nameof(DriverMissingText));
    }

    /// <summary>Raised after the driver was installed, so devices can be re-scanned.</summary>
    public event EventHandler? DriverInstalledByUser;

    /// <summary>Called before the app exits to restart (save settings, stop capture).</summary>
    public Action? BeforeRestart { get; set; }

    public string DriverName => DependencyInstaller.DriverName;

    public bool IsElevated { get; } = DependencyInstaller.IsElevated;

    public bool CanInstallDriver => DependencyInstaller.CanInstallDriver;

    public string DriverMissingText => L[OperatingSystem.IsWindows() ? "SetupDriverMissingWindows" : "SetupDriverMissingLinux"];

    public bool CanRestartElevated => !IsElevated && DependencyInstaller.CanRestartElevated;

    /// <summary>Linux: the manual install command, for users who prefer a terminal.</summary>
    public string? ManualCommand => DependencyInstaller.LinuxInstallCommand?.DisplayText;

    public bool HasManualCommand => ManualCommand is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial bool DriverInstalled { get; set; } = true;

    /// <summary>Set when capture failed or found no devices, and admin rights would likely help.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial bool ElevationRecommended { get; set; }

    /// <summary>The driver was just installed but this process loaded the old (missing) library; a restart is needed.</summary>
    [ObservableProperty]
    public partial bool RestartRequired { get; set; }

    public bool IsReady => DriverInstalled && (IsElevated || !ElevationRecommended);

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallDriverCommand), nameof(RestartElevatedCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>Updates the checklist after a device scan; opens the card when something needs attention.</summary>
    public void Report(bool driverInstalled, bool elevationRecommended)
    {
        DriverInstalled = driverInstalled;
        ElevationRecommended = elevationRecommended && !IsElevated;
        if (!IsReady)
            IsOpen = true;
    }

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task InstallDriverAsync()
    {
        IsBusy = true;
        Error = null;
        Progress = 0;
        IsProgressIndeterminate = true;
        Status = OperatingSystem.IsWindows() ? L["SetupDownloadingNpcap"] : L["SetupInstallingLibpcap"];

        var progress = new Progress<double?>(value =>
        {
            IsProgressIndeterminate = value is null;
            if (value is { } v)
            {
                Progress = v * 100;
                if (v >= 1 && OperatingSystem.IsWindows())
                    Status = L["SetupRunningInstaller"];
            }
        });

        try
        {
            var result = await DependencyInstaller.InstallDriverAsync(progress);
            switch (result.Outcome)
            {
                case InstallOutcome.Installed:
                    Status = L["SetupDriverInstalled"];
                    DriverInstalledByUser?.Invoke(this, EventArgs.Empty);
                    // SharpPcap caches a failed native load for the lifetime of the process.
                    RestartRequired = !DriverInstalled;
                    break;
                case InstallOutcome.Cancelled:
                    Status = L["SetupCancelled"];
                    DriverInstalledByUser?.Invoke(this, EventArgs.Empty);
                    break;
                default:
                    Status = null;
                    Error = L.Format("SetupFailed", result.Message);
                    break;
            }
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    [RelayCommand]
    private Task OpenDriverWebsite() => _openUrl(DependencyInstaller.DriverWebsite);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void RestartElevated() => Restart(elevated: true);

    [RelayCommand]
    private void RestartApp() => Restart(elevated: false);

    private bool CanRun() => !IsBusy;

    private void Restart(bool elevated)
    {
        Error = null;
        BeforeRestart?.Invoke();
        if (!DependencyInstaller.TryRestart(elevated))
        {
            Error = L["SetupRestartFailed"];
            return;
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
