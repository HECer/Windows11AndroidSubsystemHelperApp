using System.Security.Cryptography;
using System.Text.Json;

namespace WsaHelper.Core;

public sealed record InstalledFile(string Path, string Sha256);
public sealed record InstalledFileManifest(int Schema, string InstallId, InstalledFile[] Files);
public static class InstalledFiles
{
    public const string ManifestName = ".wsa-helper-files.json";
    public static void RecordDeployment(string root, string installId, IEnumerable<string> copiedPaths)
    {
        if (!Guid.TryParseExact(installId, "N", out _)) throw new InvalidDataException("Invalid helper installation identity.");
        var manifestFile = System.IO.Path.Combine(root, ManifestName);
        var files = new Dictionary<string, InstalledFile>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(manifestFile))
        {
            var previous = JsonSerializer.Deserialize<InstalledFileManifest>(File.ReadAllText(manifestFile), StateStore.Json);
            if (previous is not { Schema: 1 } || previous.InstallId != installId) throw new InvalidDataException("Helper file ownership does not match this installation.");
            foreach (var entry in previous.Files) { _ = Safety.Within(root, entry.Path); files[entry.Path] = entry; }
        }
        foreach (var relative in copiedPaths)
        {
            var name = relative.Replace('\\', '/');
            if (name is ManifestName or ".wsa-helper-owned") throw new InvalidDataException("Ownership metadata cannot own itself.");
            var fullPath = Safety.Within(root, name);
            using var stream = File.OpenRead(fullPath);
            files[name] = new(name, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
        }
        var temporary = manifestFile + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new InstalledFileManifest(1, installId, files.Values.ToArray()), StateStore.Json));
        File.Move(temporary, manifestFile, true);
    }
}
