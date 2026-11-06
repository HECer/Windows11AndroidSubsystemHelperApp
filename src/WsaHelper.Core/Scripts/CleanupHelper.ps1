# Per-user cleanup after the helper exits. Only recorded, unchanged helper files are removed.
param([string]$Directory, [ValidatePattern('^[a-f0-9]{32}$')][string]$InstallId, [int]$WaitForPid = 0, [string]$ReportPath)
$ErrorActionPreference = 'Stop'
if ($WaitForPid -gt 0) { Wait-Process -Id $WaitForPid -ErrorAction SilentlyContinue }
$root = [IO.Path]::GetFullPath($Directory).TrimEnd('\')
$prefix = $root + '\'
$marker = Join-Path $root '.wsa-helper-owned'
$manifestPath = Join-Path $root '.wsa-helper-files.json'
if (!(Test-Path -LiteralPath $root)) { return }
if ((Get-Item -LiteralPath $root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Helper directory is a link; files were preserved.' }
if (!(Test-Path -LiteralPath $marker) -or (Get-Content -Raw -LiteralPath $marker) -ne $InstallId) { throw 'Helper ownership marker is missing or different; files were preserved.' }
if (!(Test-Path -LiteralPath $manifestPath)) { throw 'Helper file manifest is missing; files were preserved.' }
foreach ($metadata in @($marker, $manifestPath)) {
    if ((Get-Item -LiteralPath $metadata -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Ownership metadata is linked; files were preserved.' }
}
$manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
if ($manifest.Schema -ne 1 -or $manifest.InstallId -ne $InstallId -or $null -eq $manifest.Files) { throw 'Invalid helper file manifest; files were preserved.' }
$candidates = @()
# Validate the entire manifest before deleting any content.
foreach ($entry in $manifest.Files) {
    $relative = [string]$entry.Path
    if (!$relative -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or $entry.Sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid helper file ownership entry.' }
    $path = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (!$path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or $path -eq $marker -or $path -eq $manifestPath) { throw 'Helper cleanup path is outside its ownership boundary.' }
    $current = $path
    while ($current -ne $root) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Helper cleanup path contains a link; files were preserved.' }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    $candidates += [pscustomobject]@{ Path = $path; Relative = $relative; Hash = $entry.Sha256 }
}
$retained = [Collections.Generic.List[string]]::new()
$removed = 0
$parents = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $candidates) {
    if (Test-Path -LiteralPath $entry.Path -PathType Leaf) {
        if ((Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash -ne $entry.Hash) { $retained.Add($entry.Relative); continue }
        try { Remove-Item -LiteralPath $entry.Path -Force; $removed++ }
        catch { $retained.Add($entry.Relative); continue }
    }
    $parent = [IO.Path]::GetDirectoryName($entry.Path)
    while ($parent -ne $root) { [void]$parents.Add($parent); $parent = [IO.Path]::GetDirectoryName($parent) }
}
foreach ($parent in $parents | Sort-Object Length -Descending) {
    if ((Test-Path -LiteralPath $parent -PathType Container) -and @(Get-ChildItem -LiteralPath $parent -Force).Count -eq 0) { Remove-Item -LiteralPath $parent }
}
if ($retained.Count -eq 0) {
    Remove-Item -LiteralPath $manifestPath
    Remove-Item -LiteralPath $marker
    if (@(Get-ChildItem -LiteralPath $root -Force).Count -eq 0) { Remove-Item -LiteralPath $root }
}
$report = @{ RemovedFiles = $removed; RetainedModifiedFiles = @($retained); DirectoryRemains = [bool](Test-Path -LiteralPath $root) } | ConvertTo-Json -Compress
if ($ReportPath) { [IO.File]::WriteAllText($ReportPath, $report) }
Write-Output $report
