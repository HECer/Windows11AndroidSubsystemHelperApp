param([Parameter(Mandatory)][string]$PublishDirectory, [Parameter(Mandatory)][string]$AssetsFile)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$notices = Join-Path $PublishDirectory 'licenses'
New-Item -ItemType Directory -Path $notices -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'licenses\SharpCompress-LICENSE.txt') -Destination $notices
$assets = Get-Content -Raw -LiteralPath $AssetsFile | ConvertFrom-Json
$packages = $assets.packageFolders.PSObject.Properties.Name
$runtimes = $assets.project.frameworks.PSObject.Properties.Value.downloadDependencies | Where-Object name -Match '^Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.win-x64$'
foreach ($library in $runtimes) {
    $version = $library.version.Trim('[',']').Split(',')[0].Trim()
    $relative = $library.name.ToLowerInvariant() + '/' + $version
    $source = $null
    foreach ($packageRoot in $packages) {
        $candidate = Join-Path $packageRoot $relative
        if (Test-Path -LiteralPath $candidate) { $source = $candidate; break }
    }
    if (!$source) { throw "Runtime license source not found: $($library.name)" }
    $target = Join-Path $notices ($library.name + '-' + $version)
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    $files = @(Get-ChildItem -LiteralPath $source -File | Where-Object Name -Match '^(LICENSE|THIRD-PARTY-NOTICES)(\.TXT)?$')
    if (!($files | Where-Object Name -Match '^LICENSE')) { throw "Missing runtime license: $($library.name)" }
    foreach ($file in $files) { Copy-Item -LiteralPath $file.FullName -Destination $target }
}
if (!(Get-ChildItem -LiteralPath $notices -Directory)) { throw 'No runtime license notices were bundled.' }
