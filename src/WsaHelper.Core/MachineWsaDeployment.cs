using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace WsaHelper.Core;

// Runs only in the narrowly scoped elevated child. No per-user journal is trusted here.
[SupportedOSPlatform("windows")]
public static class MachineWsaDeployment
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private const string MarkerName = "deployment.json";
    private sealed record Journal(int Schema, string InstallId, string OwnerSid, string ArchiveSha256, string Phase);
    public static string DirectoryFor(string installId) => DeploymentPolicy.DirectoryFor(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), installId);

    public static void Run(string operation, string installId, string callerSid, string? sourceArchive)
    {
        using var identity = WindowsIdentity.GetCurrent();
        DeploymentPolicy.RequireIdentity(callerSid, identity.User?.Value ?? "", new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
        _ = DeploymentPolicy.InstallId(installId);
        if (operation is not ("install" or "remove") || (operation == "install") != (sourceArchive is not null))
            throw new ArgumentException("Invalid elevated deployment request.");
        using var mutex = new Mutex(false, @"Global\WsaHelper-WsaDeployment");
        var acquired = false;
        try { acquired = mutex.WaitOne(TimeSpan.Zero); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new InvalidOperationException("Another WSA deployment is running. Wait for it to finish before retrying.");
        // A named mutex is thread-affine. Keep ownership on this dedicated worker's
        // entry thread while asynchronous copy/deployment work runs to completion.
        try { DeployAsync(operation, installId, callerSid, sourceArchive).GetAwaiter().GetResult(); }
        finally { mutex.ReleaseMutex(); }
    }

    private static async Task DeployAsync(string operation, string installId, string callerSid, string? sourceArchive)
    {
        var directory = DirectoryFor(installId);
        var runtime = Path.Combine(directory, "Runtime");
        var marker = Path.Combine(directory, MarkerName);
        var windows = new WindowsBridge(new ProcessRunner());
        var catalog = StateStore.Catalog();
        if (operation == "install" && !Directory.Exists(runtime) && new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace < 12L * 1024 * 1024 * 1024)
            throw new IOException("Fresh WSA deployment requires 12 GB free on the Program Files drive.");
        // Check for another account's package as well as the current account before staging.
        async Task RejectOtherRegistrations() => (await windows.CommandAsync(
            "$packages=@(Get-AppxPackage -AllUsers -Name 'MicrosoftCorporationII.WindowsSubsystemForAndroid'); " +
            "foreach($p in $packages) { if ([IO.Path]::GetFullPath($p.InstallLocation).TrimEnd('\\') -ine " + WindowsBridge.Quote(runtime.TrimEnd('\\')) + ") { throw 'Another WSA installation exists. It was preserved.' } }" )).RequireSuccess();

        if (!Directory.Exists(directory) && operation == "remove") return;
        if (operation == "install") await RejectOtherRegistrations();
        var parents = new[] { Path.GetDirectoryName(Path.GetDirectoryName(directory))!, Path.GetDirectoryName(directory)! };
        foreach (var parent in parents) EnsureProtectedDirectory(parent);
        Journal journal;
        if (Directory.Exists(directory))
        {
            AssertProtectedDirectory(directory);
            AssertNoLinks(directory);
            journal = File.Exists(marker) ? JsonSerializer.Deserialize<Journal>(File.ReadAllText(marker), StateStore.Json)
                ?? throw new InvalidDataException("Unreadable protected WSA deployment journal.")
                : throw new InvalidDataException("Missing protected WSA ownership journal. Files were preserved.");
            if (journal.Schema != 1 || journal.InstallId != installId || journal.OwnerSid != callerSid || journal.ArchiveSha256 != catalog.Wsa.Sha256)
                throw new InvalidDataException("Protected WSA ownership does not match this request. Files were preserved.");
        }
        else
        {
            EnsureProtectedDirectory(directory);
            journal = new(1, installId, callerSid, catalog.Wsa.Sha256, "Staging");
            Save();
        }
        void Save()
        {
            File.WriteAllText(marker + ".new", JsonSerializer.Serialize(journal, StateStore.Json));
            File.Move(marker + ".new", marker, true);
        }
        if (operation == "install")
        {
            if (!Directory.Exists(runtime))
            {
                // Copy first, then hash the protected copy. Never execute a manifest from AppData.
                var archive = Path.Combine(directory, "wsa.7z");
                var stage = Path.Combine(directory, "Extraction");
                if (File.Exists(archive)) File.Delete(archive);
                if (Directory.Exists(stage)) OwnedFiles.DeleteTree(stage);
                try
                {
                    await using (var input = new FileStream(sourceArchive!, FileMode.Open, FileAccess.Read, FileShare.Read))
                    await using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        if (input.Length != catalog.Wsa.Size) throw new InvalidDataException("Unexpected WSA archive size.");
                        await input.CopyToAsync(output);
                    }
                    if (!await VerifiedDownloads.VerifyAsync(archive, catalog.Wsa)) throw new InvalidDataException("Protected WSA archive failed SHA-256 verification.");
                    VerifiedDownloads.Extract(archive, stage);
                    var manifests = Directory.GetFiles(stage, "AppxManifest.xml", SearchOption.AllDirectories);
                    if (manifests.Length != 1) throw new InvalidDataException("Expected exactly one WSA manifest.");
                    Directory.Move(Path.GetDirectoryName(manifests[0])!, runtime);
                }
                finally
                {
                    if (File.Exists(archive)) File.Delete(archive);
                    if (Directory.Exists(stage)) OwnedFiles.DeleteTree(stage);
                }
            }
            AssertNoLinks(directory);
            journal = journal with { Phase = "Registering" }; Save();
            (await windows.CommandAsync("& { " + WindowsBridge.Script("RegisterWsa") + " } -Directory " + WindowsBridge.Quote(runtime))).RequireSuccess();
            var after = await windows.ProbeAsync();
            if (!Safety.SamePath(after.WsaLocation, runtime)) throw new InvalidOperationException("Windows did not register the protected WSA runtime.");
            journal = journal with { Phase = "Registered" }; Save();
        }
        else
        {
            // A user-editable state.json can request this operation, but cannot grant ownership.
            journal = journal with { Phase = "Removing" }; Save();
            var before = await windows.ProbeAsync();
            if (Safety.SamePath(before.WsaLocation, runtime) && before.WsaFullName is not null)
                (await windows.CommandAsync("Remove-AppxPackage -Package " + WindowsBridge.Quote(before.WsaFullName))).RequireSuccess();
            (await windows.CommandAsync("$registered=@(Get-AppxPackage -AllUsers -Name 'MicrosoftCorporationII.WindowsSubsystemForAndroid' | Where-Object { $_.InstallLocation -ieq " + WindowsBridge.Quote(runtime) + " }); if ($registered.Count) { throw 'WSA remains registered for a Windows account. Runtime files were preserved.' }")).RequireSuccess();
            AssertNoLinks(directory);
            OwnedFiles.DeleteTree(directory);
            foreach (var parent in parents.Reverse())
                if (!Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }
    }

    private static void EnsureProtectedDirectory(string path)
    {
        if (Directory.Exists(path)) { AssertProtectedDirectory(path); return; }
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(Administrators);
        foreach (var principal in new[] { Administrators, System })
            security.AddAccessRule(new FileSystemAccessRule(principal, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        foreach (var principal in new[] { new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), new SecurityIdentifier("S-1-15-2-1"), new SecurityIdentifier("S-1-15-2-2") })
            security.AddAccessRule(new FileSystemAccessRule(principal, FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        // Restrict implicit owner rights on newly created descendants, even on hosts
        // whose elevated token chooses an individual SID as the default file owner.
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-3-4"), FileSystemRights.ReadPermissions,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
        AssertProtectedDirectory(path);
    }

    private static void AssertProtectedDirectory(string path)
        => AssertProtectedPath(path, true);

    private static void AssertProtectedPath(string path, bool directory)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Protected WSA paths cannot be links.");
        FileSystemSecurity security = directory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        AssertSecurity(security);
    }

    internal static void AssertSecurity(FileSystemSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        if (!Administrators.Equals(owner) && !System.Equals(owner)) throw new IOException("WSA deployment directory has an untrusted owner. It was preserved.");
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
        if (rules.Count == 0) throw new IOException("WSA deployment has no usable access control list. Files were preserved.");
        const FileSystemRights mutation = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in rules)
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & mutation) != 0 && !Administrators.Equals(rule.IdentityReference) && !System.Equals(rule.IdentityReference))
                throw new IOException("WSA deployment directory is writable by an untrusted principal. It was preserved.");
    }

    private static void AssertNoLinks(string path)
    {
        AssertProtectedDirectory(path);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("WSA deployment contains a link. Files were preserved.");
            if ((attributes & FileAttributes.Directory) != 0) AssertNoLinks(entry);
            else AssertProtectedPath(entry, false);
        }
    }
}
