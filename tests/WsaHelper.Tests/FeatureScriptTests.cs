using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Security.Principal;
using WsaHelper.Core;
using Xunit;

namespace WsaHelper.Tests;

// Executes the actual embedded script, with HKLM rebound inside a private child
// process to this test's disposable HKCU subtree. DISM is replaced by functions.
// This never elevates or changes real Windows features or machine policy.
[SupportedOSPlatform("windows")]
public sealed class FeatureScriptTests : IDisposable
{
    private readonly string root = @"Software\WsaHelperFeatureTest_" + Guid.NewGuid().ToString("N");
    private readonly string id = Guid.NewGuid().ToString("N");
    private string Marker => root + @"\SOFTWARE\WsaHelper\Changes\" + id;
    private string Policy => root + @"\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock";
    private string Feature => root + @"\FakeFeature";
    private readonly string caller;

    public FeatureScriptTests()
    {
        using var identity = WindowsIdentity.GetCurrent(); caller = identity.User!.Value;
        using var key = Registry.CurrentUser.CreateSubKey(Feature);
        key.SetValue("State", "Disabled"); key.SetValue("EnableCount", 0); key.SetValue("DisableCount", 0);
    }
    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(root, false);
    private object? Value(string path, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path); return key?.GetValue(name);
    }
    private void Set(string path, string name, object value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path); key.SetValue(name, value);
    }
    private Task<ProcessResult> Run(string operation, string? sid = null)
    {
        var driveRoot = @"HKEY_CURRENT_USER\" + root;
        var bootstrap = "Remove-PSDrive -Name HKLM -Force; New-PSDrive -Name HKLM -PSProvider Registry -Root " + WindowsBridge.Quote(driveRoot) + " | Out-Null; " +
            "if ((Get-PSDrive HKLM).Root -cne " + WindowsBridge.Quote(driveRoot) + ") { throw 'Test registry isolation failed' }; " +
            "function Get-WindowsOptionalFeature { param([switch]$Online,[string]$FeatureName) if ($FeatureName -ne 'VirtualMachinePlatform') { throw 'Unexpected test feature' }; [pscustomobject]@{State=(Get-ItemPropertyValue 'HKLM:\\FakeFeature' 'State')} }; " +
            "function Enable-WindowsOptionalFeature { param([switch]$Online,[string]$FeatureName,[switch]$All,[switch]$NoRestart) Set-ItemProperty 'HKLM:\\FakeFeature' 'State' 'Enabled'; Set-ItemProperty 'HKLM:\\FakeFeature' 'EnableCount' ((Get-ItemPropertyValue 'HKLM:\\FakeFeature' 'EnableCount')+1); [pscustomobject]@{RestartNeeded=$true} }; " +
            "function Disable-WindowsOptionalFeature { param([switch]$Online,[string]$FeatureName,[switch]$NoRestart) Set-ItemProperty 'HKLM:\\FakeFeature' 'State' 'Disabled'; Set-ItemProperty 'HKLM:\\FakeFeature' 'DisableCount' ((Get-ItemPropertyValue 'HKLM:\\FakeFeature' 'DisableCount')+1); [pscustomobject]@{RestartNeeded=$true} }; ";
        return new WindowsBridge(new ProcessRunner()).CommandAsync(bootstrap + "& { " + WindowsBridge.Script("Features") + " } -Operation " + WindowsBridge.Quote(operation) +
            " -InstallId " + WindowsBridge.Quote(id) + " -CallerSid " + WindowsBridge.Quote(sid ?? caller));
    }
    [Fact]
    public async Task MissingPolicyAndDisabledVmpAreRestoredExactly()
    {
        Assert.Equal(3010, (await Run("Enable")).ExitCode);
        Assert.Equal(0, Value(Marker, "OriginalDevelopmentPolicyPresent")); Assert.Equal(0, Value(Marker, "VmpOriginallyEnabled"));
        Assert.Equal(1, Value(Policy, "AllowDevelopmentWithoutDevLicense")); Assert.Equal("Enabled", Value(Feature, "State"));
        Assert.Equal(3010, (await Run("Restore")).ExitCode);
        Assert.Null(Value(Policy, "AllowDevelopmentWithoutDevLicense")); Assert.Null(Value(Marker, "OwnerSid")); Assert.Equal("Disabled", Value(Feature, "State"));
    }
    [Fact]
    public async Task RetryDoesNotReplaceTheBaselineAndPreservesOriginallyEnabledVmp()
    {
        Set(Feature, "State", "Enabled"); Set(Policy, "AllowDevelopmentWithoutDevLicense", 0);
        Assert.Equal(0, (await Run("Enable")).ExitCode); Assert.Equal(0, (await Run("Enable")).ExitCode);
        Assert.Equal(0, Value(Marker, "OriginalDevelopmentPolicy")); Assert.Equal(1, Value(Marker, "VmpOriginallyEnabled"));
        Assert.Equal(0, (await Run("Restore")).ExitCode);
        Assert.Equal(0, Value(Policy, "AllowDevelopmentWithoutDevLicense")); Assert.Equal(0, Value(Feature, "DisableCount"));
    }
    [Fact]
    public async Task LaterPolicyChangesArePreserved()
    {
        Set(Feature, "State", "Enabled"); Assert.Equal(0, (await Run("Enable")).ExitCode);
        Set(Policy, "AllowDevelopmentWithoutDevLicense", 2); Assert.Equal(0, (await Run("Restore")).ExitCode);
        Assert.Equal(2, Value(Policy, "AllowDevelopmentWithoutDevLicense"));
    }
    [Fact]
    public async Task DifferentUacAccountIsRejectedBeforeAnyJournalOrFeatureWrite()
    {
        Assert.Equal(5, (await Run("Enable", "S-1-5-21-111-222-333-444")).ExitCode);
        Assert.Null(Value(Marker, "OwnerSid")); Assert.Null(Value(Policy, "AllowDevelopmentWithoutDevLicense")); Assert.Equal(0, Value(Feature, "EnableCount"));
    }
    [Fact]
    public async Task IncompleteBaselineCanResumeBeforeTheFirstFeatureChange()
    {
        Set(Marker, "OwnerSid", caller); Set(Marker, "OriginalDevelopmentPolicy", -1);
        Assert.Equal(3010, (await Run("Enable")).ExitCode);
        Assert.Equal(0, Value(Marker, "VmpOriginallyEnabled"));
        Assert.Equal(3010, (await Run("Restore")).ExitCode);
        Assert.Null(Value(Policy, "AllowDevelopmentWithoutDevLicense")); Assert.Equal("Disabled", Value(Feature, "State"));
    }
    [Fact]
    public async Task AnExistingAllBitsSetDwordIsNotConfusedWithAMissingPolicy()
    {
        Set(Feature, "State", "Enabled"); Set(Policy, "AllowDevelopmentWithoutDevLicense", -1);
        Assert.Equal(0, (await Run("Enable")).ExitCode); Assert.Equal(1, Value(Marker, "OriginalDevelopmentPolicyPresent"));
        Assert.Equal(0, (await Run("Restore")).ExitCode); Assert.Equal(-1, Value(Policy, "AllowDevelopmentWithoutDevLicense"));
    }
    [Fact]
    public async Task RemovalOfAnIncompleteBaselineDoesNotTouchFeaturesOrPolicy()
    {
        Set(Marker, "OwnerSid", caller); Set(Marker, "OriginalDevelopmentPolicyPresent", 0);
        Assert.Equal(0, (await Run("Restore")).ExitCode);
        Assert.Null(Value(Marker, "OwnerSid")); Assert.Null(Value(Policy, "AllowDevelopmentWithoutDevLicense")); Assert.Equal(0, Value(Feature, "DisableCount"));
    }
    [Fact]
    public async Task ACompletedButDamagedBaselineCannotBeSilentlyRecaptured()
    {
        Set(Marker, "OwnerSid", caller); Set(Marker, "BaselineComplete", 1);
        Assert.NotEqual(0, (await Run("Enable")).ExitCode); Assert.NotEqual(0, (await Run("Restore")).ExitCode);
        Assert.Equal(0, Value(Feature, "EnableCount")); Assert.Equal(0, Value(Feature, "DisableCount")); Assert.Null(Value(Policy, "AllowDevelopmentWithoutDevLicense"));
    }
    [Fact]
    public async Task InterruptionImmediatelyAfterKeyCreationCanResume()
    {
        using (Registry.CurrentUser.CreateSubKey(Marker)) { }
        Assert.Equal(3010, (await Run("Enable")).ExitCode); Assert.Equal(caller, Value(Marker, "OwnerSid"));
        Assert.Equal(3010, (await Run("Restore")).ExitCode); Assert.Null(Value(Policy, "AllowDevelopmentWithoutDevLicense"));
    }
    [Fact]
    public async Task InterruptionBetweenPolicyPresenceAndValueCanResume()
    {
        Set(Marker, "OwnerSid", caller); Set(Marker, "OriginalDevelopmentPolicyPresent", 1);
        Set(Policy, "AllowDevelopmentWithoutDevLicense", 0);
        Assert.Equal(3010, (await Run("Enable")).ExitCode); Assert.Equal(0, Value(Marker, "OriginalDevelopmentPolicy"));
        Assert.Equal(3010, (await Run("Restore")).ExitCode); Assert.Equal(0, Value(Policy, "AllowDevelopmentWithoutDevLicense"));
    }
    [Fact]
    public async Task OldUnsignedMissingPolicySentinelRestoresAbsence()
    {
        Set(Marker, "OwnerSid", caller); Set(Marker, "OriginalDevelopmentPolicy", -1); Set(Marker, "VmpOriginallyEnabled", 0);
        Set(Feature, "State", "Enabled"); Set(Policy, "AllowDevelopmentWithoutDevLicense", 1);
        Assert.Equal(3010, (await Run("Restore")).ExitCode); Assert.Null(Value(Policy, "AllowDevelopmentWithoutDevLicense"));
    }
    [Fact]
    public async Task RemovalBeforeAnySettingsChangeDoesNotDisableAnAlreadyDisabledFeature()
    {
        Set(Marker, "OwnerSid", caller); Set(Marker, "OriginalDevelopmentPolicy", 0); Set(Marker, "OriginalDevelopmentPolicyPresent", 0);
        Set(Marker, "VmpOriginallyEnabled", 0); Set(Marker, "BaselineComplete", 1);
        Assert.Equal(0, (await Run("Restore")).ExitCode); Assert.Equal(0, Value(Feature, "DisableCount"));
    }
}
