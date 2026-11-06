using System.Windows.Threading;

namespace WsaHelper.Desktop;

internal sealed class DispatcherProgress(Dispatcher dispatcher, Action<string> handler) : IProgress<string>
{
    public void Report(string value)
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        if (dispatcher.CheckAccess()) handler(value);
        else dispatcher.BeginInvoke(() => Report(value));
    }
}
