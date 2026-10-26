using System.Globalization;
using System.Net;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UdpTcpListener.App.Localization;
using UdpTcpListener.Core.Geo;

namespace UdpTcpListener.App.Services;

/// <summary>
/// The offline GeoIP/ASN database (DB-IP Lite): loaded in the background at startup and
/// downloaded or updated with one click from the setup assistant.
/// </summary>
public sealed partial class GeoService : ObservableObject
{
    private static Localizer L => Localizer.Instance;

    /// <summary>Used by the packet-list converter; replaced when a database is loaded.</summary>
    public static GeoService? Current { get; private set; }

    public GeoService(string? directory = null)
    {
        Directory = directory ?? GeoFiles.DefaultDirectory;
        Current = this;
        L.LanguageChanged += (_, _) => RefreshStatus();
        _ = LoadAsync();
    }

    public string Directory { get; }

    public GeoDatabase Database { get; private set; } = GeoDatabase.Empty;

    public string Attribution => GeoDatabase.Attribution;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalled))]
    public partial bool IsLoaded { get; private set; }

    public bool IsInstalled => IsLoaded && !Database.IsEmpty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? Status { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Raised on the UI thread after a (re)load, so views can refresh geo columns.</summary>
    public event EventHandler? Loaded;

    /// <summary>"US · United States · AS15169 Google LLC", or null for private/unknown addresses.</summary>
    public string? Describe(IPAddress address)
    {
        if (Database.Lookup(address) is not { } info)
            return null;
        var country = info.CountryCode is { } cc ? $"{cc} · {CountryName(cc)}" : null;
        var asn = info.Asn is { } n ? (info.Organization is { } org ? $"AS{n} {org}" : $"AS{n}") : null;
        return string.Join(" · ", new[] { country, asn }.Where(s => s is not null));
    }

    private static string CountryName(string code)
    {
        try
        {
            return new RegionInfo(code).EnglishName;
        }
        catch (ArgumentException)
        {
            return code;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        IsBusy = true;
        Error = null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("UdpTcpListener");
            var progress = new Progress<string>(file => Status = L.Format("GeoDownloading", file));
            await GeoFiles.DownloadAsync(Directory, http, progress);
            await LoadAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Error = L.Format("GeoDownloadFailed", ex.Message);
            RefreshStatus();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanDownload() => !IsBusy;

    [RelayCommand]
    private Task OpenWebsite() => Dialogs?.OpenUrlAsync("https://db-ip.com/db/lite.php") ?? Task.CompletedTask;

    [RelayCommand]
    private Task OpenFolder() => Dialogs?.OpenFolderAsync(Directory) ?? Task.CompletedTask;

    public IFileDialogs? Dialogs { get; set; }

    private async Task LoadAsync()
    {
        IsBusy = true;
        Status = L["GeoLoading"];
        GeoDatabase db;
        try
        {
            db = await Task.Run(() => GeoDatabase.LoadDirectory(Directory));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            db = GeoDatabase.Empty;
            Error = L.Format("GeoLoadFailed", ex.Message);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Database = db;
            IsLoaded = true;
            IsBusy = false;
            OnPropertyChanged(nameof(IsInstalled));
            RefreshStatus();
            Loaded?.Invoke(this, EventArgs.Empty);
        });
    }

    private void RefreshStatus()
    {
        if (!IsLoaded || IsBusy)
            return;
        Status = Database.IsEmpty
            ? L["GeoNotInstalled"]
            : L.Format("GeoInstalled", Database.RangeCount.ToString("N0", CultureInfo.CurrentCulture),
                GeoFiles.LastUpdated(Directory)?.LocalDateTime.ToString("d", CultureInfo.CurrentCulture) ?? "?");
    }
}
