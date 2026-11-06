using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WsaHelper.Core;

namespace WsaHelper.Desktop;

internal sealed partial class MainWindow : Window
{
    private readonly StateStore store;
    private readonly WindowsBridge windows;
    private readonly ProcessRunner runner = new();
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(45) };
    private InstallState state;
    private readonly ObservableCollection<AndroidApp> apps = [];
    private readonly IProgress<string> progress;
    private readonly DispatcherTimer inboxTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Queue<string> queue = new();
    private readonly List<Button> controls = [];
    private CancellationTokenSource? cancellation;
    private bool busy;
    private bool removeMode;
    private bool resume;
    private bool initialShow;
    private bool closeAfterRemoval;
    private TextBox? endpoint;
    private DataGrid? grid;

    public MainWindow(string[] args, StateStore? previewStore = null, bool visualPreview = false)
    {
        store = previewStore ?? new(DesktopInstall.Home);
        state = store.Load(); windows = new(runner);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WsaHelper/0.1.0");
        progress = new DispatcherProgress(Dispatcher, Report);
        BuildShell();
        removeMode = Path.GetFileNameWithoutExtension(Environment.ProcessPath)?.Equals("WsaRemove", StringComparison.OrdinalIgnoreCase) == true || args.Contains("--remove");
        var setupMode = Path.GetFileNameWithoutExtension(Environment.ProcessPath)?.Equals("WsaSetup", StringComparison.OrdinalIgnoreCase) == true || args.Contains("--setup");
        resume = args.Contains("--resume");
        foreach (var pending in state.PendingApks) queue.Enqueue(pending);
        foreach (var arg in args)
            if (!arg.StartsWith("--", StringComparison.Ordinal)) queue.Enqueue(Safety.Apk(arg));
        if (args.Any(a => a.StartsWith("--", StringComparison.Ordinal) && a is not ("--remove" or "--setup" or "--resume" or "--install")))
            throw new ArgumentException("Supported arguments: --setup, --remove, --install <file.apk>, or <file.apk>.");
        if (removeMode) ShowRemove(); else if (setupMode || state.Phase != "Installed") ShowSetup(); else ShowApps();
        initialShow = !removeMode && !setupMode && state.Phase == "Installed";
        Loaded += async (_, _) =>
        {
            if (visualPreview) return;
            inboxTimer.Start();
            if (resume) await Run(Install);
            else if (initialShow) await Run(async ct => { await ProcessQueue(ct); await Refresh(ct); });
        };
        inboxTimer.Tick += async (_, _) =>
        {
            if (busy || removeMode) return;
            try
            {
                var receivedRequests = false;
                var inbox = Path.Combine(store.Root, "inbox");
                if (Directory.Exists(inbox)) foreach (var file in Directory.EnumerateFiles(inbox, "*.json"))
                {
                    try
                    {
                        if (new FileInfo(file).Length > 65536) throw new InvalidDataException("Oversized APK request.");
                        var paths = (JsonSerializer.Deserialize<string[]>(File.ReadAllText(file)) ?? []).Select(Safety.Apk).ToArray();
                        // Persist the complete validated request before acknowledging its inbox file.
                        state.PendingApks = state.PendingApks.Concat(paths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); store.Save(state);
                        foreach (var path in paths) if (!queue.Contains(path, StringComparer.OrdinalIgnoreCase)) queue.Enqueue(path);
                        File.Delete(file); receivedRequests |= paths.Length > 0;
                    }
                    catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException)
                    {
                        File.Move(file, file + ".rejected", true); Report("APK request rejected: " + ex.Message);
                    }
                }
                // A failed connection is retried through Refresh or a new Explorer request, not a modal prompt every timer tick.
                if (receivedRequests && queue.Count > 0 && state.Phase == "Installed") await Run(async ct => { ShowApps(); await ProcessQueue(ct); await Refresh(ct); });
            }
            catch (Exception ex) { Report(ex.Message); }
        };
        Closing += OnClosing;
        Closed += (_, _) => { inboxTimer.Stop(); http.Dispose(); cancellation?.Dispose(); };
    }
    private void Report(string message)
    {
        status.Text = message; status.ToolTip = message; log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}"); log.ScrollToEnd();
    }
    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (busy) return;
        busy = true; SetBusy(true); foreach (var c in controls) c.IsEnabled = false;
        cancellation = new();
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { SetOperationState("Cancelled", "Muted"); Report("Operation cancelled. Completed changes remain recorded; retry setup or use Remove."); }
        catch (Exception ex) { SetOperationState("Action needed", "Warning"); Report(ex.Message); details.IsExpanded = true; }
        finally { cancellation.Dispose(); cancellation = null; busy = false; SetBusy(false); foreach (var c in controls) c.IsEnabled = true; UpdateSelection(); }
    }
    private SetupService Setup => new(store, windows, new VerifiedDownloads(http), progress);
    private async Task Install(CancellationToken ct)
    {
        closeAfterRemoval = false;
        state.PendingApks = state.PendingApks.Concat(queue).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); queue.Clear(); store.Save(state);
        DesktopInstall.Deploy(state); DesktopInstall.ResumeAtSignIn(false);
        var complete = await Setup.InstallAsync(state, ct);
        if (!complete) { DesktopInstall.ResumeAtSignIn(true); return; }
        DesktopInstall.Register(state); store.Save(state); DesktopInstall.ResumeAtSignIn(false);
        foreach (var path in state.PendingApks) queue.Enqueue(path);
        ShowApps(); OpenSettings();
        if (queue.Count > 0) await ProcessQueue(ct);
    }
    private void OpenSettings()
    {
        // Launch the package through AppsFolder so Windows resolves its actual settings entry point.
        WindowsBridge.Open(@"shell:AppsFolder\MicrosoftCorporationII.WindowsSubsystemForAndroid_8wekyb3d8bbwe!SettingsApp");
        Report("In WSA settings, enable Developer mode (Advanced settings), start WSA using Files, and approve ‘Allow debugging’. Then click Refresh. Keep the endpoint on 127.0.0.1.");
    }
    private AdbClient Adb()
    {
        state.Endpoint = Safety.Endpoint(endpoint?.Text.Trim() ?? state.Endpoint); store.Save(state);
        var exe = Path.Combine(store.Tools, "adb.exe");
        if (!File.Exists(exe)) throw new InvalidOperationException("Android tools are missing. Run Setup to repair them.");
        return new(runner, exe, state.Endpoint);
    }
    private async Task<AdbClient> ConnectedAdb(CancellationToken ct)
    {
        var adb = Adb();
        SetConnection("Connecting…", "Muted");
        try { await adb.ConnectAsync(ct); SetConnection("Connected to WSA", "Accent"); return adb; }
        catch (AdbNotReadyException ex)
        {
            SetConnection(ex.State == "unauthorized" ? "Approval needed" : "Starting WSA…", "Warning");
            if (ex.State != "unauthorized")
            {
                Report("Starting WSA. Approve Android debugging if asked…");
                WindowsBridge.Open("wsa://com.android.settings");
            }
            try { await adb.WaitUntilReadyAsync(TimeSpan.FromMinutes(2), progress, ct); }
            catch { SetConnection("Not connected", "Muted"); throw; }
            SetConnection("Connected to WSA", "Accent"); return adb;
        }
    }
    private async void ShowAppsAndRefresh() { ShowApps(); await Run(async ct => { await ProcessQueue(ct); await Refresh(ct); }); }
    private async Task Refresh(CancellationToken ct)
    {
        Report("Connecting to WSA…"); var adb = await ConnectedAdb(ct);
        var list = await adb.ListAsync(ct); DisplayApps(list);
        Report($"{list.Count} user-installed Android apps. Right-click an APK → Open with → WSA Helper installs it automatically.");
    }
    private async Task ProcessQueue(CancellationToken ct)
    {
        if (queue.Count == 0) return;
        state.PendingApks = state.PendingApks.Concat(queue).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); store.Save(state);
        var adb = await ConnectedAdb(ct);
        var failures = new List<string>();
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested(); var file = queue.Peek();
            try { Report("Installing " + Path.GetFileName(file) + "…"); await adb.InstallAsync(file, ct); Report("Installed " + Path.GetFileName(file)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { failures.Add(Path.GetFileName(file) + ": " + ex.Message); Report(failures[^1]); }
            queue.Dequeue(); state.PendingApks = state.PendingApks.Where(p => !p.Equals(file, StringComparison.OrdinalIgnoreCase)).ToArray(); store.Save(state);
        }
        if (failures.Count > 0) MessageBox.Show(this, string.Join("\n\n", failures), "Some APKs could not be installed");
    }
    private async Task Uninstall(string[] packages, CancellationToken ct)
    {
        if (packages.Length == 0) { Report("Select one or more Android apps first."); return; }
        if (MessageBox.Show(this, $"Uninstall {packages.Length} Android app(s) and delete their Android data?\n\n{string.Join("\n", packages.Take(12))}", "Confirm uninstall", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var adb = await ConnectedAdb(ct);
        var results = await adb.UninstallManyAsync(packages, progress, ct);
        var failures = results.Where(result => !result.Success).Select(result => result.PackageName + ": " + result.Error).ToArray();
        await Refresh(ct);
        if (failures.Length > 0) MessageBox.Show(this, string.Join("\n\n", failures), "Some apps could not be uninstalled");
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; Report("Wait for the operation to finish, or cancel it before closing. Windows feature and WSA deployment steps finish before cancellation."); return; }
        if (closeAfterRemoval) ScheduleExecutableCleanup();
    }
    private void ScheduleExecutableCleanup()
    {
        var marker = Path.Combine(DesktopInstall.Bin, ".wsa-helper-owned");
        if (!File.Exists(marker) || File.ReadAllText(marker) != state.InstallId) return;
        var command = "& { " + WindowsBridge.Script("CleanupHelper") + " } -Directory " + WindowsBridge.Quote(DesktopInstall.Bin) +
            " -InstallId " + WindowsBridge.Quote(state.InstallId) + " -WaitForPid " + Environment.ProcessId +
            " -ReportPath " + WindowsBridge.Quote(Path.Combine(store.Root, "cleanup-result.json"));
        _ = windows.CommandAsync(command);
    }
}
