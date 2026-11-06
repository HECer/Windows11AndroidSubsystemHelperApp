using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Security.Principal;

namespace WsaHelper.Core;

public interface IWindowsBridge
{
    Task<MachineStatus> ProbeAsync(CancellationToken ct = default);
    Task<bool> FeaturesAsync(string operation, string id, CancellationToken ct);
    string DeploymentDirectory(string installId);
    Task InstallWsaAsync(string archive, string installId, CancellationToken ct);
    Task RemoveWsaAsync(string installId, CancellationToken ct);
}
public sealed class WindowsBridge(IProcessRunner runner) : IWindowsBridge
{
    private static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    private const string Prelude = "$env:PSModulePath=[IO.Path]::Combine($PSHOME,'Modules'); $ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Stop'; ";
    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
    public static string Script(string name)
    {
        using var stream = typeof(WindowsBridge).Assembly.GetManifestResourceStream($"WsaHelper.Core.Scripts.{name}.ps1")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    public Task<ProcessResult> CommandAsync(string command, CancellationToken ct = default) => runner.RunAsync(PowerShell,
        ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding = [Text.UTF8Encoding]::new(); " + Prelude + command))], TimeSpan.FromMinutes(15), ct);
    public async Task<MachineStatus> ProbeAsync(CancellationToken ct = default) =>
        JsonSerializer.Deserialize<MachineStatus>((await CommandAsync(Script("Probe"), ct)).RequireSuccess().Trim(), StateStore.Json)
        ?? throw new InvalidDataException("Could not read Windows status.");
    public async Task<bool> FeaturesAsync(string operation, string id, CancellationToken ct)
    {
        if (operation is not ("Enable" or "Restore") || !Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid feature operation.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent();
        var command = Prelude + "& { " + Script("Features") + " } -Operation " + Quote(operation) + " -InstallId " + Quote(id) + " -CallerSid " + Quote(identity.User!.Value);
        var info = new ProcessStartInfo(PowerShell) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Administrator approval is required to configure Windows features.");
        // Once DISM starts it must finish; cancellation is honored before the next setup step.
        await process.WaitForExitAsync(CancellationToken.None);
        if (process.ExitCode == 5) throw new InvalidOperationException("Administrator approval used a different Windows account. Fresh setup must be started and approved by the same administrator account. No feature settings were changed.");
        if (process.ExitCode is not (0 or 3010)) throw new InvalidOperationException("Windows feature configuration failed. Check DISM logs in Windows\\Logs\\DISM and retry.");
        return process.ExitCode == 3010;
    }
    public string DeploymentDirectory(string installId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return Path.Combine(MachineWsaDeployment.DirectoryFor(installId), "Runtime");
    }
    public Task InstallWsaAsync(string archive, string installId, CancellationToken ct) => DeployAsync("install", archive, installId, ct);
    public Task RemoveWsaAsync(string installId, CancellationToken ct) => DeployAsync("remove", null, installId, ct);
    private static async Task DeployAsync(string operation, string? archive, string installId, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        _ = DeploymentPolicy.InstallId(installId); ct.ThrowIfCancellationRequested();
        using var identity = WindowsIdentity.GetCurrent();
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the deployment helper.");
        if (Path.GetFileName(executable) is not ("WsaHelper.exe" or "WsaSetup.exe" or "WsaRemove.exe"))
            throw new InvalidOperationException("Use the published Windows executable for fresh WSA deployment.");
        var info = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "--machine-wsa", operation, installId, identity.User!.Value }) info.ArgumentList.Add(arg);
        if (archive is not null) info.ArgumentList.Add(Path.GetFullPath(archive));
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Administrator approval is required to deploy WSA.");
        // Interrupting AppX deployment can damage registration. Finish this atomic step first.
        await process.WaitForExitAsync(CancellationToken.None);
        if (process.ExitCode != 0) throw new InvalidOperationException("Elevated WSA deployment did not finish. See its error message, then retry setup or removal; protected runtime files were retained for recovery.");
    }
    public static void Open(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
}
