param([string]$Directory)
$ErrorActionPreference = 'Stop'
$manifest = Join-Path $Directory 'AppxManifest.xml'
[xml]$xml = Get-Content -LiteralPath $manifest
if ($xml.Package.Identity.Name -ne 'MicrosoftCorporationII.WindowsSubsystemForAndroid' -or $xml.Package.Identity.ProcessorArchitecture -ne 'x64') { throw 'Unexpected WSA package identity.' }
$existing = Get-AppxPackage -Name $xml.Package.Identity.Name
if ($existing) {
    if ([IO.Path]::GetFullPath($existing.InstallLocation).TrimEnd('\') -ieq [IO.Path]::GetFullPath($Directory).TrimEnd('\')) { return }
    throw 'WSA was installed by another process. Refresh setup; existing installations are preserved.'
}
foreach ($dependency in $xml.Package.Dependencies.PackageDependency) {
    $installed = Get-AppxPackage -Name $dependency.Name | Where-Object { $_.Architecture -eq 'X64' -or $_.Architecture -eq 'Neutral' } | Sort-Object Version -Descending | Select-Object -First 1
    if (!$installed -or [version]$installed.Version -lt [version]$dependency.MinVersion) {
        $path = Join-Path $Directory ($dependency.Name + '_x64.appx')
        if (!(Test-Path -LiteralPath $path)) { throw "Missing bundled dependency: $($dependency.Name)" }
        # AppX deployment verifies the package signature and certificate trust. Check its manifest identity too.
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($path)
        try {
            $entry = $archive.GetEntry('AppxManifest.xml')
            $reader = New-Object IO.StreamReader($entry.Open())
            try { [xml]$dependencyXml = $reader.ReadToEnd() } finally { $reader.Dispose() }
            if ($dependencyXml.Package.Identity.Name -ne $dependency.Name -or $dependencyXml.Package.Identity.Publisher -ne $dependency.Publisher) { throw 'Unexpected dependency identity.' }
        } finally { $archive.Dispose() }
        Add-AppxPackage -Path $path -ErrorAction Stop
    }
}
Add-AppxPackage -Register $manifest -ErrorAction Stop
