using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WsaHelper.Desktop;
using Xunit;

namespace WsaHelper.Desktop.Tests;

public sealed class WindowTests
{
    [Fact]
    public void AllScreensConstructAndRenderWithoutRunningSetup()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow(["--setup"]);
                var directory = Environment.GetEnvironmentVariable("WSA_HELPER_RENDER_DIR");
                foreach (var (screen, action) in new (string, Action)[] { ("setup", window.ShowSetup), ("apps", window.ShowApps), ("remove", window.ShowRemove) })
                {
                    action(); var visual = (FrameworkElement)window.Content;
                    visual.Measure(new Size(1100, 750)); visual.Arrange(new Rect(0, 0, 1100, 750)); visual.UpdateLayout();
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                    var bitmap = new RenderTargetBitmap(1100, 750, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                    Assert.Equal(1100, bitmap.PixelWidth); Assert.True(visual.ActualHeight > 0);
                    if (directory is not null)
                    {
                        Directory.CreateDirectory(directory); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(directory, screen + ".png")); encoder.Save(file);
                    }
                }
                window.Close();
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30))); Assert.Null(failure);
    }
}
