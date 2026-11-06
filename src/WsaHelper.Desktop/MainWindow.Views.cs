using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using WsaHelper.Core;

namespace WsaHelper.Desktop;

internal sealed partial class MainWindow
{
    private readonly StackPanel content = new() { Margin = new Thickness(32, 26, 32, 24) };
    private readonly TextBox log = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 135, FontFamily = new FontFamily("Consolas"), FontSize = 11, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock status = new() { Text = "Choose an action to get started.", TextWrapping = TextWrapping.Wrap, MaxHeight = 40, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly Expander details = new() { Header = "Activity details", Margin = new Thickness(0, 12, 0, 0), FontSize = 12 };
    private readonly ProgressBar activity = new() { Height = 3, Visibility = Visibility.Collapsed };
    private readonly TextBlock operation = new() { Text = "Ready", FontSize = 11, FontWeight = FontWeights.SemiBold };
    private readonly Button cancel = new() { Content = "Cancel", Visibility = Visibility.Collapsed };
    private readonly Dictionary<string, Button> navigation = [];
    private TextBlock? connectionLabel, appCount, selectionCount;
    private Button? uninstallSelected;
    private StackPanel? emptyState;
    private TextBox? search;
    private ScrollViewer? pageViewport;
    private bool appsLoaded, dark, operationFailed;
    private string page = "Setup", connectionText = "Not connected", connectionColor = "Muted";

    private static void Paint(FrameworkElement target, DependencyProperty property, string key) => target.SetResourceReference(property, key);
    private static TextBlock Text(string value, double size = 13, string color = "Ink", FontWeight? weight = null)
    {
        var text = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = weight ?? FontWeights.Normal };
        Paint(text, TextBlock.ForegroundProperty, color); return text;
    }
    private static TextBlock Glyph(string value, double size = 18, string color = "Accent")
    {
        var icon = Text(value, size, color); icon.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        icon.IsHitTestVisible = false; icon.Focusable = false; return icon;
    }
    private static Border Card(UIElement child, string color = "Surface", double padding = 22)
    {
        var border = new Border { Child = child, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(padding), Margin = new Thickness(0, 0, 0, 16) };
        Paint(border, Border.BackgroundProperty, color); Paint(border, Border.BorderBrushProperty, "Line"); return border;
    }
    private static Border Badge(string value, string background = "Tint", string foreground = "Accent")
    {
        var badge = new Border { Child = Text(value, 11, foreground, FontWeights.SemiBold), CornerRadius = new CornerRadius(5), Padding = new Thickness(9, 5, 9, 5), HorizontalAlignment = HorizontalAlignment.Left };
        Paint(badge, Border.BackgroundProperty, background); return badge;
    }
    private static TextBlock Paragraph(Panel parent, string value, double size = 12, string color = "Muted", double bottom = 12)
    {
        var text = Text(value, size, color); text.Margin = new Thickness(0, 0, 0, bottom); parent.Children.Add(text); return text;
    }
    private Button AddButton(Panel panel, string label, RoutedEventHandler action, string? style = null, string? glyph = null)
    {
        var button = new Button { IsEnabled = !busy, HorizontalContentAlignment = style == "Nav" ? HorizontalAlignment.Left : HorizontalAlignment.Center };
        if (style is not null) button.Style = (Style)FindResource(style);
        if (glyph is null) button.Content = label;
        else
        {
            var inline = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = Glyph(glyph, 16); icon.Margin = new Thickness(0, 0, 10, 0); icon.VerticalAlignment = VerticalAlignment.Center;
            var text = Text(label); text.VerticalAlignment = VerticalAlignment.Center;
            foreach (var item in new[] { icon, text }) item.SetBinding(TextBlock.ForegroundProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
            inline.Children.Add(icon); inline.Children.Add(text); button.Content = inline;
        }
        AutomationProperties.SetName(button, label); button.Click += action; panel.Children.Add(button); controls.Add(button); return button;
    }
    private void BuildShell()
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/WsaHelper;component/Theme.xaml", UriKind.Relative) });
        dark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int setting && setting == 0;
        DesktopTheme.Apply(this, dark);
        Title = "WSA Helper"; Width = 1180; Height = 860; MinWidth = 850; MinHeight = 650;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/WsaHelper;component/Assets/WsaHelper.ico"));
        WindowStartupLocation = WindowStartupLocation.CenterScreen; UseLayoutRounding = true; SnapsToDevicePixels = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"); FontSize = 13;
        Paint(this, BackgroundProperty, "Canvas"); Paint(this, ForegroundProperty, "Ink");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        var shell = new Grid(); Paint(shell, Panel.BackgroundProperty, "Canvas"); Content = shell;
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(184) }); shell.ColumnDefinitions.Add(new ColumnDefinition());
        var rail = new DockPanel { Margin = new Thickness(15, 26, 15, 18) };
        var sidebar = new Border { Child = rail, BorderThickness = new Thickness(0, 0, 1, 0) };
        Paint(sidebar, Border.BackgroundProperty, "Sidebar"); Paint(sidebar, Border.BorderBrushProperty, "Line"); shell.Children.Add(sidebar);
        var brand = new StackPanel { Margin = new Thickness(10, 0, 0, 38) };
        brand.Children.Add(BrandMark()); brand.Children.Add(Text("WSA Helper", 19, weight: FontWeights.SemiBold));
        var caption = Text("Android on Windows", 11, "Muted"); caption.Margin = new Thickness(0, 4, 0, 0); brand.Children.Add(caption);
        DockPanel.SetDock(brand, Dock.Top); rail.Children.Add(brand);
        var railBottom = new StackPanel { Margin = new Thickness(10, 0, 0, 0) }; railBottom.Children.Add(Badge("EXPERIMENTAL", "WarningTint", "Warning"));
        var version = Text("Version 0.1.0 · Windows x64", 10, "Muted"); version.Margin = new Thickness(0, 10, 0, 0); railBottom.Children.Add(version);
        DockPanel.SetDock(railBottom, Dock.Bottom); rail.Children.Add(railBottom);
        var nav = new StackPanel(); rail.Children.Add(nav);
        navigation.Add("Setup", AddButton(nav, "Setup", (_, _) => ShowSetup(), "Nav", "\uE713"));
        navigation.Add("Android apps", AddButton(nav, "Android apps", (_, _) => ShowAppsAndRefresh(), "Nav", "\uE71D"));
        navigation.Add("Remove", AddButton(nav, "Remove", (_, _) => ShowRemove(), "Nav", "\uE74D"));
        var body = new DockPanel(); Grid.SetColumn(body, 1); shell.Children.Add(body);
        var top = new DockPanel { Margin = new Thickness(32, 18, 24, 0) };
        var appearance = new Button { Content = "\uE708", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Style = (Style)FindResource("Quiet"), Margin = new Thickness(0), Padding = new Thickness(10, 7, 10, 7), ToolTip = "Switch color theme", IsEnabled = !SystemParameters.HighContrast };
        AutomationProperties.SetName(appearance, "Switch between light and dark theme");
        appearance.Click += (_, _) => { dark = !dark; DesktopTheme.Apply(this, dark); };
        DockPanel.SetDock(appearance, Dock.Right); top.Children.Add(appearance);
        var product = Text("YOUR ANDROID WORKSPACE", 10, "Muted", FontWeights.SemiBold); product.VerticalAlignment = VerticalAlignment.Center; top.Children.Add(product);
        DockPanel.SetDock(top, Dock.Top); body.Children.Add(top);
        var footer = new StackPanel { Margin = new Thickness(32, 0, 32, 18) };
        Paint(activity, ProgressBar.ForegroundProperty, "Accent"); footer.Children.Add(activity);
        var statusRow = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        cancel.Style = (Style)FindResource("Quiet"); cancel.Margin = new Thickness(12, 0, 0, 0); cancel.Click += (_, _) => cancellation?.Cancel();
        AutomationProperties.SetName(cancel, "Cancel current operation"); DockPanel.SetDock(cancel, Dock.Right); statusRow.Children.Add(cancel);
        var pill = new Border { Child = operation, CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        Paint(pill, Border.BackgroundProperty, "Tint"); Paint(operation, TextBlock.ForegroundProperty, "Accent"); DockPanel.SetDock(pill, Dock.Left); statusRow.Children.Add(pill);
        Paint(status, TextBlock.ForegroundProperty, "Muted"); statusRow.Children.Add(status); footer.Children.Add(statusRow);
        Paint(log, Control.BackgroundProperty, "Surface"); Paint(log, Control.ForegroundProperty, "Muted");
        details.Content = log; Paint(details, Control.ForegroundProperty, "Muted"); footer.Children.Add(details);
        DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer);
        var scroller = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetName(scroller, "Page content");
        pageViewport = scroller;
        body.Children.Add(scroller); scroller.SizeChanged += (_, _) => { if (grid is not null) grid.Height = Math.Max(100, scroller.ActualHeight - 430); };
    }
    private static UIElement BrandMark()
    {
        var symbol = new Grid { Width = 40, Height = 40, Margin = new Thickness(0, 0, 0, 14), HorizontalAlignment = HorizontalAlignment.Left };
        var tile = new Border { CornerRadius = new CornerRadius(11) }; Paint(tile, Border.BackgroundProperty, "Accent"); symbol.Children.Add(tile);
        var drawing = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 10,20 A 10,10 0 0 1 30,20 L 30,28 Q 30,30 28,30 L 12,30 Q 10,30 10,28 Z M 12,9 L 16,15 M 28,9 L 24,15"), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round, Margin = new Thickness(0, -1, 0, 0) };
        Paint(drawing, System.Windows.Shapes.Shape.FillProperty, "OnAccent"); Paint(drawing, System.Windows.Shapes.Shape.StrokeProperty, "OnAccent"); symbol.Children.Add(drawing);
        foreach (var x in new[] { 15.0, 23.0 })
        {
            var eye = new System.Windows.Shapes.Ellipse { Width = 2.5, Height = 2.5, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(x, 19, 0, 0) };
            Paint(eye, System.Windows.Shapes.Shape.FillProperty, "Accent"); symbol.Children.Add(eye);
        }
        return symbol;
    }
    private void Heading(string section, string title, string description)
    {
        if (controls.Count > 3) controls.RemoveRange(3, controls.Count - 3);
        page = section; foreach (var (name, button) in navigation) button.Tag = name == section ? "selected" : null;
        content.Children.Clear(); grid = null; endpoint = null; search = null; uninstallSelected = null; emptyState = null; selectionCount = null; connectionLabel = null;
        pageViewport?.ScrollToTop();
        var eyebrow = Text(section == "Setup" ? "GET STARTED" : section == "Remove" ? "YOUR INSTALLATION" : "APP LIBRARY", 10, "Accent", FontWeights.SemiBold);
        eyebrow.Margin = new Thickness(0, 0, 0, 10);
        var headingRow = new DockPanel();
        if (section == "Android apps")
        {
            connectionLabel = Text(connectionText, 11, connectionColor, FontWeights.SemiBold);
            DockPanel.SetDock(connectionLabel, Dock.Right); headingRow.Children.Add(connectionLabel);
        }
        headingRow.Children.Add(eyebrow); content.Children.Add(headingRow);
        content.Children.Add(Text(title, 30, weight: FontWeights.SemiBold));
        var sub = Text(description, 13, "Muted"); sub.Margin = new Thickness(0, 8, 0, 24); content.Children.Add(sub);
    }
    internal void ShowSetup()
    {
        removeMode = false; Heading("Setup", "Android, right at home.", "One setup for WSA, APK installs, and a home for your Android apps.");
        var setup = new StackPanel(); setup.Children.Add(Badge(state.Phase == "Installed" ? "HELPER INSTALLED" : "WINDOWS 11 · X64"));
        var title = Text("Let's get everything connected.", 21, weight: FontWeights.SemiBold); title.Margin = new Thickness(0, 16, 0, 8); setup.Children.Add(title);
        Paragraph(setup, "We check your PC, download the essentials, and guide you through Android's final connection. An existing WSA installation stays in place.", 13);
        var steps = new Grid { Margin = new Thickness(0, 10, 0, 22) };
        string[] names = ["Check your PC", "Install the essentials", "Connect to Android"];
        string[] descriptions = ["Windows, space and virtualization.", "Verified WSA and private Android tools.", "Enable Developer mode and approve debugging."];
        for (var i = 0; i < names.Length; i++)
        {
            steps.ColumnDefinitions.Add(new ColumnDefinition()); var step = new StackPanel { Margin = new Thickness(0, 0, i == 2 ? 0 : 20, 0) };
            var number = Text("0" + (i + 1), 11, "Accent", FontWeights.SemiBold); number.Margin = new Thickness(0, 0, 0, 9); step.Children.Add(number);
            step.Children.Add(Text(names[i], 13, weight: FontWeights.SemiBold)); var detail = Text(descriptions[i], 12, "Muted"); detail.Margin = new Thickness(0, 5, 0, 0); step.Children.Add(detail);
            Grid.SetColumn(step, i); steps.Children.Add(step);
        }
        setup.Children.Add(steps); var actions = new WrapPanel();
        AddButton(actions, "Install / resume setup", async (_, _) => await Run(Install), "Primary", "\uE896");
        AddButton(actions, "Check this PC", async (_, _) => await Run(async ct =>
        {
            var m = await windows.ProbeAsync(ct); Report($"Windows {m.Build}, {m.Architecture} · {m.MemoryGb} GB RAM · {m.FreeGb} GB free · Virtualization: {m.Virtualization} · VMP: {m.VmpEnabled} · Restart pending: {m.RestartPending} · WSA: {m.WsaFullName ?? "not installed"}");
        }));
        setup.Children.Add(actions); content.Children.Add(Card(setup));
        var requirements = new WrapPanel(); foreach (var requirement in new[] { "Windows 11 x64", "8 GB RAM", "12 GB free space", "Firmware virtualization" })
        { var item = Badge(requirement, "Sidebar", "Muted"); item.Margin = new Thickness(0, 0, 8, 8); requirements.Children.Add(item); }
        content.Children.Add(requirements);
        var note = new StackPanel(); note.Children.Add(Text("A note about WSA", 13, weight: FontWeights.SemiBold));
        var noteCopy = Paragraph(note, "Microsoft retired WSA. This helper uses the pinned WSABuilds LTS 8 community build, without Google Play, Amazon or root. Future Windows compatibility is not guaranteed."); noteCopy.Margin = new Thickness(0, 6, 0, 12);
        Paragraph(note, "Fresh setup requires an administrator account approving its own UAC prompts. A restart may be needed. Developer mode and Android debugging require your approval.");
        var terms = Text("By installing, you accept the upstream terms, including the ", 11, "Muted");
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("Android SDK License Agreement")) { NavigateUri = new Uri("https://developer.android.com/studio/terms") };
        link.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Accent");
        link.RequestNavigate += (_, e) => { WindowsBridge.Open(e.Uri.AbsoluteUri); e.Handled = true; }; terms.Inlines.Add(link); note.Children.Add(terms);
        content.Children.Add(Card(note, "Canvas", 18));
    }
    internal void ShowApps()
    {
        removeMode = false; Heading("Android apps", "Your Android apps", "Install APKs here, or right-click an APK → Open with → WSA Helper.");
        CollectionViewSource.GetDefaultView(apps).Filter = null;
        var library = new StackPanel(); var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var actions = new WrapPanel();
        AddButton(actions, "Install APK…", async (_, _) =>
        {
            var picker = new OpenFileDialog { Filter = "Android APK (*.apk)|*.apk", Multiselect = true }; if (picker.ShowDialog(this) != true) return;
            foreach (var file in picker.FileNames) queue.Enqueue(Safety.Apk(file)); await Run(async ct => { await ProcessQueue(ct); await Refresh(ct); });
        }, "Primary", "\uE898");
        AddButton(actions, "Refresh", async (_, _) => await Run(async ct => { await ProcessQueue(ct); await Refresh(ct); }), glyph: "\uE72C");
        DockPanel.SetDock(actions, Dock.Right); toolbar.Children.Add(actions);
        var libraryLabel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        libraryLabel.Children.Add(Text("Installed apps", 16, weight: FontWeights.SemiBold));
        appCount = Text("", 12, "Muted"); appCount.Margin = new Thickness(10, 0, 0, 0); appCount.VerticalAlignment = VerticalAlignment.Center; libraryLabel.Children.Add(appCount);
        toolbar.Children.Add(libraryLabel); library.Children.Add(toolbar);
        var searchArea = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        search = new TextBox { ToolTip = "Search installed apps by package ID", Padding = new Thickness(36, 11, 12, 11) }; AutomationProperties.SetName(search, "Search installed apps by package ID");
        var placeholder = Text("Search by package ID", 12, "Muted"); placeholder.Margin = new Thickness(36, 0, 0, 0); placeholder.VerticalAlignment = VerticalAlignment.Center; placeholder.IsHitTestVisible = false;
        search.TextChanged += (_, _) => { CollectionViewSource.GetDefaultView(apps).Filter = a => ((AndroidApp)a).PackageName.Contains(search.Text, StringComparison.OrdinalIgnoreCase); placeholder.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; UpdateLibrary(); };
        searchArea.Children.Add(search); var searchIcon = Glyph("\uE721", 14, "Muted"); searchIcon.VerticalAlignment = VerticalAlignment.Center; searchIcon.Margin = new Thickness(13, 0, 0, 0); searchArea.Children.Add(searchIcon); searchArea.Children.Add(placeholder); library.Children.Add(searchArea);
        var table = new Grid();
        grid = new DataGrid { ItemsSource = apps, AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended, SelectionUnit = DataGridSelectionUnit.FullRow, Height = Math.Max(100, (pageViewport?.ActualHeight ?? 640) - 430), CanUserAddRows = false, CanUserDeleteRows = false, CanUserResizeRows = false, CanUserReorderColumns = false, EnableRowVirtualization = true };
        AutomationProperties.SetName(grid, "Installed Android apps. Use Control or Shift to select multiple apps.");
        var packageStyle = new Style(typeof(TextBlock));
        packageStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        packageStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
        packageStyle.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new FontFamily("Cascadia Mono, Consolas")));
        packageStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12.0));
        packageStyle.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(AndroidApp.PackageName))));
        grid.Columns.Add(new DataGridTextColumn { Header = "ANDROID PACKAGE", Binding = new Binding(nameof(AndroidApp.PackageName)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), ElementStyle = packageStyle });
        grid.SelectionChanged += (_, _) => UpdateSelection(); table.Children.Add(grid);
        emptyState = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24, 38, 24, 12), MaxWidth = 380, IsHitTestVisible = false }; table.Children.Add(emptyState); library.Children.Add(table);
        var bottom = new DockPanel { Margin = new Thickness(0, 14, 0, 0) }; var selectionActions = new WrapPanel();
        AddButton(selectionActions, "Select all", (_, _) => grid?.SelectAll(), "Quiet");
        uninstallSelected = AddButton(selectionActions, "Uninstall selected", async (_, _) => await Run(async ct => await Uninstall(grid?.SelectedItems.Cast<AndroidApp>().Select(a => a.PackageName).ToArray() ?? [], ct)));
        DockPanel.SetDock(selectionActions, Dock.Right); bottom.Children.Add(selectionActions);
        selectionCount = Text("0 selected", 11, "Muted"); selectionCount.VerticalAlignment = VerticalAlignment.Center; bottom.Children.Add(selectionCount); library.Children.Add(bottom);
        Paragraph(library, "Ctrl / Shift to select several apps. Uninstalling deletes their Android data.", 11, bottom: 0); content.Children.Add(Card(library));
        var more = new Expander { Header = "Connection & APK tools", Margin = new Thickness(0, 8, 0, 0), FontSize = 12 }; Paint(more, Control.ForegroundProperty, "Muted");
        var advanced = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; advanced.Children.Add(Text("WSA endpoint · loopback only", 11, "Muted"));
        var advancedActions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        endpoint = new TextBox { Text = state.Endpoint, Width = 185, Margin = new Thickness(0, 0, 10, 8) }; AutomationProperties.SetName(endpoint, "WSA debugging endpoint"); advancedActions.Children.Add(endpoint);
        AddButton(advancedActions, "WSA settings", (_, _) => OpenSettings(), "Quiet");
        AddButton(advancedActions, "Uninstall from APK…", async (_, _) =>
        {
            var picker = new OpenFileDialog { Filter = "Android APK (*.apk)|*.apk" };
            if (picker.ShowDialog(this) == true) await Run(async ct => await Uninstall([ApkManifest.PackageFromFile(picker.FileName)], ct));
        });
        advanced.Children.Add(advancedActions); more.Content = advanced; content.Children.Add(more); UpdateLibrary(); UpdateSelection();
    }
    internal void DisplayApps(IReadOnlyList<AndroidApp> list)
    {
        appsLoaded = true; apps.Clear(); foreach (var app in list) apps.Add(app); UpdateLibrary();
    }
    private void UpdateLibrary()
    {
        if (page != "Android apps" || emptyState is null) return;
        var visible = CollectionViewSource.GetDefaultView(apps).Cast<AndroidApp>().Count();
        if (appCount is not null) appCount.Text = appsLoaded ? (visible == apps.Count ? apps.Count.ToString() : $"{visible} of {apps.Count}") : "";
        emptyState.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed; emptyState.Children.Clear();
        var icon = Glyph("\uE71D", 28, "Muted"); icon.HorizontalAlignment = HorizontalAlignment.Center; icon.Margin = new Thickness(0, 0, 0, 12); emptyState.Children.Add(icon);
        var title = Text(!appsLoaded ? "Connect to see your apps" : apps.Count == 0 ? "No Android apps yet" : "No matching apps", 16, weight: FontWeights.SemiBold); title.TextAlignment = TextAlignment.Center; emptyState.Children.Add(title);
        var explanation = Text(!appsLoaded ? "Start WSA and choose Refresh. Your installed apps will appear here." : apps.Count == 0 ? "Choose Install APK to add your first Android app." : "Try a different package ID or clear the search.", 12, "Muted"); explanation.TextAlignment = TextAlignment.Center; explanation.Margin = new Thickness(0, 7, 0, 0); emptyState.Children.Add(explanation);
    }
    private void UpdateSelection()
    {
        if (selectionCount is not null) selectionCount.Text = $"{grid?.SelectedItems.Count ?? 0} selected";
        if (uninstallSelected is not null) uninstallSelected.IsEnabled = !busy && grid?.SelectedItems.Count > 0;
    }
    private void SetConnection(string label, string color)
    {
        connectionText = label; connectionColor = color;
        if (connectionLabel is null) return; connectionLabel.Text = label; Paint(connectionLabel, TextBlock.ForegroundProperty, color);
    }
    private void SetOperationState(string label, string color)
    {
        operation.Text = label; Paint(operation, TextBlock.ForegroundProperty, color); operationFailed = true;
    }
    private void SetBusy(bool value)
    {
        cancel.Visibility = value ? Visibility.Visible : Visibility.Collapsed; activity.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        activity.IsIndeterminate = value && SystemParameters.ClientAreaAnimation;
        if (value) { operationFailed = false; operation.Text = "Working"; Paint(operation, TextBlock.ForegroundProperty, "Accent"); }
        else if (!operationFailed) operation.Text = "Ready";
    }
    internal void ShowRemove()
    {
        removeMode = true; Heading("Remove", "Leave things tidy.", "Remove the helper and review what happens to your installation.");
        var summary = new StackPanel(); summary.Children.Add(Badge("REMOVAL", "DangerTint", "Danger"));
        var title = Text("You're in control of what goes.", 21, weight: FontWeights.SemiBold); title.Margin = new Thickness(0, 16, 0, 18); summary.Children.Add(title);
        SummaryRow(summary, "\uE74D", "Helper & Windows integration", "Removes helper files, private Android tools, shortcuts and Open with registration.");
        SummaryRow(summary, "\uE73E", "Pre-existing WSA is preserved", "Subsystems installed before this helper and their Android apps are kept. Shared Microsoft frameworks stay installed.");
        var warning = new StackPanel(); warning.Children.Add(Text("If this helper installed WSA", 13, "Danger", FontWeights.SemiBold));
        var warningCopy = Text("Removing it deletes that entire subsystem, including all Android apps and data added later. Back up anything you need first.", 12, "Muted"); warningCopy.Margin = new Thickness(0, 6, 0, 0); warning.Children.Add(warningCopy); summary.Children.Add(Card(warning, "DangerTint", 16));
        var restore = new CheckBox { Content = Text("Also restore Windows features changed by setup", 13), Margin = new Thickness(0, 0, 0, 8) }; summary.Children.Add(restore);
        var help = Text("Leave this off if WSL, Docker, other VMs or sideloaded apps depend on these settings. A restart may be required.", 12, "Muted"); help.Margin = new Thickness(27, 0, 0, 20); summary.Children.Add(help);
        var actions = new WrapPanel(); AddButton(actions, "Remove installation", async (_, _) =>
        {
            if (MessageBox.Show(this, "Remove this installation? If WSA was installed by this helper, all of its Android apps and data will be deleted.", "Confirm removal", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await Run(async ct =>
            {
                if (File.Exists(Path.Combine(store.Tools, "adb.exe"))) await new AdbClient(runner, Path.Combine(store.Tools, "adb.exe"), state.Endpoint).StopAsync();
                await Setup.RemoveAsync(state, restore.IsChecked == true, ct); queue.Clear(); DesktopInstall.Unregister(); state.AssociationRegistered = false; store.Save(state); closeAfterRemoval = true;
                Report("Removal finished. Close this window to remove the helper executables. The recovery journal remains in " + store.Root);
            });
        }, "DangerButton", "\uE74D");
        AddButton(actions, "Keep my installation", (_, _) => ShowSetup(), "Quiet"); summary.Children.Add(actions); content.Children.Add(Card(summary));
        content.Children.Add(Text("A small recovery journal is kept so you can restore recorded Windows settings later. Removal always asks for confirmation.", 12, "Muted"));
    }
    private static void SummaryRow(Panel panel, string glyph, string title, string description)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 20) }; var icon = Glyph(glyph, 18); icon.Margin = new Thickness(0, 3, 14, 0); DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon);
        var copy = new StackPanel(); copy.Children.Add(Text(title, 13, weight: FontWeights.SemiBold)); var sub = Text(description, 12, "Muted"); sub.Margin = new Thickness(0, 5, 0, 0); copy.Children.Add(sub); row.Children.Add(copy); panel.Children.Add(row);
    }
}
