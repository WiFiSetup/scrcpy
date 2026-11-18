$ErrorActionPreference = 'Stop'
$root = Join-Path $env:LOCALAPPDATA 'Programs\scrcpy'
$bin = Join-Path $root 'scrcpy-win64-v5.0.1'
if (-not (Test-Path -LiteralPath (Join-Path $bin 'scrcpy.exe'))) {
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    $archive = Join-Path $root 'scrcpy-win64-v5.0.1.zip'
    Invoke-WebRequest 'https://github.com/Genymobile/scrcpy/releases/download/v5.0.1/scrcpy-win64-v5.0.1.zip' -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne 'B12A2C4EE8BE317422451FC7DCF8EE20A71B5EA7EF9AD73DDD825A25316227A5') { throw 'scrcpy archive checksum mismatch' }
    Expand-Archive -LiteralPath $archive -DestinationPath $root -Force
}
dotnet build (Join-Path $PSScriptRoot 'AndroidDesk.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Dashboard build failed' }
$target = Join-Path $env:LOCALAPPDATA 'Programs\AndroidDesk'
New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item -Path (Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\*') -Destination $target -Force
$wsh = New-Object -ComObject WScript.Shell
$shortcut = $wsh.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'Android Desk.lnk'))
$shortcut.TargetPath = Join-Path $target 'AndroidDesk.exe'
$shortcut.WorkingDirectory = $target
$shortcut.Description = 'Manage Android phones and tablets on your desktop'
$shortcut.Save()
Write-Output "Installed Android Desk: $target"
