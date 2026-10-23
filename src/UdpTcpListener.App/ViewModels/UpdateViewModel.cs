using System.Diagnostics;
using System.Reflection;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Velopack;
using Velopack.Sources;

namespace UdpTcpListener.App.ViewModels;

/// <summary>
/// Self-update through Velopack + GitHub Releases. The release workflow publishes packages
/// to GitHub; installed copies check on startup and every few hours, download only the delta
/// and restart into the new version when the user clicks "Update".
/// </summary>
public sealed partial class UpdateViewModel : ViewModelBase
{
    public const string RepositoryUrl = "https://github.com/shaxboz1011/UdpTcpListener";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

    private readonly UpdateManager? _manager;
    private UpdateInfo? _pending;
    private DispatcherTimer? _timer;

    public UpdateViewModel()
    {
        try
        {
            _manager = new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Updater unavailable: {ex.Message}");
        }

        CurrentVersion = _manager?.CurrentVersion?.ToString() ?? AssemblyVersion();
    }

    /// <summary>Called before the app restarts into the new version (save settings, stop capture).</summary>
    public Action? BeforeRestart { get; set; }

    /// <summary>False for development builds that were not installed through Velopack.</summary>
    public bool CanUpdate => _manager?.IsInstalled == true;

    public string CurrentVersion { get; }

    [ObservableProperty]
    public partial bool IsUpdateAvailable { get; set; }

    [ObservableProperty]
    public partial string? NewVersion { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateAndRestartCommand))]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty]
    public partial int Progress { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public void StartPeriodicChecks()
    {
        if (!CanUpdate)
            return;

        _ = CheckAsync();
        _timer = new DispatcherTimer { Interval = CheckInterval };
        _timer.Tick += (_, _) => _ = CheckAsync();
        _timer.Start();
    }

    [RelayCommand]
    public async Task CheckAsync()
    {
        if (_manager is null || !CanUpdate || IsDownloading)
            return;

        try
        {
            var info = await _manager.CheckForUpdatesAsync();
            if (info is null)
                return;

            _pending = info;
            NewVersion = info.TargetFullRelease.Version.ToString();
            IsUpdateAvailable = true;
            Error = null;
        }
        catch (Exception ex)
        {
            // Offline or GitHub rate-limited: try again on the next tick.
            Trace.TraceWarning($"Update check failed: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartUpdate))]
    private async Task UpdateAndRestartAsync()
    {
        if (_manager is null || _pending is null)
            return;

        IsDownloading = true;
        Error = null;
        try
        {
            await _manager.DownloadUpdatesAsync(_pending, p => Dispatcher.UIThread.Post(() => Progress = p));
            BeforeRestart?.Invoke();
            _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            IsDownloading = false;
        }
    }

    private bool CanStartUpdate() => !IsDownloading;

    [RelayCommand]
    private void Dismiss() => IsUpdateAvailable = false;

    private static string AssemblyVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = version.IndexOf('+');
        return plus > 0 ? version[..plus] : version;
    }
}
