# Builds Folio as a single self-contained .exe, then wraps it in an .msi installer.
# Output: artifacts\Folio-<version>.msi
param([string]$Version = '1.0.0')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publish = Join-Path $root 'artifacts\publish\'

dotnet publish "$root\Folio\Folio.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:Version=$Version -o $publish
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

dotnet build "$root\installer\Folio.Installer.wixproj" -c Release -p:Version=$Version -p:PublishDir=$publish
if ($LASTEXITCODE) { throw 'installer build failed' }

$msi = Get-ChildItem "$root\installer\bin" -Recurse -Filter Folio.msi | Sort-Object LastWriteTime | Select-Object -Last 1
$out = Join-Path $root "artifacts\Folio-$Version.msi"
Copy-Item $msi.FullName $out -Force
"Built $out ($([math]::Round((Get-Item $out).Length / 1MB, 1)) MB)"
