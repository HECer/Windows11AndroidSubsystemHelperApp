using System.Text.Json;
using System.Text.RegularExpressions;

namespace WsaHelper.Core;

public sealed record Artifact(string Url, string Sha256, long Size);
public sealed record DownloadCatalog(Artifact Wsa, Artifact PlatformTools);
public sealed record AndroidApp(string PackageName, string Version, string Label)
{
    public bool Selected { get; set; }
}
public sealed record MachineStatus(int Build, string Architecture, double MemoryGb, double FreeGb,
    bool Virtualization, bool VmpEnabled, bool RestartPending, string? WsaLocation, string? WsaFullName);
public sealed class InstallState
{
    public int Schema { get; set; } = 1;
    public string InstallId { get; set; } = Guid.NewGuid().ToString("N");
    public string Phase { get; set; } = "New";
    public bool WsaInstallAttempted { get; set; }
    public string? WsaDirectory { get; set; }
    public string? WsaFullName { get; set; }
    public bool WsaOwned { get; set; }
    public bool AssociationRegistered { get; set; }
    public bool FeatureEnableAttempted { get; set; }
    public bool VmpWasEnabled { get; set; }
    public string[] PendingApks { get; set; } = [];
    public string[] StagingDirectories { get; set; } = [];
    public string Endpoint { get; set; } = "127.0.0.1:58526";
}
public static partial class Safety
{
    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9_]*(?:\.[a-zA-Z0-9_]+)+$")]
    private static partial Regex PackagePattern();
    public static string Package(string value) => PackagePattern().IsMatch(value)
        ? value : throw new InvalidDataException("Invalid Android package identifier.");
    public static string Endpoint(string value)
    {
        var parts = value.Split(':');
        if (parts.Length != 2 || parts[0] != "127.0.0.1" || !int.TryParse(parts[1], out var port) || port is < 1024 or > 65535)
            throw new InvalidDataException("Use a loopback WSA endpoint such as 127.0.0.1:58526. Remote devices are not supported.");
        return value;
    }
    public static string Within(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException("Invalid archive path.");
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive path leaves its destination.");
        return target;
    }
    public static bool SamePath(string? left, string? right) => left is not null && right is not null &&
        Path.GetFullPath(left).TrimEnd('\\', '/').Equals(Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    public static string Apk(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path) || !path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose an existing .apk file. Split APK bundles are not supported.");
        return path;
    }
}
public sealed class StateStore(string root)
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public string Root { get; } = Path.GetFullPath(root);
    public string StateFile => Path.Combine(Root, "state.json");
    public string Tools => Path.Combine(Root, "platform-tools");
    public string Cache => Path.Combine(Root, "cache");
    public InstallState Load() => File.Exists(StateFile)
        ? JsonSerializer.Deserialize<InstallState>(File.ReadAllText(StateFile), Json) is { Schema: 1 } state
            ? state : throw new InvalidDataException("Unsupported or damaged setup journal. Preserve it for recovery.")
        : new();
    public void Save(InstallState state)
    {
        Directory.CreateDirectory(Root);
        var temporary = StateFile + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, Json));
        File.Move(temporary, StateFile, true);
    }
    public static DownloadCatalog Catalog()
    {
        using var stream = typeof(StateStore).Assembly.GetManifestResourceStream("WsaHelper.Core.catalog.json")!;
        return JsonSerializer.Deserialize<DownloadCatalog>(stream, Json)!;
    }
}
