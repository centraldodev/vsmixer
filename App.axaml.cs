using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using VSMixer.Services;
using VSMixer.ViewModels;
using VSMixer.Views;

namespace VSMixer;

public partial class App : Application
{
    private MainWindowViewModel? _mainViewModel;

    public override void Initialize()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                AppLogger.Error("Exceção não tratada", exception);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLogger.Error("Falha em tarefa assíncrona", args.Exception);
            args.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            AppLogger.Error("Falha na interface", args.Exception);
        };
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _mainViewModel = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = _mainViewModel,
            };
            desktop.Exit += (_, _) => _mainViewModel?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
