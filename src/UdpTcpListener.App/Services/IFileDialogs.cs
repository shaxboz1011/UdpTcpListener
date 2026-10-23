namespace UdpTcpListener.App.Services;

/// <summary>File pickers, implemented by the view so the view model stays UI-framework free.</summary>
public interface IFileDialogs
{
    Task<string?> SaveCaptureAsync(string suggestedName);
    Task<string?> OpenCaptureAsync();
    Task OpenUrlAsync(string url);
}
