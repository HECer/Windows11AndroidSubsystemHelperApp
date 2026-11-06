using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Automation;
using WsaHelper.Desktop;
using Xunit;

namespace WsaHelper.Desktop.Tests;

public sealed class WindowTests
{
    [Fact]
    public void ProgressCreatedBeforeTheApplicationLoopUpdatesControlsOnTheirOwnerThread()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(null);
                var label = new System.Windows.Controls.TextBlock();
                var owner = Environment.CurrentManagedThreadId; var callbackThread = 0;
                IProgress<string> progress = new DispatcherProgress(label.Dispatcher, message => { callbackThread = Environment.CurrentManagedThreadId; label.Text = message; });
                Task.Run(() => progress.Report("Setup progress")).GetAwaiter().GetResult();
                label.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.Equal(owner, callbackThread); Assert.Equal("Setup progress", label.Text);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(20))); Assert.Null(failure);
    }
    [Fact]
    public void DeploymentDoesNotCopyUnrelatedFilesBesideTheDistribution()
    {
        var root = Path.Combine(Path.GetTempPath(), "WsaHelper-distribution-test-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source"); var target = Path.Combine(root, "target"); Directory.CreateDirectory(source);
        try
        {
            foreach (var name in new[] { "WsaHelper.exe", "LICENSE", "README.md", "unrelated.exe", "private-settings.json", "notes.txt" })
                File.WriteAllText(Path.Combine(source, name), "disposable fixture");
            var copied = DesktopInstall.CopyDistributionFiles(source, target);
            Assert.Equal(new[] { "LICENSE", "README.md", "WsaHelper.exe" }, copied.Order(StringComparer.Ordinal));
            Assert.Equal(3, Directory.GetFiles(target).Length); Assert.False(File.Exists(Path.Combine(target, "private-settings.json")));
        }
        finally { Core.OwnedFiles.DeleteTree(root); }
    }
    [Fact]
    public void AllScreensConstructAndRenderWithoutRunningSetup()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var previewRoot = Path.Combine(Path.GetTempPath(), "WsaHelper-ui-test-" + Guid.NewGuid().ToString("N"));
                var window = new MainWindow(["--setup"], new Core.StateStore(previewRoot), visualPreview: true);
                var directory = Environment.GetEnvironmentVariable("WSA_HELPER_RENDER_DIR");
                window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -20000; window.Top = -20000; window.ShowInTaskbar = false; window.Show();
                try
                {
                    foreach (var dark in new[] { false, true })
                    {
                        DesktopTheme.Apply(window, dark);
                        foreach (var compact in new[] { false, true })
                        {
                        window.Width = compact ? 850 : 1180; window.Height = compact ? 650 : 860;
                        foreach (var (screen, action) in new (string, Action)[] { ("setup", window.ShowSetup), ("apps", window.ShowApps), ("remove", window.ShowRemove), ("apps-populated", () =>
                        {
                            window.ShowApps(); window.DisplayApps([new("com.example.notes", "", ""), new("com.example.reader", "", ""), new("com.example.a_very_long_package_identifier_with_many_segments.to_check_ellipsis_and_horizontal_overflow", "", "")]);
                        }), ("apps-selected", () =>
                        {
                            window.ShowApps();
                            var appGrid = Descendants<DataGrid>(window).Single(); appGrid.SelectAll();
                            Assert.Equal(3, appGrid.SelectedItems.Count);
                            Assert.True(Descendants<Button>(window).Single(b => AutomationProperties.GetName(b) == "Uninstall selected").IsEnabled);
                        }), ("apps-no-matches", () =>
                        {
                            window.ShowApps(); Descendants<TextBox>(window).Single(t => AutomationProperties.GetName(t) == "Search installed apps by package ID").Text = "no-match";
                            Assert.Empty(Descendants<DataGrid>(window).Single().Items.Cast<object>());
                        }), ("apps-empty", () => { window.ShowApps(); window.DisplayApps([]); }) })
                        {
                            action(); var visual = (FrameworkElement)window.Content;
                            visual.UpdateLayout();
                            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                            if (screen == "apps-selected")
                            {
                                var viewport = Descendants<ScrollViewer>(window).Single(v => AutomationProperties.GetName(v) == "Page content");
                                var remove = Descendants<Button>(window).Single(b => AutomationProperties.GetName(b) == "Uninstall selected");
                                var bottom = remove.TransformToAncestor(viewport).Transform(new Point(0, remove.ActualHeight)).Y;
                                Assert.True(bottom <= viewport.ActualHeight, "Bulk removal must remain visible at the minimum window size.");
                            }
                            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                            Assert.True(bitmap.PixelWidth > 800); Assert.True(visual.ActualHeight > 0);
                            if (directory is not null)
                            {
                                Directory.CreateDirectory(directory); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                                using var file = File.Create(Path.Combine(directory, screen + (dark ? "-dark" : "-light") + (compact ? "-compact" : "") + ".png")); encoder.Save(file);
                            }
                        }
                        }
                    }
                }
                finally { window.Close(); Core.OwnedFiles.DeleteTree(previewRoot); }
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30))); Assert.Null(failure);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
