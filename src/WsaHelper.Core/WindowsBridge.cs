using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WsaHelper.Core;

public interface IWindowsBridge
{
    Task<MachineStatus> ProbeAsync(CancellationToken ct = default);
    Task<bool> FeaturesAsync(string operation, string id, CancellationToken ct);
    Task RegisterAsync(string directory, CancellationToken ct);
    Task RemoveAsync(string fullName, CancellationToken ct);
}
public sealed class WindowsBridge(IProcessRunner runner) : IWindowsBridge
{
    private static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
    public static string Script(string name)
    {
        using var stream = typeof(WindowsBridge).Assembly.GetManifestResourceStream($"WsaHelper.Core.Scripts.{name}.ps1")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    public Task<ProcessResult> CommandAsync(string command, CancellationToken ct = default) => runner.RunAsync(PowerShell,
        ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding = [Text.UTF8Encoding]::new(); $ErrorActionPreference='Stop'; " + command))], TimeSpan.FromMinutes(15), ct);
    public async Task<MachineStatus> ProbeAsync(CancellationToken ct = default) =>
        JsonSerializer.Deserialize<MachineStatus>((await CommandAsync(Script("Probe"), ct)).RequireSuccess().Trim(), StateStore.Json)
        ?? throw new InvalidDataException("Could not read Windows status.");
    public async Task<bool> FeaturesAsync(string operation, string id, CancellationToken ct)
    {
        if (operation is not ("Enable" or "Restore") || !Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid feature operation.");
        var command = "& { " + Script("Features") + " } -Operation " + Quote(operation) + " -InstallId " + Quote(id);
        var info = new ProcessStartInfo(PowerShell) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Administrator approval is required to configure Windows features.");
        // Once DISM starts it must finish; cancellation is honored before the next setup step.
        await process.WaitForExitAsync(CancellationToken.None);
        if (process.ExitCode is not (0 or 3010)) throw new InvalidOperationException("Windows feature configuration failed. Check DISM logs in Windows\\Logs\\DISM and retry.");
        return process.ExitCode == 3010;
    }
    public async Task RegisterAsync(string directory, CancellationToken ct) =>
        (await CommandAsync("& { " + Script("RegisterWsa") + " } -Directory " + Quote(directory), ct)).RequireSuccess();
    public async Task RemoveAsync(string fullName, CancellationToken ct) =>
        (await CommandAsync("Remove-AppxPackage -Package " + Quote(fullName), ct)).RequireSuccess();
    public static void Open(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
}
