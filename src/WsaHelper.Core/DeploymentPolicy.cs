namespace WsaHelper.Core;

public static class DeploymentPolicy
{
    public static string InstallId(string value) => Guid.TryParseExact(value, "N", out _) && value == value.ToLowerInvariant()
        ? value : throw new InvalidDataException("Invalid subsystem installation identity.");

    public static string DirectoryFor(string programFiles, string installId) =>
        Safety.Within(programFiles, "WsaHelper/Subsystems/" + InstallId(installId));

    public static void RequireIdentity(string callerSid, string currentSid, bool administrator)
    {
        if (!administrator) throw new InvalidOperationException("Windows requires administrator approval to deploy WSA's system service.");
        if (!callerSid.StartsWith("S-1-5-", StringComparison.Ordinal) || callerSid != currentSid)
            throw new InvalidOperationException("Administrator approval used a different Windows account. No deployment changes were made. Fresh WSA setup must be started by an administrator account and approved by that same account; existing WSA installations can be managed normally.");
    }
}
