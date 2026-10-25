# Builds BrightLock.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
# -Install copies it to %LOCALAPPDATA%\Programs\BrightLock and starts it.
param([switch]$Install)
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$src = Join-Path $root 'BrightLock.cs'
$bin = Join-Path $root 'bin'
$exe = Join-Path $bin 'BrightLock.exe'
$ico = Join-Path $root 'app.ico'
New-Item -ItemType Directory -Force $bin | Out-Null

$cscArgs = @('/nologo', '/target:winexe', '/optimize+', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Management.dll', "/out:$exe")

if (-not (Test-Path $ico)) {
    & $csc @cscArgs $src
    if ($LASTEXITCODE) { throw 'build failed' }
    Start-Process $exe -ArgumentList '--make-icon', "`"$ico`"" -Wait
}

& $csc @cscArgs "/win32icon:$ico" $src
if ($LASTEXITCODE) { throw 'build failed' }
Write-Host "Built $exe"

if ($Install) {
    $dest = Join-Path $env:LOCALAPPDATA 'Programs\BrightLock'
    Get-Process BrightLock -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit(3000) | Out-Null }
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item $exe $dest -Force
    Start-Process (Join-Path $dest 'BrightLock.exe')
    Write-Host "Installed to $dest and started"
}
