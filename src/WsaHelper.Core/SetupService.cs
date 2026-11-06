namespace WsaHelper.Core;

public sealed class SetupService(StateStore store, IWindowsBridge windows, VerifiedDownloads downloads, IProgress<string> progress)
{
    private string BeginStage(InstallState state, string prefix)
    {
        var name = prefix + "-stage-" + Guid.NewGuid().ToString("N");
        state.StagingDirectories = [.. state.StagingDirectories, name]; store.Save(state);
        return Safety.Within(store.Root, name);
    }
    private void ClearStages(InstallState state)
    {
        foreach (var name in state.StagingDirectories)
        {
            var prefix = name.StartsWith("wsa-stage-", StringComparison.Ordinal) ? "wsa-stage-" : "tools-stage-";
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _))
                throw new InvalidDataException("Invalid staging directory in setup journal; files were preserved.");
            OwnedFiles.DeleteTree(Safety.Within(store.Root, name));
        }
        state.StagingDirectories = []; store.Save(state);
    }
    public async Task<bool> InstallAsync(InstallState state, CancellationToken ct)
    {
        ClearStages(state);
        progress.Report("Checking Windows, firmware virtualization, memory, storage and existing WSA…");
        var machine = await windows.ProbeAsync(ct);
        if (machine.Build < 22000 || !machine.Architecture.Equals("X64", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This release requires Windows 11 on an x64 PC. ARM64 and Windows 10 are not supported by this package.");
        if (machine.MemoryGb < 7.5) throw new InvalidOperationException("WSA requires at least 8 GB of RAM; 16 GB is recommended.");
        if (!machine.Virtualization) throw new InvalidOperationException("Enable Intel VT-x / AMD-V (SVM) in UEFI/BIOS, restart Windows, then run setup again. An app cannot change firmware settings automatically.");
        if (machine.FreeGb < (machine.WsaLocation is null ? 12 : 1)) throw new InvalidOperationException("Not enough free space on the Local AppData drive. New WSA setup needs 12 GB; an existing WSA needs 1 GB for helper tools.");
        state.Phase = "Preparing"; store.Save(state);
        if (machine.WsaLocation is null)
        {
            if (machine.RestartPending)
            {
                progress.Report("Windows has a pending restart. Restart Windows, then setup will resume at sign-in.");
                state.Phase = "RestartRequired"; store.Save(state); return false;
            }
            progress.Report("Configuring Virtual Machine Platform and sideloading policy (Windows asks for administrator approval)…");
            if (!state.FeatureEnableAttempted) state.VmpWasEnabled = machine.VmpEnabled;
            state.FeatureEnableAttempted = true; store.Save(state);
            if (await windows.FeaturesAsync("Enable", state.InstallId, ct))
            {
                state.Phase = "RestartRequired"; store.Save(state);
                progress.Report("Windows needs a restart. Save your work and restart; setup will resume at sign-in."); return false;
            }
            ct.ThrowIfCancellationRequested();
        }
        else
        {
            progress.Report("Using the existing WSA installation; it will be preserved on removal.");
            if (state.WsaInstallAttempted && Safety.SamePath(state.WsaDirectory, machine.WsaLocation)) state.WsaOwned = true;
            state.WsaFullName = machine.WsaFullName;
            store.Save(state);
        }
        var catalog = StateStore.Catalog();
        if (!File.Exists(Path.Combine(store.Tools, "adb.exe")))
        {
            var toolsArchive = await downloads.GetAsync(catalog.PlatformTools, store.Cache, progress, ct);
            var stage = BeginStage(state, "tools");
            try
            {
                await Task.Run(() => VerifiedDownloads.Extract(toolsArchive, stage, ct), ct);
                if (Directory.Exists(store.Tools)) throw new InvalidOperationException("Incomplete platform-tools folder exists. Remove it through WSA Helper removal before retrying.");
                Directory.Move(Path.Combine(stage, "platform-tools"), store.Tools);
            }
            finally { ClearStages(state); }
        }
        if (machine.WsaLocation is null)
        {
            var destination = windows.DeploymentDirectory(state.InstallId);
            var archive = await downloads.GetAsync(catalog.Wsa, store.Cache, progress, ct);
            state.WsaDirectory = destination; state.WsaInstallAttempted = true; state.Phase = "Registering"; store.Save(state);
            progress.Report("Deploying WSA's system service and Microsoft frameworks for this Windows account (administrator approval required)…");
            await windows.InstallWsaAsync(archive, state.InstallId, ct);
            machine = await windows.ProbeAsync(ct);
            if (!Safety.SamePath(machine.WsaLocation, destination)) throw new InvalidOperationException("Windows did not register the expected protected WSA directory.");
            state.WsaOwned = true; state.WsaFullName = machine.WsaFullName;
        }
        state.Phase = "Installed"; store.Save(state);
        // Download archives are disposable; the registered WSA directory is permanent.
        if (Directory.Exists(store.Cache)) OwnedFiles.DeleteTree(store.Cache);
        progress.Report("Setup complete. Enable Developer mode in WSA settings, start WSA, and approve Android debugging when prompted.");
        return true;
    }
    public async Task RemoveAsync(InstallState state, bool restoreFeatures, CancellationToken ct)
    {
        ClearStages(state);
        var machine = await windows.ProbeAsync(ct);
        var expected = windows.DeploymentDirectory(state.InstallId);
        if (state.WsaInstallAttempted && !Safety.SamePath(state.WsaDirectory, expected))
            throw new InvalidDataException("The WSA ownership path does not match this version's protected deployment location. Its files and recovery journal were preserved.");
        if (state.WsaInstallAttempted && Safety.SamePath(state.WsaDirectory, expected))
        {
            if (Safety.SamePath(machine.WsaLocation, expected) && machine.WsaFullName is not null)
            {
                progress.Report("Removing the WSA installation created by this app, including its Android apps and data…");
            }
            else if (machine.WsaLocation is not null && Safety.SamePath(machine.WsaLocation, state.WsaDirectory))
                throw new InvalidOperationException("WSA ownership could not be verified; no files were removed.");
            // The elevated worker requires a protected, matching ownership journal too.
            await windows.RemoveWsaAsync(state.InstallId, ct);
            var after = await windows.ProbeAsync(ct);
            if (Safety.SamePath(after.WsaLocation, expected)) throw new InvalidOperationException("WSA is still registered. Its files have been preserved.");
        }
        state.WsaOwned = false; state.WsaInstallAttempted = false; state.WsaDirectory = null; state.WsaFullName = null; store.Save(state);
        if (restoreFeatures && state.FeatureEnableAttempted)
        {
            progress.Report("Restoring the machine's recorded Windows feature and sideloading settings…");
            var restart = await windows.FeaturesAsync("Restore", state.InstallId, ct);
            state.FeatureEnableAttempted = false;
            if (restart) progress.Report("Restart Windows to finish restoring Virtual Machine Platform.");
        }
        foreach (var path in new[] { store.Tools, store.Cache }) if (Directory.Exists(path)) OwnedFiles.DeleteTree(path);
        state.Phase = "Removed"; state.PendingApks = []; store.Save(state);
        progress.Report("Removal complete. Existing WSA installations and shared Microsoft framework packages were preserved.");
    }
}
public static class OwnedFiles
{
    // Call only with an exact task/app-owned directory. Never traverse junctions or symlinks.
    public static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing to traverse a linked directory: " + path);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing to delete linked content: " + entry);
            if ((attributes & FileAttributes.Directory) != 0) DeleteTree(entry);
            else { File.SetAttributes(entry, FileAttributes.Normal); File.Delete(entry); }
        }
        Directory.Delete(path);
    }
}
