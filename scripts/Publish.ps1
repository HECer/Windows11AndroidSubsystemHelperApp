param([string]$Version = '0.1.0')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.]+)?$') { throw 'Invalid version' }
$root = Split-Path $PSScriptRoot -Parent
$destination = Join-Path $root "artifacts\release\WSA-Helper-$Version-win-x64"
if (Test-Path -LiteralPath $destination) { throw "Output already exists: $destination. Preserve or remove it explicitly before publishing again." }
dotnet test (Join-Path $root 'tests\WsaHelper.Tests\WsaHelper.Tests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
dotnet test (Join-Path $root 'tests\WsaHelper.Desktop.Tests\WsaHelper.Desktop.Tests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Desktop tests failed' }
dotnet publish (Join-Path $root 'src\WsaHelper.Desktop\WsaHelper.Desktop.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:Version=$Version -o $destination
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Copy-Item -LiteralPath (Join-Path $destination 'WsaHelper.exe') -Destination (Join-Path $destination 'WsaSetup.exe')
Copy-Item -LiteralPath (Join-Path $destination 'WsaHelper.exe') -Destination (Join-Path $destination 'WsaRemove.exe')
foreach ($name in @('README.md','LICENSE','THIRD-PARTY-NOTICES.md','docs\ACCEPTANCE.md','docs\VALIDATION.md')) { Copy-Item -LiteralPath (Join-Path $root $name) -Destination $destination }
& (Join-Path $PSScriptRoot 'Bundle-Licenses.ps1') -PublishDirectory $destination -AssetsFile (Join-Path $root 'src\WsaHelper.Desktop\obj\project.assets.json')
$archive = "$destination.zip"
Compress-Archive -LiteralPath $destination -DestinationPath $archive -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
Write-Output $archive
