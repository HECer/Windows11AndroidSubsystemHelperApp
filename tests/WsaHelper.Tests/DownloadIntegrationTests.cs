using System.Diagnostics;
using System.Xml.Linq;
using WsaHelper.Core;
using Xunit;

namespace WsaHelper.Tests;

public sealed class DownloadIntegrationTests
{
    [DownloadIntegrationFact]
    [Trait("Category", "DownloadIntegration")]
    public async Task PinnedArchivesExtractAndContainExpectedComponents()
    {
        var directory = Environment.GetEnvironmentVariable("WSA_HELPER_VALIDATION_ARCHIVES") ?? throw new InvalidOperationException("Set the validation archive directory.");
        var root = Path.Combine(directory, "WsaHelper-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = StateStore.Catalog(); var wsa = Path.Combine(directory, "wsa.7z"); var tools = Path.Combine(directory, "platform-tools.zip");
            Assert.True(await VerifiedDownloads.VerifyAsync(wsa, catalog.Wsa)); Assert.True(await VerifiedDownloads.VerifyAsync(tools, catalog.PlatformTools));
            var wsaOut = Path.Combine(root, "wsa"); VerifiedDownloads.Extract(wsa, wsaOut);
            var manifest = Directory.GetFiles(wsaOut, "AppxManifest.xml", SearchOption.AllDirectories).Single();
            var xml = XDocument.Load(manifest); XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
            Assert.Equal("MicrosoftCorporationII.WindowsSubsystemForAndroid", xml.Root!.Element(ns + "Identity")!.Attribute("Name")!.Value);
            foreach (var dependency in xml.Descendants(ns + "PackageDependency"))
                Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(manifest)!, dependency.Attribute("Name")!.Value + "_x64.appx")));
            Assert.Contains(xml.Descendants(ns + "Application"), app => app.Attribute("Id")?.Value == "SettingsApp");
            var toolsOut = Path.Combine(root, "tools"); VerifiedDownloads.Extract(tools, toolsOut);
            var executable = Path.Combine(toolsOut, "platform-tools", "adb.exe");
            var runner = new ProcessRunner(); var version = (await runner.RunAsync(executable, ["version"], TimeSpan.FromSeconds(20))).RequireSuccess();
            Assert.Contains("37.0.2", version);
            try
            {
                (await runner.RunAsync(executable, ["-P", "51539", "start-server"], TimeSpan.FromSeconds(20))).RequireSuccess();
                var devices = (await runner.RunAsync(executable, ["-P", "51539", "devices"], TimeSpan.FromSeconds(20))).RequireSuccess();
                Assert.Contains("List of devices", devices);
            }
            finally { (await runner.RunAsync(executable, ["-P", "51539", "kill-server"], TimeSpan.FromSeconds(20))).RequireSuccess(); }
        }
        finally { OwnedFiles.DeleteTree(root); }
    }
}
public sealed class DownloadIntegrationFactAttribute : FactAttribute
{
    public DownloadIntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WSA_HELPER_VALIDATION_ARCHIVES") is null)
            Skip = "Opt-in: set WSA_HELPER_VALIDATION_ARCHIVES to the pinned archives directory.";
    }
}
