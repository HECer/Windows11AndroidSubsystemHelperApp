using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WsaHelper.Core;

namespace WsaHelper.Desktop;

internal static class DesktopInstall
{
    public static string Home => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WsaHelper");
    public static string Bin => Path.Combine(Home, "App");
    public static string Manager => Path.Combine(Bin, "WsaHelper.exe");
    private const string ProgId = "WsaHelper.Apk";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WsaHelper";
    private static readonly string[] DistributionFiles =
    [
        "WsaHelper.exe", "WsaSetup.exe", "WsaRemove.exe", "WsaHelper.dll", "WsaHelper.Core.dll", "SharpCompress.dll",
        "WsaHelper.deps.json", "WsaHelper.runtimeconfig.json", "README.md", "LICENSE", "THIRD-PARTY-NOTICES.md", "ACCEPTANCE.md", "VALIDATION.md"
    ];
    internal static string[] CopyDistributionFiles(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        var copied = new List<string>();
        foreach (var name in DistributionFiles)
        {
            var source = Path.Combine(sourceDirectory, name);
            if (!File.Exists(source)) continue;
            File.Copy(source, Path.Combine(targetDirectory, name), true); copied.Add(name);
        }
        return copied.ToArray();
    }
    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
    public static void Deploy(InstallState state)
    {
        var copied = new List<string>();
        Directory.CreateDirectory(Bin);
        var marker = Path.Combine(Bin, ".wsa-helper-owned");
        if (Directory.EnumerateFileSystemEntries(Bin).Any() && (!File.Exists(marker) || File.ReadAllText(marker) != state.InstallId))
            throw new IOException("The app installation directory contains unowned files. It was preserved.");
        File.WriteAllText(marker, state.InstallId);
        if (!Safety.SamePath(AppContext.BaseDirectory, Bin))
        {
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "WsaHelper.exe"))) throw new IOException("Run WsaSetup.exe from the extracted release folder.");
            // Copy only this product's files, even when setup was extracted beside unrelated downloads.
            copied.AddRange(CopyDistributionFiles(AppContext.BaseDirectory, Bin));
        }
        else if (!File.Exists(Path.Combine(Bin, InstalledFiles.ManifestName)))
            throw new IOException("Helper file ownership is missing. Run setup from the downloaded release folder to repair it safely.");
        foreach (var name in new[] { "WsaSetup.exe", "WsaRemove.exe" })
            if (!File.Exists(Path.Combine(Bin, name))) { File.Copy(Manager, Path.Combine(Bin, name)); copied.Add(name); }
        var sourceLicenses = Path.Combine(AppContext.BaseDirectory, "licenses");
        var targetLicenses = Path.Combine(Bin, "licenses");
        if (!Safety.SamePath(AppContext.BaseDirectory, Bin) && Directory.Exists(sourceLicenses))
        {
            foreach (var source in Directory.EnumerateFiles(sourceLicenses, "*", SearchOption.AllDirectories))
            {
                var destination = Safety.Within(targetLicenses, Path.GetRelativePath(sourceLicenses, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination, true);
                copied.Add(Path.GetRelativePath(Bin, destination));
            }
        }
        InstalledFiles.RecordDeployment(Bin, state.InstallId, copied);
    }
    public static void Register(InstallState state)
    {
        using (var type = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
        {
            type.SetValue("", "Android application (WSA Helper)");
            using var command = type.CreateSubKey(@"shell\open\command"); command.SetValue("", $"\"{Manager}\" --install \"%1\"");
            using var icon = type.CreateSubKey("DefaultIcon"); icon.SetValue("", $"\"{Manager}\",0");
        }
        using (var list = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.apk\OpenWithProgids")) list.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        using (var application = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\WsaHelper.exe"))
        {
            application.SetValue("FriendlyAppName", "WSA Helper — Install Android APK");
            using var command = application.CreateSubKey(@"shell\open\command"); command.SetValue("", $"\"{Manager}\" --install \"%1\"");
            using var types = application.CreateSubKey("SupportedTypes"); types.SetValue(".apk", "");
        }
        using (var uninstall = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            uninstall.SetValue("DisplayName", "WSA Helper"); uninstall.SetValue("DisplayVersion", typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");
            uninstall.SetValue("Publisher", "WSA Helper contributors"); uninstall.SetValue("InstallLocation", Bin);
            uninstall.SetValue("UninstallString", $"\"{Path.Combine(Bin, "WsaRemove.exe")}\"");
            uninstall.SetValue("NoModify", 1); uninstall.SetValue("NoRepair", 1);
        }
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var folder = Path.Combine(programs, "WSA Helper"); Directory.CreateDirectory(folder);
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows shortcut service is unavailable.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            foreach (var (name, exe) in new[] { ("Android Apps", "WsaHelper.exe"), ("Setup", "WsaSetup.exe"), ("Remove", "WsaRemove.exe") })
            {
                dynamic shortcut = shell.CreateShortcut(Path.Combine(folder, name + ".lnk"));
                try { shortcut.TargetPath = Path.Combine(Bin, exe); shortcut.WorkingDirectory = Bin; shortcut.Description = "WSA Helper — " + name; shortcut.Save(); }
                finally { Marshal.FinalReleaseComObject(shortcut); }
            }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        state.AssociationRegistered = true;
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }
    public static void ResumeAtSignIn(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue("WsaHelperSetup", $"\"{Manager}\" --setup --resume");
        else key.DeleteValue("WsaHelperSetup", false);
    }
    public static void Unregister()
    {
        ResumeAtSignIn(false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId, false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Applications\WsaHelper.exe", false);
        using (var list = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.apk\OpenWithProgids", true)) list?.DeleteValue(ProgId, false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "WSA Helper");
        foreach (var name in new[] { "Android Apps.lnk", "Setup.lnk", "Remove.lnk" })
        {
            var file = Path.Combine(folder, name); if (File.Exists(file)) File.Delete(file);
        }
        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }
}
