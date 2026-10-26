using System.Diagnostics;
using System.Text.Json;
using UdpTcpListener.Core.Capture;

namespace UdpTcpListener.App.Services;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    public string? Language { get; set; }
    public string? LastDevice { get; set; }
    public ProtocolFilter Protocol { get; set; } = ProtocolFilter.Both;
    public string? SourceHost { get; set; }
    public string? DestinationHost { get; set; }
    public string? Port { get; set; }
    public string? CustomBpf { get; set; }
    public bool AutoScroll { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Packet-list columns the user hid (by column key).</summary>
    public List<string> HiddenColumns { get; set; } = [];

    // Save-to-disk (ring buffer) while capturing.
    public bool RecordToDisk { get; set; }
    public string? RecordFolder { get; set; }
    public int RecordFileSizeMb { get; set; } = 100;
    public int RecordMaxFiles { get; set; } = 10;

    /// <summary>Master switch for alert rules (the rules themselves live in alerts.json).</summary>
    public bool AlertsEnabled { get; set; } = true;
}

/// <summary>Persists <see cref="AppSettings"/> as JSON in the per-user application data folder.</summary>
public static class SettingsService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UdpTcpListener");

    private static string FilePath => Path.Combine(Directory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not read settings, using defaults: {ex.Message}");
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not save settings: {ex.Message}");
        }
    }
}
