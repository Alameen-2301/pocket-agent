using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace PocketAgent.Windows;

internal static class Program
{
    private const string MutexName = "PocketAgentWindows.SingleInstance";
    internal const string ShowEventName = "PocketAgentWindows.ShowWindow";

    [STAThread]
    private static int Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool first);
        if (!first)
        {
            // Already running: ask the first instance to show its window.
            try
            {
                using var existing = EventWaitHandle.OpenExisting(ShowEventName);
                existing.Set();
            }
            catch { }
            return 0;
        }

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            CrashLog.Write(e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write(e.Exception);
            e.SetObserved();
        };

        WindowsHost? host = null;
        app.Startup += (_, _) =>
        {
            try
            {
                host = new WindowsHost(app, showEvent, startHidden: args.Contains("--minimized", StringComparer.OrdinalIgnoreCase));
                host.Start();
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);
                MessageBox.Show("Pocket Agent could not start:\n\n" + ex.Message + "\n\nDetails were written to " + CrashLog.FilePath,
                    "Pocket Agent", MessageBoxButton.OK, MessageBoxImage.Error);
                app.Shutdown(1);
            }
        };
        app.Exit += (_, _) => host?.Dispose();
        return app.Run();
    }
}

/// <summary>Writes unexpected errors to %LOCALAPPDATA%\PocketAgentWindows\crash.log.</summary>
internal static class CrashLog
{
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PocketAgentWindows");
    public static string FilePath => Path.Combine(Folder, "crash.log");
    private static readonly object Lock = new();

    public static void Write(Exception? e) => Append("ERROR " + (e?.ToString() ?? "unknown error"));

    public static void Info(string message) => Append("INFO  " + message);

    private static void Append(string text)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Folder);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 2_000_000) info.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
