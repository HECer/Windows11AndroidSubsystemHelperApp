using System.Net;
using System.Text.Json;
using WsaHelper.Core;
using Xunit;

namespace WsaHelper.Tests;

public sealed class ExistingWsaIntegrationTests
{
    [ExistingWsaFact]
    [Trait("Category", "ExistingWsaIntegration")]
    public async Task SetupAndRemovalPreserveAnExistingWsaWithoutConfiguringWindows()
    {
        var realWindows = new WindowsBridge(new ProcessRunner()); var before = await realWindows.ProbeAsync();
        Assert.NotNull(before.WsaLocation);
        var windows = new PreserveExistingWindows(realWindows, before);
        var root = Path.Combine(Path.GetTempPath(), "WsaHelper-reuse-" + Guid.NewGuid().ToString("N"));
        var store = new StateStore(root); var state = store.Load();
        using var http = new HttpClient(new PlatformToolsOnlyHandler(StateStore.Catalog().PlatformTools.Url));
        var setup = new SetupService(store, windows, new(http), new Progress<string>());
        try
        {
            Assert.True(await setup.InstallAsync(state, default));
            Assert.True(File.Exists(Path.Combine(store.Tools, "adb.exe")));
            Assert.False(state.WsaOwned); Assert.False(state.WsaInstallAttempted); Assert.False(state.FeatureEnableAttempted);
            await setup.RemoveAsync(state, false, default);
            Assert.False(Directory.Exists(store.Tools)); Assert.Equal("Removed", store.Load().Phase);
            var after = await realWindows.ProbeAsync();
            Assert.Equal(before.WsaFullName, after.WsaFullName); Assert.Equal(before.WsaLocation, after.WsaLocation);
            Assert.Equal(before.VmpEnabled, after.VmpEnabled); Assert.False(windows.Mutated);
        }
        finally { OwnedFiles.DeleteTree(root); }
    }
    [ExistingWsaFact]
    [Trait("Category", "ExistingWsaIntegration")]
    public async Task ReusesExistingWsaAndInstallsListsAndRemovesOnlyDisposableApks()
    {
        var fixturesFile = Environment.GetEnvironmentVariable("WSA_HELPER_LIVE_FIXTURES")!;
        using var fixtures = JsonDocument.Parse(File.ReadAllText(fixturesFile));
        var paths = fixtures.RootElement.GetProperty("apks").EnumerateArray().Select(p => Safety.Apk(p.GetString()!)).ToArray();
        Assert.Equal(3, paths.Length);
        var packages = paths.Select(ApkManifest.PackageFromFile).ToArray();
        var runId = fixtures.RootElement.GetProperty("runId").GetString();
        foreach (var package in packages) Assert.StartsWith("io.github.wsahelper.validation." + runId + ".", package);
        Assert.Equal(3, packages.Distinct().Count());
        var runner = new ProcessRunner(); var realWindows = new WindowsBridge(runner);
        var before = await realWindows.ProbeAsync(); Assert.NotNull(before.WsaLocation);
        var windows = new PreserveExistingWindows(realWindows, before);
        var root = Path.Combine(Path.GetTempPath(), "WsaHelper-live-" + Guid.NewGuid().ToString("N"));
        var store = new StateStore(root); var state = store.Load();
        var catalog = StateStore.Catalog();
        using var http = new HttpClient(new PlatformToolsOnlyHandler(catalog.PlatformTools.Url));
        var setup = new SetupService(store, windows, new(http), new Progress<string>());
        AdbClient? adb = null; var attempted = new List<string>();
        IReadOnlyList<AndroidApp>? baseline = null;
        try
        {
            Assert.True(await setup.InstallAsync(state, default));
            Assert.False(state.WsaOwned); Assert.False(state.WsaInstallAttempted); Assert.False(state.FeatureEnableAttempted);
            adb = new(runner, Path.Combine(store.Tools, "adb.exe"), Environment.GetEnvironmentVariable("WSA_HELPER_LIVE_ENDPOINT") ?? "127.0.0.1:58526");
            await adb.WaitUntilReadyAsync(TimeSpan.FromMinutes(2), new Progress<string>()); baseline = await adb.ListAsync();
            Assert.DoesNotContain(baseline, app => packages.Contains(app.PackageName));
            foreach (var (path, package) in paths.Zip(packages))
            {
                attempted.Add(package); await adb.InstallAsync(path);
            }
            // Exercise updating the same signed APK, as Explorer's automatic install does.
            await adb.InstallAsync(paths[0]);
            var installed = await adb.ListAsync();
            Assert.Equal(baseline.Count + 3, installed.Count);
            foreach (var package in packages) Assert.Contains(installed, app => app.PackageName == package);
            await adb.UninstallAsync(ApkManifest.PackageFromFile(paths[0])); attempted.Remove(packages[0]);
            var results = await adb.UninstallManyAsync(packages[1..], new Progress<string>());
            Assert.All(results, result => Assert.True(result.Success, result.Error));
            attempted.Clear();
            Assert.Equal(baseline.Select(a => a.PackageName), (await adb.ListAsync()).Select(a => a.PackageName));
            await adb.StopAsync(); adb = null;
            await setup.RemoveAsync(state, false, default);
            var after = await realWindows.ProbeAsync();
            Assert.Equal(before.WsaFullName, after.WsaFullName); Assert.Equal(before.WsaLocation, after.WsaLocation);
            Assert.Equal(before.VmpEnabled, after.VmpEnabled); Assert.False(windows.Mutated);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixturesFile)!, "existing-wsa-result.json"), JsonSerializer.Serialize(new
            {
                TimestampUtc = DateTimeOffset.UtcNow, Result = "passed", before.WsaFullName,
                OriginalAppCount = baseline.Count, TestApksInstalled = 3, SameApkUpdate = true, UninstallFromApk = true,
                BulkUninstall = true, ExistingAppsPreserved = true, ExistingWsaPreserved = true, WindowsFeaturesChanged = false
            }, StateStore.Json));
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            if (adb is not null)
            {
                foreach (var package in attempted)
                    try
                    {
                        if ((await adb.ListAsync()).Any(app => app.PackageName == package)) await adb.UninstallAsync(package);
                    }
                    catch (Exception ex) { cleanupFailures.Add(new InvalidOperationException("Could not clean up disposable package " + package, ex)); }
                try { await adb.StopAsync(); } catch (Exception ex) { cleanupFailures.Add(ex); }
            }
            if (cleanupFailures.Count > 0) throw new AggregateException("Disposable WSA test cleanup needs attention. Fixture directory and tool state were preserved at " + root, cleanupFailures);
            OwnedFiles.DeleteTree(root);
        }
    }
    private sealed class PreserveExistingWindows(WindowsBridge real, MachineStatus original) : IWindowsBridge
    {
        public bool Mutated { get; private set; }
        public async Task<MachineStatus> ProbeAsync(CancellationToken ct = default)
        {
            var status = await real.ProbeAsync(ct);
            Assert.Equal(original.WsaLocation, status.WsaLocation); return status;
        }
        private Exception Denied() { Mutated = true; return new InvalidOperationException("The existing-WSA smoke test must not change Windows features or AppX registration."); }
        public Task<bool> FeaturesAsync(string operation, string id, CancellationToken ct) => throw Denied();
        public Task RegisterAsync(string directory, CancellationToken ct) => throw Denied();
        public Task RemoveAsync(string fullName, CancellationToken ct) => throw Denied();
    }
    private sealed class PlatformToolsOnlyHandler(string allowedUrl) : HttpClientHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsoluteUri != allowedUrl) throw new InvalidOperationException("Existing WSA test must not download or replace WSA.");
            return base.SendAsync(request, cancellationToken);
        }
    }
}
public sealed class ExistingWsaFactAttribute : FactAttribute
{
    public ExistingWsaFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WSA_HELPER_LIVE_FIXTURES") is null)
            Skip = "Opt-in: requires existing authorized WSA and WSA_HELPER_LIVE_FIXTURES for disposable signed test APKs.";
    }
}
