using System.Configuration;
using System.Data;
using System.Windows;

namespace MakroWerkstatt;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        FileLogger.Initialize();
        FileLogger.Info("App.OnStartup", "Application started");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        FileLogger.Info("App.OnExit", "Application stopped");
        FileLogger.Shutdown();
        base.OnExit(e);
    }
}

