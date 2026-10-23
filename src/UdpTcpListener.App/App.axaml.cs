using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using UdpTcpListener.App.Localization;
using UdpTcpListener.App.Services;
using UdpTcpListener.App.ViewModels;
using UdpTcpListener.App.Views;

namespace UdpTcpListener.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = SettingsService.Load();
            Localizer.Instance.SetLanguage(settings.Language);

            var viewModel = new MainWindowViewModel(settings);
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.ShutdownRequested += (_, _) =>
            {
                viewModel.StopCapture();
                viewModel.SaveSettings();
                viewModel.Dispose();
            };

            if (settings.CheckForUpdates)
                viewModel.Update.StartPeriodicChecks();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
