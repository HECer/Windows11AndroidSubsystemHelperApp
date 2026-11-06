using WsaHelper.Core;
using Xunit;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace WsaHelper.Tests;

public sealed class DeploymentPolicyTests
{
    [Theory]
    [InlineData("../another-install")]
    [InlineData("C:\\Windows")]
    [InlineData("00112233-4455-6677-8899-aabbccddeeff")]
    [InlineData("00112233445566778899AABBCCDDEEFF")]
    [InlineData("")]
    public void ElevatedDestinationRejectsArbitraryPaths(string installId) =>
        Assert.Throws<InvalidDataException>(() => DeploymentPolicy.DirectoryFor(Path.GetTempPath(), installId));

    [Fact]
    public void DestinationIsFixedUnderTheMachineRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ProgramFiles");
        Assert.Equal(Path.Combine(root, "WsaHelper", "Subsystems", "00112233445566778899aabbccddeeff"),
            DeploymentPolicy.DirectoryFor(root, "00112233445566778899aabbccddeeff"));
    }

    [Fact]
    public void OtherAdministratorCredentialsCannotRedirectRegistration()
    {
        var error = Assert.Throws<InvalidOperationException>(() => DeploymentPolicy.RequireIdentity("S-1-5-21-1000", "S-1-5-21-2000", true));
        Assert.Contains("different Windows account", error.Message);
    }

    [Fact]
    public void SameAccountWithoutElevationIsRejected() =>
        Assert.Throws<InvalidOperationException>(() => DeploymentPolicy.RequireIdentity("S-1-5-21-1000", "S-1-5-21-1000", false));

    [Fact]
    public void ElevatedOriginalAccountIsAccepted() => DeploymentPolicy.RequireIdentity("S-1-5-21-1000", "S-1-5-21-1000", true);

    [Theory, SupportedOSPlatform("windows")]
    [InlineData("S-1-5-21-1000", FileSystemRights.ReadAndExecute)]
    [InlineData("S-1-5-32-544", FileSystemRights.Write)]
    [InlineData("S-1-5-32-544", FileSystemRights.ChangePermissions)]
    [InlineData("S-1-5-32-544", FileSystemRights.TakeOwnership)]
    public void UntrustedFileOwnershipOrWritableAclIsRejected(string owner, FileSystemRights usersRights)
    {
        if (!OperatingSystem.IsWindows()) return;
        var acl = Acl(owner, usersRights);
        Assert.Throws<IOException>(() => MachineWsaDeployment.AssertSecurity(acl));
    }

    [Fact, SupportedOSPlatform("windows")]
    public void ProtectedMachineAclAllowsUsersAndAppContainersToReadOnly()
    {
        if (!OperatingSystem.IsWindows()) return;
        var acl = Acl("S-1-5-32-544", FileSystemRights.ReadAndExecute);
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-15-2-1"), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        MachineWsaDeployment.AssertSecurity(acl);
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity Acl(string owner, FileSystemRights usersRights)
    {
        var acl = new DirectorySecurity();
        acl.SetOwner(new SecurityIdentifier(owner));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-544"), FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-18"), FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"), usersRights, AccessControlType.Allow));
        return acl;
    }
}
