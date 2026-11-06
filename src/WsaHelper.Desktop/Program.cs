using System.IO;
using System.Security.Principal;
using System.Windows;

namespace WsaHelper.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--version" }))
        {
            Console.WriteLine(typeof(Program).Assembly.GetName().Version); return;
        }
        var app = new Application();
        app.DispatcherUnhandledException += (_, e) => { MessageBox.Show(e.Exception.Message, "WSA Helper", MessageBoxButton.OK, MessageBoxImage.Error); e.Handled = true; };
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show("Start WSA Helper normally, without ‘Run as administrator’. Setup requests elevation only for Windows features; APKs and WSA belong to your regular Windows account.", "WSA Helper"); return;
        }
        var mutexName = @"Local\WsaHelper-" + identity.User?.Value;
        using var mutex = new Mutex(true, mutexName, out var first);
        if (!first)
        {
            // A durable inbox allows Explorer launches while the manager is already running.
            try
            {
                var paths = args.Where(a => a.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)).Select(Core.Safety.Apk).ToArray();
                if (paths.Length == 0) { MessageBox.Show("WSA Helper is already open. Use its existing window.", "WSA Helper"); return; }
                var inbox = Path.Combine(DesktopInstall.Home, "inbox"); Directory.CreateDirectory(inbox);
                var message = Path.Combine(inbox, Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(message + ".new", System.Text.Json.JsonSerializer.Serialize(paths)); File.Move(message + ".new", message);
            }
            catch (Exception e) { MessageBox.Show(e.Message, "WSA Helper"); }
            return;
        }
        try { app.Run(new MainWindow(args)); }
        catch (Exception e) { MessageBox.Show(e.Message, "WSA Helper", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { mutex.ReleaseMutex(); }
    }
}
