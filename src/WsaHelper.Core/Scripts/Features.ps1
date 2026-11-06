# Only this fixed script runs elevated. It never executes user paths or downloads.
param([ValidateSet('Enable','Restore')][string]$Operation, [ValidatePattern('^[a-f0-9]{32}$')][string]$InstallId, [string]$CallerSid)
$ErrorActionPreference = 'Stop'
# Refuse over-the-shoulder UAC before any registry or feature writes.
if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -cne $CallerSid) { exit 5 }
$marker = "HKLM:\SOFTWARE\WsaHelper\Changes\$InstallId"
$policy = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
# The registry provider can expose DWORD 0xffffffff as unsigned 4294967295.
# Preserve its bit pattern; do not use -1 alone to represent an absent value.
function ConvertTo-SignedDword([long]$Value) {
    if ($Value -lt -2147483648 -or $Value -gt 4294967295) { throw 'Invalid DWORD in machine journal.' }
    return [BitConverter]::ToInt32([BitConverter]::GetBytes([uint32]($Value -band 4294967295L)), 0)
}
function Test-PolicyPresent($Journal) {
    if ($null -ne $Journal.OriginalDevelopmentPolicyPresent) {
        if ($Journal.OriginalDevelopmentPolicyPresent -notin @(0,1)) { throw 'Invalid policy-presence flag in machine journal.' }
        return $Journal.OriginalDevelopmentPolicyPresent -eq 1
    }
    # Older journals used this ambiguous DWORD sentinel for a missing policy.
    return $Journal.OriginalDevelopmentPolicy -ne -1 -and $Journal.OriginalDevelopmentPolicy -ne 4294967295L
}
try {
    # Creating a registry key and recording its owner are separate writes.
    # An empty key cannot have changed any settings and is safe to resume.
    if ((Test-Path $marker) -and (Get-Item -LiteralPath $marker).GetValueNames().Count -eq 0) {
        New-ItemProperty $marker 'OwnerSid' -Value $CallerSid -PropertyType String | Out-Null
    }
    if ($Operation -eq 'Enable') {
        if (!(Test-Path $marker)) {
            New-Item $marker -Force | Out-Null
            New-ItemProperty $marker 'OwnerSid' -Value $CallerSid -PropertyType String | Out-Null
        }
        $journal = Get-ItemProperty $marker
        if ($journal.OwnerSid -cne $CallerSid) { throw 'Foreign machine journal; no settings changed.' }
        if ($journal.BaselineComplete -eq 1 -and ($null -eq $journal.OriginalDevelopmentPolicy -or $null -eq $journal.OriginalDevelopmentPolicyPresent -or $null -eq $journal.VmpOriginallyEnabled)) { throw 'Recorded machine baseline is damaged; settings were preserved.' }
        if ($null -eq $journal.OriginalDevelopmentPolicy) {
            if ($null -ne $journal.VmpOriginallyEnabled) { throw 'Inconsistent machine journal; settings were preserved.' }
            $original = Get-ItemPropertyValue $policy 'AllowDevelopmentWithoutDevLicense' -ErrorAction SilentlyContinue
            # Presence is written first. On interruption, both values can be
            # captured again because no settings change until BaselineComplete.
            New-ItemProperty $marker 'OriginalDevelopmentPolicyPresent' -Value ([int]($null -ne $original)) -PropertyType DWord -Force | Out-Null
            New-ItemProperty $marker 'OriginalDevelopmentPolicy' -Value $(if ($null -eq $original) {0} else {ConvertTo-SignedDword ([long]$original)}) -PropertyType DWord | Out-Null
        } elseif ($null -eq $journal.OriginalDevelopmentPolicyPresent) {
            New-ItemProperty $marker 'OriginalDevelopmentPolicyPresent' -Value ([int](Test-PolicyPresent $journal)) -PropertyType DWord | Out-Null
        }
        if ($null -eq $journal.VmpOriginallyEnabled) {
            $enabled = (Get-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform).State -eq 'Enabled'
            New-ItemProperty $marker 'VmpOriginallyEnabled' -Value ([int]$enabled) -PropertyType DWord | Out-Null
        }
        $journal = Get-ItemProperty $marker
        if ($journal.VmpOriginallyEnabled -notin @(0,1)) { throw 'Invalid VMP baseline; settings were preserved.' }
        ConvertTo-SignedDword ([long]$journal.OriginalDevelopmentPolicy) | Out-Null
        Test-PolicyPresent $journal | Out-Null
        New-ItemProperty $marker 'BaselineComplete' -Value 1 -PropertyType DWord -Force | Out-Null
        $restart = $false
        if ((Get-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform).State -ne 'Enabled') {
            $result = Enable-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform -All -NoRestart
            $restart = [bool]$result.RestartNeeded
        }
        New-Item $policy -Force | Out-Null
        New-ItemProperty $policy 'AllowDevelopmentWithoutDevLicense' -Value 1 -PropertyType DWord -Force | Out-Null
        if ($restart) { exit 3010 }
    } else {
        if (Test-Path $marker) {
            $saved = Get-ItemProperty $marker
            if ($saved.OwnerSid -cne $CallerSid) { throw 'Foreign machine journal; settings were preserved.' }
            if ($null -eq $saved.OriginalDevelopmentPolicy -and $null -ne $saved.VmpOriginallyEnabled) { throw 'Inconsistent machine journal; settings were preserved.' }
            if ($null -eq $saved.OriginalDevelopmentPolicy -or $null -eq $saved.VmpOriginallyEnabled) {
                if ($saved.BaselineComplete -eq 1) { throw 'Recorded machine baseline is damaged; settings were preserved.' }
                # A partial baseline cannot have changed features or policy.
                Remove-Item -LiteralPath $marker
                exit 0
            }
            if ($saved.BaselineComplete -eq 1 -and $null -eq $saved.OriginalDevelopmentPolicyPresent) { throw 'Recorded policy-presence flag is missing; settings were preserved.' }
            if ($saved.VmpOriginallyEnabled -notin @(0,1)) { throw 'Invalid VMP baseline; settings were preserved.' }
            $policyPresent = Test-PolicyPresent $saved
            $originalPolicy = ConvertTo-SignedDword ([long]$saved.OriginalDevelopmentPolicy)
            $restart = $false
            if ($saved.VmpOriginallyEnabled -eq 0 -and (Get-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform).State -ne 'Disabled') {
                $result = Disable-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform -NoRestart
                $restart = [bool]$result.RestartNeeded
            }
            if ((Get-ItemPropertyValue $policy 'AllowDevelopmentWithoutDevLicense' -ErrorAction SilentlyContinue) -eq 1) {
                if (!$policyPresent) {
                    Remove-ItemProperty $policy 'AllowDevelopmentWithoutDevLicense'
                } else {
                    Set-ItemProperty $policy 'AllowDevelopmentWithoutDevLicense' -Value $originalPolicy
                }
            }
            Remove-Item -LiteralPath $marker
            if ($restart) { exit 3010 }
        }
    }
    exit 0
} catch { Write-Error $_; exit 1 }
