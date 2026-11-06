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

internal sealed class MainWindow : Window
{
    private readonly StateStore store = new(DesktopInstall.Home);
    private readonly WindowsBridge windows;
    private readonly ProcessRunner runner = new();
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(45) };
    private InstallState state;
    private readonly StackPanel content = new() { Margin = new Thickness(28, 18, 28, 12) };
    private readonly TextBox log = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = new SolidColorBrush(Color.FromRgb(19, 25, 33)), Foreground = Brushes.LightGray, BorderThickness = new Thickness(0), Padding = new Thickness(14), FontFamily = new FontFamily("Consolas"), FontSize = 12 };
    private readonly TextBlock status = new() { Text = "Ready", Margin = new Thickness(28, 10, 28, 10), TextWrapping = TextWrapping.Wrap };
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

    public MainWindow(string[] args)
    {
        state = store.Load(); windows = new(runner);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WsaHelper/0.1.0");
        progress = new DispatcherProgress(Dispatcher, Report);
        Title = "WSA Helper"; Width = 1100; Height = 790; MinWidth = 850; MinHeight = 650;
        Background = new SolidColorBrush(Color.FromRgb(25, 32, 43)); Foreground = Brushes.White; FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        var buttonStyle = new Style(typeof(Button));
        buttonStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(15, 9, 15, 9)));
        buttonStyle.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0, 0, 10, 10)));
        buttonStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
        Resources.Add(typeof(Button), buttonStyle);
        var root = new DockPanel(); Content = root;
        var header = new StackPanel { Margin = new Thickness(28, 22, 28, 0) };
        header.Children.Add(new TextBlock { Text = "WSA Helper", FontSize = 30, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Android apps, at home on Windows.", Foreground = Brushes.LightSteelBlue, Margin = new Thickness(0, 4, 0, 16) });
        var navigation = new WrapPanel();
        AddButton(navigation, "Setup", (_, _) => ShowSetup()); AddButton(navigation, "Android apps", (_, _) => ShowAppsAndRefresh()); AddButton(navigation, "Remove", (_, _) => ShowRemove());
        header.Children.Add(navigation); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var bottom = new DockPanel { Height = 205, Margin = new Thickness(28, 0, 28, 20) };
        var cancel = new Button { Content = "Cancel current operation", HorizontalAlignment = HorizontalAlignment.Right };
        cancel.Click += (_, _) => cancellation?.Cancel(); DockPanel.SetDock(cancel, Dock.Top); bottom.Children.Add(cancel); bottom.Children.Add(log);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
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
        status.Text = message; log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}"); log.ScrollToEnd();
    }
    private Button AddButton(Panel panel, string label, RoutedEventHandler action)
    {
        var button = new Button { Content = label, IsEnabled = !busy }; button.Click += action; panel.Children.Add(button); controls.Add(button); return button;
    }
    private void Heading(string title, string description)
    {
        if (controls.Count > 3) controls.RemoveRange(3, controls.Count - 3);
        content.Children.Clear();
        content.Children.Add(new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        content.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, Margin = new Thickness(0, 0, 0, 15) });
    }
    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (busy) return;
        busy = true; foreach (var c in controls) c.IsEnabled = false;
        cancellation = new();
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { Report("Operation cancelled. Completed changes remain recorded; retry setup or use Remove."); }
        catch (Exception ex) { Report(ex.Message); MessageBox.Show(this, ex.Message, "Action needed", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally { cancellation.Dispose(); cancellation = null; busy = false; foreach (var c in controls) c.IsEnabled = true; }
    }
    private SetupService Setup => new(store, windows, new VerifiedDownloads(http), progress);
    internal void ShowSetup()
    {
        removeMode = false;
        Heading("Set up Android on Windows", "Microsoft retired WSA on March 5, 2025. This helper uses a pinned community WSA build (WSABuilds LTS 8), without Google Play, Amazon or root. It is unsupported by Microsoft and may stop working after Windows updates.");
        content.Children.Add(new TextBlock { Text = "Requires Windows 11 x64 · 8 GB RAM · 12 GB free space · firmware virtualization\n\nSetup downloads WSA and Google Android platform-tools. Windows may request administrator approval and a restart. WSA Developer mode and its debugging approval require your interaction. Existing WSA installations are reused.\n\nInstalling APKs means trusting their publishers. WSA and platform-tools retain their upstream license terms.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) });
        var terms = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        terms.Inlines.Add("By choosing Install, you accept the upstream terms, including the ");
        var sdkLink = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("Android SDK License Agreement")) { NavigateUri = new Uri("https://developer.android.com/studio/terms"), Foreground = Brushes.LightSkyBlue };
        sdkLink.RequestNavigate += (_, e) => { WindowsBridge.Open(e.Uri.AbsoluteUri); e.Handled = true; }; terms.Inlines.Add(sdkLink); content.Children.Add(terms);
        var buttons = new WrapPanel(); content.Children.Add(buttons);
        AddButton(buttons, "Install / resume setup", async (_, _) => await Run(Install));
        AddButton(buttons, "Check this PC", async (_, _) => await Run(async ct => { var m = await windows.ProbeAsync(ct); Report($"Windows build {m.Build}, {m.Architecture}; RAM {m.MemoryGb} GB; free {m.FreeGb} GB; virtualization {m.Virtualization}; VMP {m.VmpEnabled}; restart pending {m.RestartPending}; WSA {m.WsaFullName ?? "not installed"}."); }));
        AddButton(buttons, "WSA settings", (_, _) => OpenSettings());
    }
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
        try { await adb.ConnectAsync(ct); return adb; }
        catch (InvalidOperationException ex) when (!ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            Report("Starting WSA. Approve Android debugging if asked…");
            WindowsBridge.Open("wsa://com.android.settings");
            for (var attempt = 0; attempt < 15; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                try { await adb.ConnectAsync(ct); return adb; }
                catch (InvalidOperationException retry) when (attempt < 14 && !retry.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)) { }
            }
            throw;
        }
    }
    internal void ShowApps()
    {
        removeMode = false;
        Heading("Your Android apps", "User-installed apps in WSA (Android user 0). Package IDs identify apps reliably. Select several rows to uninstall together; Android app data is deleted on uninstall.");
        var connection = new WrapPanel(); content.Children.Add(connection);
        connection.Children.Add(new TextBlock { Text = "WSA endpoint", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) });
        endpoint = new TextBox { Text = state.Endpoint, Width = 185, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(7) }; connection.Children.Add(endpoint);
        AddButton(connection, "Refresh", async (_, _) => await Run(async ct => { await ProcessQueue(ct); await Refresh(ct); }));
        AddButton(connection, "WSA settings", (_, _) => OpenSettings());
        var actions = new WrapPanel(); content.Children.Add(actions);
        AddButton(actions, "Install APK…", async (_, _) =>
        {
            var picker = new OpenFileDialog { Filter = "Android APK (*.apk)|*.apk", Multiselect = true };
            if (picker.ShowDialog(this) != true) return;
            foreach (var file in picker.FileNames) queue.Enqueue(Safety.Apk(file));
            await Run(async ct => { await ProcessQueue(ct); await Refresh(ct); });
        });
        AddButton(actions, "Uninstall from APK…", async (_, _) =>
        {
            var picker = new OpenFileDialog { Filter = "Android APK (*.apk)|*.apk" };
            if (picker.ShowDialog(this) != true) return;
            await Run(async ct => { var package = ApkManifest.PackageFromFile(picker.FileName); await Uninstall([package], ct); });
        });
        AddButton(actions, "Uninstall selected", async (_, _) => await Run(async ct => { await Uninstall(grid?.SelectedItems.Cast<AndroidApp>().Select(a => a.PackageName).ToArray() ?? [], ct); }));
        AddButton(actions, "Select all", (_, _) => grid?.SelectAll());
        var search = new TextBox { Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(6), ToolTip = "Filter installed apps by package ID" };
        search.TextChanged += (_, _) => { CollectionViewSource.GetDefaultView(apps).Filter = a => ((AndroidApp)a).PackageName.Contains(search.Text, StringComparison.OrdinalIgnoreCase); };
        System.Windows.Automation.AutomationProperties.SetName(search, "Filter installed Android apps by package ID"); content.Children.Add(search);
        grid = new DataGrid { ItemsSource = apps, AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended, SelectionUnit = DataGridSelectionUnit.FullRow, Height = 175, Foreground = Brushes.Black, Background = Brushes.White, CanUserAddRows = false };
        grid.Columns.Add(new DataGridTextColumn { Header = "Android package", Binding = new Binding(nameof(AndroidApp.PackageName)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        content.Children.Add(grid);
    }
    private async void ShowAppsAndRefresh() { ShowApps(); await Run(async ct => { await ProcessQueue(ct); await Refresh(ct); }); }
    private async Task Refresh(CancellationToken ct)
    {
        Report("Connecting to WSA…"); var adb = await ConnectedAdb(ct);
        var list = await adb.ListAsync(ct); apps.Clear(); foreach (var app in list) apps.Add(app);
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
    internal void ShowRemove()
    {
        removeMode = true;
        Heading("Remove WSA Helper", "Removes WSA only when this helper installed it. Removing that subsystem deletes all its Android apps and data, including apps installed later through other tools. Existing WSA installations are preserved.");
        var restore = new CheckBox { Content = "Also restore Windows features and sideloading policy changed by setup", Foreground = Brushes.White, Margin = new Thickness(0, 5, 0, 15) }; content.Children.Add(restore);
        content.Children.Add(new TextBlock { Text = "Keep this unchecked if WSL, Docker, virtual machines, or other sideloaded apps now rely on these settings. Shared Microsoft framework packages are retained. A restart may be required.\n\nThe small recovery journal is retained so you can restore machine settings later. Use Remove again from the downloaded release if needed.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) });
        var actions = new WrapPanel(); content.Children.Add(actions);
        AddButton(actions, "Remove installation", async (_, _) =>
        {
            if (MessageBox.Show(this, "Remove this installation? If WSA was installed by this helper, all of its Android apps and data will be deleted.", "Confirm removal", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await Run(async ct =>
            {
                if (File.Exists(Path.Combine(store.Tools, "adb.exe"))) await new AdbClient(runner, Path.Combine(store.Tools, "adb.exe"), state.Endpoint).StopAsync();
                await Setup.RemoveAsync(state, restore.IsChecked == true, ct);
                queue.Clear();
                DesktopInstall.Unregister(); state.AssociationRegistered = false; store.Save(state);
                closeAfterRemoval = true;
                Report("Removal finished. Close this window to remove the helper executables. The recovery journal remains in " + store.Root);
            });
        });
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; Report("Wait for the operation to finish, or cancel it before closing. Windows feature changes finish before cancellation."); return; }
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
