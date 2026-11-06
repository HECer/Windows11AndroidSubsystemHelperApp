# Only this fixed script runs elevated. It never executes user paths or downloads.
param([ValidateSet('Enable','Restore')][string]$Operation, [ValidatePattern('^[a-f0-9]{32}$')][string]$InstallId, [string]$CallerSid)
$ErrorActionPreference = 'Stop'
# Refuse over-the-shoulder UAC before any registry or feature writes.
if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -cne $CallerSid) { exit 5 }
$marker = "HKLM:\SOFTWARE\WsaHelper\Changes\$InstallId"
$policy = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
try {
    if ($Operation -eq 'Enable') {
        if (!(Test-Path $marker)) {
            New-Item $marker -Force | Out-Null
            New-ItemProperty $marker 'OwnerSid' -Value $CallerSid -PropertyType String | Out-Null
            $original = Get-ItemPropertyValue $policy 'AllowDevelopmentWithoutDevLicense' -ErrorAction SilentlyContinue
            New-ItemProperty $marker 'OriginalDevelopmentPolicy' -Value $(if ($null -eq $original) {-1} else {[int]$original}) -PropertyType DWord | Out-Null
            $enabled = (Get-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform).State -eq 'Enabled'
            New-ItemProperty $marker 'VmpOriginallyEnabled' -Value ([int]$enabled) -PropertyType DWord | Out-Null
        }
        $journal = Get-ItemProperty $marker
        if ($journal.OwnerSid -cne $CallerSid -or $null -eq $journal.OriginalDevelopmentPolicy -or $null -eq $journal.VmpOriginallyEnabled) { throw 'Incomplete or foreign machine journal; no settings changed. Restore manually.' }
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
            if ($saved.OwnerSid -cne $CallerSid -or $null -eq $saved.OriginalDevelopmentPolicy -or $null -eq $saved.VmpOriginallyEnabled) { throw 'Incomplete or foreign machine journal; restore manually.' }
            $restart = $false
            if ($saved.VmpOriginallyEnabled -eq 0) {
                $result = Disable-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform -NoRestart
                $restart = [bool]$result.RestartNeeded
            }
            if ((Get-ItemPropertyValue $policy 'AllowDevelopmentWithoutDevLicense' -ErrorAction SilentlyContinue) -eq 1) {
                if ($saved.OriginalDevelopmentPolicy -eq -1) {
                    Remove-ItemProperty $policy 'AllowDevelopmentWithoutDevLicense'
                } else {
                    Set-ItemProperty $policy 'AllowDevelopmentWithoutDevLicense' -Value $saved.OriginalDevelopmentPolicy
                }
            }
            Remove-Item -LiteralPath $marker
            if ($restart) { exit 3010 }
        }
    }
    exit 0
} catch { Write-Error $_; exit 1 }
