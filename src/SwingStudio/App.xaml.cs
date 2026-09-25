using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;

namespace SwingStudio;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Start();
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        Log.Info($"SwingStudio {version} starting. {RuntimeInformation.OSDescription}, {RuntimeInformation.FrameworkDescription}, from {AppContext.BaseDirectory}");

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal("Unhandled error on the UI thread.", args.Exception);
            MessageBox.Show(
                "SwingStudio hit an unexpected error and will close.\n\nDetails were written to:\n" + Log.CurrentFile,
                "SwingStudio",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception error)
            {
                Log.Fatal("Unhandled error.", error);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) => Log.Error("A background task failed.", args.Exception);

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info($"SwingStudio closed (exit code {e.ApplicationExitCode}).");
        Log.Stop();
        base.OnExit(e);
    }
}
