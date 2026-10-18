$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'CastBox.Windows.csproj'
$out = Join-Path $root 'publish'
Write-Host 'Publishing CastBox as a self-contained Windows x64 app...' -ForegroundColor Cyan
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
$iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue
if (!$iscc) { throw 'Inno Setup is required to compile the installer. Install Inno Setup 6, then run this script again.' }
& $iscc.Source (Join-Path $root 'installer\CastBox.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE" }
Write-Host "Installer created in $root\installer-output\CastBox-Setup.exe" -ForegroundColor Green
