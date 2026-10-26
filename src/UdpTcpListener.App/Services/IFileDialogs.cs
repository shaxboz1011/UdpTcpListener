using UdpTcpListener.App.ViewModels;

namespace UdpTcpListener.App.Services;

/// <summary>Dialogs, clipboard and windows, implemented by the view so the view models stay UI-framework free.</summary>
public interface IFileDialogs
{
    Task<string?> SaveCaptureAsync(string suggestedName);
    Task<string?> OpenCaptureAsync();
    Task<string?> SaveTextAsync(string suggestedName);
    Task<string?> PickFolderAsync();
    Task OpenUrlAsync(string url);
    Task CopyTextAsync(string text);
    void ShowStream(StreamViewModel stream);
    void ShowSender(SendViewModel sender);
    Task OpenFolderAsync(string path);
    void ShowAlerts(AlertsViewModel alerts);
}
