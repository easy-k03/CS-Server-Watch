using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace CsServerWatch;

public partial class App : Application
{
    private Window? _window;

    public static Window? MainAppWindow { get; private set; }

    public App()
    {
        UnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => CrashLog.Write(args.ExceptionObject as Exception);
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        MainAppWindow = _window;
        _window.Activate();
    }

    private static void OnUnhandled(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        CrashLog.Write(args.Exception);
        CrashLog.Show(args.Exception);
    }
}

internal static class CrashLog
{
    public static void Write(Exception? exception)
    {
        try
        {
            var exe = Environment.ProcessPath;
            var directory = string.IsNullOrWhiteSpace(exe)
                ? AppContext.BaseDirectory
                : Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;
            File.WriteAllText(Path.Combine(directory, "crash.log"), exception?.ToString() ?? "Unknown crash");
        }
        catch (Exception)
        {
            // The crash dialog still explains what happened.
        }
    }

    public static void Show(Exception? exception)
    {
        var path = Environment.ProcessPath;
        var directory = string.IsNullOrWhiteSpace(path) ? AppContext.BaseDirectory : Path.GetDirectoryName(path);
        var where = string.IsNullOrWhiteSpace(directory) ? "" : $"\n\nDetails were written to:\n{Path.Combine(directory, "crash.log")}";
        var text = $"The app crashed.{where}\n\n{exception?.Message}";
        MessageBox(IntPtr.Zero, text, "CS Server Watch", 0x00000010);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
}
