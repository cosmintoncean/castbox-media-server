$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'publish\CastBox.exe'
if (!(Test-Path $source)) { throw 'CastBox.exe was not found. Run Build-CastBox.ps1 first.' }
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\CastBox'
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item $source (Join-Path $installDir 'CastBox.exe') -Force
$exe = Join-Path $installDir 'CastBox.exe'
$desktop = [Environment]::GetFolderPath('Desktop')
$ws = New-Object -ComObject WScript.Shell
$shortcut = $ws.CreateShortcut((Join-Path $desktop 'CastBox Server.lnk'))
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $installDir
$shortcut.Description = 'CastBox home media server'
$shortcut.Save()
Write-Host 'CastBox installed for the current Windows user.' -ForegroundColor Green
Write-Host 'A desktop shortcut was created. Open it and choose your media folders.'
Start-Process $exe
