using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using WsaHelper.Core;
using Xunit;

namespace WsaHelper.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WsaHelper-tests-" + Guid.NewGuid().ToString("N"));
    public CoreTests() => Directory.CreateDirectory(root);
    public void Dispose() => OwnedFiles.DeleteTree(root);
    [Theory]
    [InlineData("../escape")][InlineData("..\\escape")][InlineData("/absolute")][InlineData("C:\\escape")][InlineData("file:stream")][InlineData("")]
    public void RejectsArchiveEscape(string name) => Assert.Throws<InvalidDataException>(() => Safety.Within(root, name));
    [Theory]
    [InlineData("com.example.app;rm")][InlineData("-r")][InlineData("com.foo bar")][InlineData("com.foo\nbar")][InlineData("$(test)")]
    public void RejectsShellPackageInjection(string package) => Assert.Throws<InvalidDataException>(() => Safety.Package(package));
    [Theory]
    [InlineData("192.168.1.1:5555")][InlineData("emulator-5554")][InlineData("127.0.0.1:22")][InlineData("localhost:58526")][InlineData("127.0.0.1:70000")]
    public void RefusesOtherDevices(string endpoint) => Assert.Throws<InvalidDataException>(() => Safety.Endpoint(endpoint));
    [Fact]
    public void PinnedCatalogIsComplete()
    {
        var catalog = StateStore.Catalog(); VerifiedDownloads.ValidateArtifact(catalog.Wsa); VerifiedDownloads.ValidateArtifact(catalog.PlatformTools);
        Assert.Contains("NoGApps-NoAmazon", catalog.Wsa.Url); Assert.Contains("37.0.2", catalog.PlatformTools.Url);
    }
    [Fact]
    public void JournalIsDurableAndRecoverable()
    {
        var store = new StateStore(root); var state = store.Load(); state.WsaInstallAttempted = true; state.Phase = "Registering"; store.Save(state);
        Assert.Equal(state.InstallId, store.Load().InstallId); Assert.True(store.Load().WsaInstallAttempted); Assert.False(File.Exists(store.StateFile + ".new"));
        File.WriteAllText(store.StateFile, "{\"schema\":99}"); Assert.Throws<InvalidDataException>(() => store.Load());
    }
    [Fact]
    public void ZipTraversalNeverWritesOutsideRoot()
    {
        var archive = Path.Combine(root, "bad.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) using (var writer = new StreamWriter(zip.CreateEntry("../escaped.txt").Open())) writer.Write("bad");
        Assert.Throws<InvalidDataException>(() => VerifiedDownloads.Extract(archive, Path.Combine(root, "out")));
        Assert.False(File.Exists(Path.Combine(root, "escaped.txt")));
    }
    [Fact]
    public void ZipSymlinkIsRejected()
    {
        var archive = Path.Combine(root, "link.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) { var entry = zip.CreateEntry("link"); entry.ExternalAttributes = unchecked((int)0xa1ff0000); }
        Assert.Throws<InvalidDataException>(() => VerifiedDownloads.Extract(archive, Path.Combine(root, "out")));
    }
    [Fact]
    public async Task CorruptedDownloadIsRejectedAndPartialCleaned()
    {
        var handler = new Handler([1, 2, 3]); using var http = new HttpClient(handler);
        var artifact = new Artifact("https://dl.google.com/android/repository/test.zip", new string('0', 64), 3);
        await Assert.ThrowsAsync<InvalidDataException>(() => new VerifiedDownloads(http).GetAsync(artifact, root, new Progress<string>(), default));
        Assert.Empty(Directory.GetFiles(root));
    }
    [Fact]
    public async Task CorrectDownloadIsCachedButTamperedCacheIsReplaced()
    {
        byte[] bytes = [1, 2, 3]; var handler = new Handler(bytes); using var http = new HttpClient(handler);
        var artifact = new Artifact("https://dl.google.com/android/repository/test.zip", Convert.ToHexString(SHA256.HashData(bytes)), 3);
        var download = new VerifiedDownloads(http);
        var file = await download.GetAsync(artifact, root, new Progress<string>(), default);
        await download.GetAsync(artifact, root, new Progress<string>(), default); Assert.Equal(1, handler.Calls);
        File.WriteAllBytes(file, [3, 2, 1]); await download.GetAsync(artifact, root, new Progress<string>(), default); Assert.Equal(2, handler.Calls);
    }
    [Fact]
    public async Task AdbNeverTargetsAnUnspecifiedDeviceAndDoesNotTrustExitCodeAlone()
    {
        var runner = new FakeRunner(new(0, "Failure [INSTALL_FAILED_INVALID_APK]", ""));
        var client = new AdbClient(runner, "adb.exe", "127.0.0.1:58526");
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.UninstallAsync("com.example.app"));
        Assert.Equal(new[] { "-P", "5039", "-s", "127.0.0.1:58526", "uninstall", "com.example.app" }, runner.Arguments);
    }
    [Fact]
    public async Task UnauthorizedAdbDeviceExplainsRequiredApproval()
    {
        var runner = new FakeRunner(new(0, "127.0.0.1:58526\tunauthorized\n", ""));
        var ex = await Assert.ThrowsAsync<AdbNotReadyException>(() => new AdbClient(runner, "adb.exe", "127.0.0.1:58526").ConnectAsync());
        Assert.Contains("approve", ex.Message);
    }
    [Fact]
    public async Task AppListIncludesOnlyValidPackages()
    {
        var runner = new FakeRunner(new(0, "package:com.example.b\npackage:com.example.a\npackage:com.example.b\n", ""));
        var apps = await new AdbClient(runner, "adb.exe", "127.0.0.1:58526").ListAsync();
        Assert.Equal(new[] { "com.example.a", "com.example.b" }, apps.Select(a => a.PackageName));
    }
    [Fact]
    public void ReadsTextManifestAndRefusesXmlEntities()
    {
        Assert.Equal("com.example.app", ApkManifest.ReadPackage(Encoding.UTF8.GetBytes("<manifest package=\"com.example.app\"/>")));
        Assert.ThrowsAny<Exception>(() => ApkManifest.ReadPackage(Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///secret'>]><manifest package=\"&e;\"/>")));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void ReadsBinaryManifestStringPools(bool utf8)
    {
        Assert.Equal("com.example.app", ApkManifest.ReadPackage(BinaryManifest(utf8)));
    }
    [Fact]
    public void TruncatedBinaryManifestsFailClosed()
    {
        var valid = BinaryManifest(true);
        for (var i = 0; i < valid.Length; i++) Assert.ThrowsAny<Exception>(() => ApkManifest.ReadPackage(valid[..i]));
    }
    [Fact]
    public async Task RemovalPreservesExistingWsaAndSharedFeatures()
    {
        var store = new StateStore(root); var state = store.Load(); var windows = new FakeWindows("C:\\ExistingWSA");
        using var http = new HttpClient();
        await new SetupService(store, windows, new(http), new Progress<string>()).RemoveAsync(state, false, default);
        Assert.False(windows.Removed); Assert.False(windows.FeaturesChanged); Assert.Equal("Removed", store.Load().Phase);
    }
    [Fact]
    public async Task PartialRegistrationIsRemovedOnlyWhenLocationMatchesJournal()
    {
        var store = new StateStore(root); var state = store.Load();
        var windows = new FakeWindows(null) { DeploymentRoot = Path.Combine(root, "protected") };
        state.WsaInstallAttempted = true; state.WsaDirectory = windows.DeploymentDirectory(state.InstallId); Directory.CreateDirectory(state.WsaDirectory);
        var expected = state.WsaDirectory;
        windows.Location = state.WsaDirectory; using var http = new HttpClient();
        await new SetupService(store, windows, new(http), new Progress<string>()).RemoveAsync(state, false, default);
        Assert.True(windows.Removed); Assert.False(Directory.Exists(expected));
    }
    [Fact]
    public async Task RemovalDoesNotTrustTamperedOwnershipPath()
    {
        var store = new StateStore(root); var state = store.Load(); state.WsaInstallAttempted = true; state.WsaOwned = true; state.WsaDirectory = Path.Combine(root, "unrelated"); Directory.CreateDirectory(state.WsaDirectory);
        var windows = new FakeWindows(state.WsaDirectory); using var http = new HttpClient();
        store.Save(state);
        await Assert.ThrowsAsync<InvalidDataException>(() => new SetupService(store, windows, new(http), new Progress<string>()).RemoveAsync(state, false, default));
        Assert.False(windows.Removed); Assert.True(Directory.Exists(Path.Combine(root, "unrelated"))); Assert.True(store.Load().WsaInstallAttempted);
    }
    [Fact]
    public async Task SetupStopsBeforeMutationWhenFirmwareIsDisabled()
    {
        var store = new StateStore(root); var windows = new FakeWindows(null) { Virtualization = false }; using var http = new HttpClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SetupService(store, windows, new(http), new Progress<string>()).InstallAsync(store.Load(), default));
        Assert.False(windows.FeaturesChanged);
    }
    [Fact]
    public async Task RebootBoundaryIsJournaledBeforeDownloadingWsa()
    {
        var store = new StateStore(root); var windows = new FakeWindows(null) { NeedsRestart = true }; using var http = new HttpClient(new Handler([]));
        Assert.False(await new SetupService(store, windows, new(http), new Progress<string>()).InstallAsync(store.Load(), default));
        Assert.Equal("RestartRequired", store.Load().Phase); Assert.True(store.Load().FeatureEnableAttempted); Assert.False(store.Load().WsaInstallAttempted);
    }
    [Fact]
    public async Task ExplicitRestoreUsesRecordedMachineChanges()
    {
        var store = new StateStore(root); var state = store.Load(); state.FeatureEnableAttempted = true;
        var windows = new FakeWindows(null); using var http = new HttpClient();
        await new SetupService(store, windows, new(http), new Progress<string>()).RemoveAsync(state, true, default);
        Assert.True(windows.FeaturesChanged); Assert.False(store.Load().FeatureEnableAttempted);
    }
    [Fact]
    public async Task BulkUninstallContinuesAfterFailureAndDeduplicatesPackages()
    {
        var runner = new SelectiveRunner(arguments => arguments[^1] == "com.example.fail" ? new(0, "Failure [DELETE_FAILED_INTERNAL_ERROR]", "") : new(0, "Success", ""));
        var results = await new AdbClient(runner, "adb.exe", "127.0.0.1:58526").UninstallManyAsync(["com.example.fail", "com.example.ok", "com.example.ok"], new Progress<string>());
        Assert.Collection(results, failure => Assert.False(failure.Success), success => Assert.True(success.Success));
        Assert.Equal(2, runner.Calls.Count);
        Assert.All(runner.Calls, args => Assert.Equal(new[] { "-P", "5039", "-s", "127.0.0.1:58526", "uninstall" }, args[..^1]));
    }
    [Fact]
    public async Task BulkUninstallValidatesTheEntireSelectionBeforeChangingAnything()
    {
        var runner = new SelectiveRunner(_ => new(0, "Success", ""));
        await Assert.ThrowsAsync<InvalidDataException>(() => new AdbClient(runner, "adb.exe", "127.0.0.1:58526").UninstallManyAsync(["com.example.ok", "com.example.app;bad"], new Progress<string>()));
        Assert.Empty(runner.Calls);
    }
    [Fact]
    public async Task BulkUninstallHonorsCancellationBetweenApps()
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new SelectiveRunner(_ => { cancellation.Cancel(); return new(0, "Success", ""); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AdbClient(runner, "adb.exe", "127.0.0.1:58526").UninstallManyAsync(["com.example.one", "com.example.two"], new Progress<string>(), cancellation.Token));
        Assert.Single(runner.Calls);
    }
    [Fact]
    public async Task ReadinessWaitContinuesAutomaticallyAfterApproval()
    {
        var polls = 0;
        var runner = new SelectiveRunner(args => new(0, args[^1] == "devices" ? "127.0.0.1:58526\t" + (++polls == 1 ? "unauthorized" : "device") : "", ""));
        await new AdbClient(runner, "adb.exe", "127.0.0.1:58526").WaitUntilReadyAsync(TimeSpan.FromSeconds(10), new Progress<string>());
        Assert.Equal(2, polls); Assert.DoesNotContain(runner.Calls, args => args.Contains("install"));
    }
    [Fact]
    public async Task ReadinessWaitHasAFiniteDeadlineAndDoesNotInstallAnything()
    {
        var runner = new SelectiveRunner(_ => new(0, "127.0.0.1:58526\tunauthorized", ""));
        var error = await Assert.ThrowsAsync<TimeoutException>(() => new AdbClient(runner, "adb.exe", "127.0.0.1:58526").WaitUntilReadyAsync(TimeSpan.FromMilliseconds(100), new Progress<string>()));
        Assert.Contains("approve", error.Message); Assert.DoesNotContain(runner.Calls, args => args.Contains("install"));
    }
    [Fact]
    public async Task CancelledReadinessWaitDoesNotStartAdb()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var runner = new SelectiveRunner(_ => new(0, "", ""));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AdbClient(runner, "adb.exe", "127.0.0.1:58526").WaitUntilReadyAsync(TimeSpan.FromMinutes(1), new Progress<string>(), cancelled.Token));
        Assert.Empty(runner.Calls);
    }
    private static byte[] BinaryManifest(bool utf8)
    {
        string[] strings = ["manifest", "package", "com.example.app"];
        using var poolBytes = new MemoryStream(); using var values = new BinaryWriter(poolBytes); var offsets = new List<int>();
        foreach (var s in strings) { offsets.Add((int)poolBytes.Length); if (utf8) { values.Write((byte)s.Length); values.Write((byte)s.Length); values.Write(Encoding.UTF8.GetBytes(s)); values.Write((byte)0); } else { values.Write((ushort)s.Length); values.Write(Encoding.Unicode.GetBytes(s)); values.Write((ushort)0); } }
        while (poolBytes.Length % 4 != 0) values.Write((byte)0);
        using var result = new MemoryStream(); using var w = new BinaryWriter(result);
        var poolSize = 28 + 12 + (int)poolBytes.Length; w.Write((ushort)3); w.Write((ushort)8); w.Write(8 + poolSize + 56);
        w.Write((ushort)1); w.Write((ushort)28); w.Write(poolSize); w.Write(3); w.Write(0); w.Write(utf8 ? 256 : 0); w.Write(40); w.Write(0);
        foreach (var o in offsets) w.Write(o); w.Write(poolBytes.ToArray());
        w.Write((ushort)0x102); w.Write((ushort)16); w.Write(56); w.Write(1); w.Write(-1); w.Write(-1); w.Write(0); w.Write((ushort)20); w.Write((ushort)20); w.Write((ushort)1); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0);
        w.Write(-1); w.Write(1); w.Write(2); w.Write((ushort)8); w.Write((byte)0); w.Write((byte)3); w.Write(2); return result.ToArray();
    }
    private sealed class Handler(byte[] bytes) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request }); }
    }
    private sealed class FakeRunner(ProcessResult result) : IProcessRunner
    {
        public string[] Arguments = [];
        public Task<ProcessResult> RunAsync(string file, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default) { Arguments = arguments.ToArray(); return Task.FromResult(result); }
    }
    private sealed class SelectiveRunner(Func<string[], ProcessResult> response) : IProcessRunner
    {
        public List<string[]> Calls { get; } = [];
        public Task<ProcessResult> RunAsync(string file, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var args = arguments.ToArray(); Calls.Add(args); return Task.FromResult(response(args));
        }
    }
    private sealed class FakeWindows(string? location) : IWindowsBridge
    {
        public string? Location = location;
        public string DeploymentRoot = Path.Combine(Path.GetTempPath(), "WsaHelper-fake-protected");
        public bool Removed; public bool FeaturesChanged; public bool Virtualization = true; public bool NeedsRestart;
        public Task<MachineStatus> ProbeAsync(CancellationToken ct = default) => Task.FromResult(new MachineStatus(22631, "X64", 16, 40, Virtualization, true, false, Removed ? null : Location, Removed || Location is null ? null : "WSA_full_name"));
        public Task<bool> FeaturesAsync(string operation, string id, CancellationToken ct) { FeaturesChanged = true; return Task.FromResult(NeedsRestart); }
        public string DeploymentDirectory(string installId) => Safety.Within(DeploymentRoot, DeploymentPolicy.InstallId(installId) + "/Runtime");
        public Task InstallWsaAsync(string archive, string installId, CancellationToken ct) => Task.CompletedTask;
        public Task RemoveWsaAsync(string installId, CancellationToken ct) { Removed = true; if (Location is not null && Directory.Exists(Location)) OwnedFiles.DeleteTree(Location); return Task.CompletedTask; }
    }
}
