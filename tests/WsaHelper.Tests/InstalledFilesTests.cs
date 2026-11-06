using System.Text.Json;
using WsaHelper.Core;
using Xunit;

namespace WsaHelper.Tests;

public sealed class InstalledFilesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WsaHelper-file-ownership-" + Guid.NewGuid().ToString("N"));
    private readonly string id = Guid.NewGuid().ToString("N");
    public InstalledFilesTests() => Directory.CreateDirectory(root);
    public void Dispose() => OwnedFiles.DeleteTree(root);
    private string App => Path.Combine(root, "App");
    private void Initialize()
    {
        Directory.CreateDirectory(Path.Combine(App, "licenses"));
        File.WriteAllText(Path.Combine(App, ".wsa-helper-owned"), id);
        File.WriteAllText(Path.Combine(App, "helper.exe"), "test-only helper bytes");
        File.WriteAllText(Path.Combine(App, "licenses", "notice.txt"), "test notice");
        InstalledFiles.RecordDeployment(App, id, ["helper.exe", "licenses/notice.txt"]);
    }
    private Task<ProcessResult> Cleanup() => new WindowsBridge(new ProcessRunner()).CommandAsync("& { " + WindowsBridge.Script("CleanupHelper") + " } -Directory " + WindowsBridge.Quote(App) + " -InstallId " + WindowsBridge.Quote(id));
    [Fact]
    public async Task RemovesRecordedUnchangedFilesButPreservesUserAddedFiles()
    {
        Initialize(); File.WriteAllText(Path.Combine(App, "user-notes.txt"), "keep me");
        (await Cleanup()).RequireSuccess();
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(App, "user-notes.txt")));
        Assert.Equal(new[] { "user-notes.txt" }, Directory.GetFiles(App).Select(Path.GetFileName)); Assert.False(Directory.Exists(Path.Combine(App, "licenses")));
    }
    [Fact]
    public async Task PreservesModifiedOwnedFilesAndTheirOwnershipRecord()
    {
        Initialize(); File.WriteAllText(Path.Combine(App, "helper.exe"), "user modified this file");
        var output = (await Cleanup()).RequireSuccess();
        Assert.Contains("helper.exe", output); Assert.True(File.Exists(Path.Combine(App, InstalledFiles.ManifestName)));
        Assert.Equal("user modified this file", File.ReadAllText(Path.Combine(App, "helper.exe")));
    }
    [Fact]
    public async Task RemovesAnEmptyHelperDirectoryAfterDeletingOwnedFiles()
    {
        Initialize(); (await Cleanup()).RequireSuccess(); Assert.False(Directory.Exists(App));
    }
    [Fact]
    public async Task RejectsAnEscapingManifestBeforeDeletingAnyFile()
    {
        Initialize(); var outside = Path.Combine(root, "unrelated.txt"); File.WriteAllText(outside, "preserve outside");
        var file = Path.Combine(App, InstalledFiles.ManifestName);
        var original = JsonSerializer.Deserialize<InstalledFileManifest>(File.ReadAllText(file), StateStore.Json)!;
        var invalid = original with { Files = [.. original.Files, new("../unrelated.txt", new string('0', 64))] };
        File.WriteAllText(file, JsonSerializer.Serialize(invalid, StateStore.Json));
        Assert.NotEqual(0, (await Cleanup()).ExitCode); Assert.True(File.Exists(Path.Combine(App, "helper.exe"))); Assert.Equal("preserve outside", File.ReadAllText(outside));
    }
    [Fact]
    public void UpdatingOtherFilesDoesNotClaimAnOldModifiedFile()
    {
        Initialize(); File.WriteAllText(Path.Combine(App, "helper.exe"), "modified");
        var before = JsonSerializer.Deserialize<InstalledFileManifest>(File.ReadAllText(Path.Combine(App, InstalledFiles.ManifestName)), StateStore.Json)!;
        File.WriteAllText(Path.Combine(App, "new.dll"), "new owned file"); InstalledFiles.RecordDeployment(App, id, ["new.dll"]);
        var after = JsonSerializer.Deserialize<InstalledFileManifest>(File.ReadAllText(Path.Combine(App, InstalledFiles.ManifestName)), StateStore.Json)!;
        Assert.Equal(before.Files.Single(f => f.Path == "helper.exe").Sha256, after.Files.Single(f => f.Path == "helper.exe").Sha256);
    }
}
