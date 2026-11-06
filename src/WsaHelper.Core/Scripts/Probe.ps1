$ErrorActionPreference = 'Stop'
$os = Get-CimInstance Win32_OperatingSystem
$computer = Get-CimInstance Win32_ComputerSystem
$cpu = @(Get-CimInstance Win32_Processor)
$feature = Get-CimInstance Win32_OptionalFeature -Filter "Name='VirtualMachinePlatform'"
$wsa = Get-AppxPackage -Name 'MicrosoftCorporationII.WindowsSubsystemForAndroid' | Select-Object -First 1
$disk = Get-CimInstance Win32_LogicalDisk -Filter ("DeviceID='" + $env:LOCALAPPDATA.Substring(0,2) + "'")
@{
    Build = [int]$os.BuildNumber
    Architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    MemoryGb = [math]::Round($computer.TotalPhysicalMemory / 1GB, 1)
    FreeGb = [math]::Round($disk.FreeSpace / 1GB, 1)
    Virtualization = [bool]($computer.HypervisorPresent -or ($cpu | Where-Object VirtualizationFirmwareEnabled))
    VmpEnabled = [bool]($feature.InstallState -eq 1)
    RestartPending = [bool]((Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') -or (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired'))
    WsaLocation = $(if ($wsa) {$wsa.InstallLocation} else {$null})
    WsaFullName = $(if ($wsa) {$wsa.PackageFullName} else {$null})
} | ConvertTo-Json -Compress
