$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'CastBox.Windows.csproj'
$out = Join-Path $PSScriptRoot 'publish'
Write-Host 'Building CastBox as a self-contained Windows x64 application...' -ForegroundColor Cyan
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $out
if ($LASTEXITCODE -ne 0) { Write-Host "dotnet publish failed with exit code $LASTEXITCODE. The compiler output above contains the actual error." -ForegroundColor Red; exit $LASTEXITCODE }
Write-Host "Published to $out" -ForegroundColor Green
Write-Host 'Run CastBox.exe to open setup. It does not require the .NET runtime after publishing.'
