namespace WsaHelper.Core;

public sealed record AppOperationResult(string PackageName, bool Success, string? Error = null);
public sealed class AdbClient(IProcessRunner runner, string executable, string endpoint)
{
    public const string ServerPort = "5039";
    private readonly string target = Safety.Endpoint(endpoint);
    private Task<ProcessResult> Run(IEnumerable<string> args, CancellationToken ct) => runner.RunAsync(executable,
        new[] { "-P", ServerPort }.Concat(args), TimeSpan.FromMinutes(5), ct);
    private Task<ProcessResult> Device(IEnumerable<string> args, CancellationToken ct) => Run(new[] { "-s", target }.Concat(args), ct);
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        // ADB's default listener is localhost-only. Do not use -a (all interfaces).
        (await Run(["start-server"], ct)).RequireSuccess();
        var connection = (await Run(["connect", target], ct)).RequireSuccess();
        var devices = (await Run(["devices"], ct)).RequireSuccess();
        var line = devices.Split('\n').FirstOrDefault(l => l.StartsWith(target + "\t", StringComparison.Ordinal));
        if (line is null || line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) != "device")
            throw new InvalidOperationException("WSA is not ready for APK management. Open WSA settings, enable Developer mode, start the subsystem and approve its Android debugging prompt. Then retry.\n" + connection + "\n" + line);
    }
    public async Task InstallAsync(string path, CancellationToken ct = default)
    {
        var output = (await Device(["install", "-r", Safety.Apk(path)], ct)).RequireSuccess();
        if (!output.Split('\n').Any(l => l.Trim() == "Success")) throw new InvalidOperationException("Android rejected the APK: " + output);
    }
    public async Task UninstallAsync(string package, CancellationToken ct = default)
    {
        var output = (await Device(["uninstall", Safety.Package(package)], ct)).RequireSuccess();
        if (!output.Split('\n').Any(l => l.Trim() == "Success")) throw new InvalidOperationException("Android could not uninstall the app: " + output);
    }
    public async Task<IReadOnlyList<AppOperationResult>> UninstallManyAsync(IEnumerable<string> packages, IProgress<string> progress, CancellationToken ct = default)
    {
        var validated = packages.Select(Safety.Package).Distinct(StringComparer.Ordinal).ToArray();
        var results = new List<AppOperationResult>();
        foreach (var package in validated)
        {
            ct.ThrowIfCancellationRequested(); progress.Report("Uninstalling " + package);
            try
            {
                await UninstallAsync(package, ct); results.Add(new(package, true)); progress.Report("Uninstalled " + package);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new(package, false, ex.Message)); progress.Report(package + ": " + ex.Message);
            }
        }
        return results;
    }
    public async Task<IReadOnlyList<AndroidApp>> ListAsync(CancellationToken ct = default)
    {
        var output = (await Device(["shell", "pm", "list", "packages", "-3", "--user", "0"], ct)).RequireSuccess();
        return output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("package:", StringComparison.Ordinal))
            .Select(l => Safety.Package(l[8..])).Distinct().Order(StringComparer.OrdinalIgnoreCase)
            .Select(p => new AndroidApp(p, "", p)).ToArray();
    }
    public async Task StopAsync()
    {
        if (File.Exists(executable)) (await Run(["kill-server"], CancellationToken.None)).RequireSuccess();
    }
}
