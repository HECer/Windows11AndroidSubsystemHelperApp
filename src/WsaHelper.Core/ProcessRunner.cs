using System.Diagnostics;
using System.Text;

namespace WsaHelper.Core;

public sealed record ProcessResult(int ExitCode, string Output, string Error)
{
    public string RequireSuccess()
    {
        if (ExitCode != 0) throw new InvalidOperationException($"Command failed ({ExitCode}). {Error}\n{Output}");
        return Output;
    }
}
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string file, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default);
}
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string file, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var process = new Process { StartInfo = new(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 } };
        if (Path.GetFileName(file).Equals("adb.exe", StringComparison.OrdinalIgnoreCase))
            foreach (var variable in new[] { "ADB_SERVER_SOCKET", "ANDROID_ADB_SERVER_PORT", "ADB_SERVER_PORT", "ANDROID_SERIAL" }) process.StartInfo.Environment.Remove(variable);
        foreach (var arg in arguments) process.StartInfo.ArgumentList.Add(arg);
        if (!process.Start()) throw new InvalidOperationException($"Could not start {Path.GetFileName(file)}.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch (InvalidOperationException) { }
            if (!cancellationToken.IsCancellationRequested) throw new TimeoutException($"{Path.GetFileName(file)} timed out. Retry after checking Windows/WSA status.");
            throw;
        }
        return new(process.ExitCode, await output, await error);
    }
}
