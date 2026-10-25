# Builds BrightLock.exe and BrightLock-Setup.exe with the C# compiler that ships with
# Windows (.NET Framework 4.x), so no SDK is needed. Output goes to bin\.
# -Install runs the new setup quietly (installs or updates this PC's copy and starts it).
param([switch]$Install)
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$src = Join-Path $root 'BrightLock.cs'
$inst = Join-Path $root 'Install.cs'
$bin = Join-Path $root 'bin'
$obj = Join-Path $root 'obj'
$exe = Join-Path $bin 'BrightLock.exe'
$setup = Join-Path $bin 'BrightLock-Setup.exe'
$ico = Join-Path $root 'app.ico'
New-Item -ItemType Directory -Force $bin, $obj | Out-Null

$refs = @('/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Management.dll')
$common = @('/nologo', '/target:winexe', '/optimize+') + $refs

if (-not (Test-Path $ico)) {
    & $csc @common "/out:$exe" $src $inst
    if ($LASTEXITCODE) { throw 'build failed' }
    Start-Process $exe -ArgumentList '--make-icon', "`"$ico`"" -Wait
}

& $csc @common "/win32icon:$ico" "/out:$exe" $src $inst
if ($LASTEXITCODE) { throw 'build failed' }
Write-Host "Built $exe"

# The setup gets the same version as the app (Program.Version in BrightLock.cs).
$m = Select-String -Path $src -Pattern 'const string Version = "([0-9.]+)"' | Select-Object -First 1
if (-not $m) { throw 'Version not found in BrightLock.cs' }
$version = $m.Matches[0].Groups[1].Value
$verFile = Join-Path $obj 'SetupVersion.cs'
@"
[assembly: System.Reflection.AssemblyTitle("BrightLock Setup")]
[assembly: System.Reflection.AssemblyProduct("BrightLock")]
[assembly: System.Reflection.AssemblyVersion("$version")]
[assembly: System.Reflection.AssemblyFileVersion("$version")]
namespace BrightLock { static class SetupVersion { public const string Value = "$version"; } }
"@ | Set-Content -Encoding ASCII $verFile

& $csc @common "/win32icon:$ico" "/resource:$exe,BrightLock.exe" "/out:$setup" (Join-Path $root 'Setup.cs') $inst $verFile
if ($LASTEXITCODE) { throw 'setup build failed' }
Write-Host "Built $setup ($version)"

if ($Install) {
    # Not Start-Process -Wait: that also waits for the BrightLock the setup starts.
    $p = [Diagnostics.Process]::Start($setup, '/S')
    $p.WaitForExit()
    if ($p.ExitCode) { throw "setup failed ($($p.ExitCode))" }
    Write-Host "Installed to $env:LOCALAPPDATA\Programs\BrightLock and started"
}
