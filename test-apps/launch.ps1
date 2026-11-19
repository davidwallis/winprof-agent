<#
.SYNOPSIS
Builds (if needed) and starts all test apps, or stops them with -Stop.
#>
param([switch]$Stop, [switch]$Rebuild)

$root = $PSScriptRoot
$apps = @(
    @{ Process = 'busy';       Exe = "$root\busy-net10\bin\Release\net10.0\busy.exe";   Build = { dotnet build "$root\busy-net10" -c Release -nologo -v q } },
    @{ Process = 'FxBusy';     Exe = "$root\busy-netfx\out\FxBusy.exe";                  Build = { & "$root\busy-netfx\build.ps1" } },
    @{ Process = 'BusyNative'; Exe = "$root\busy-native\out\BusyNative.exe";             Build = $null }
)

foreach ($a in $apps) {
    if ($Stop) {
        Get-Process $a.Process -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force; "Stopped $($a.Process) ($($_.Id))" }
        continue
    }

    if (Get-Process $a.Process -ErrorAction SilentlyContinue) { "$($a.Process) already running"; continue }

    if ((-not (Test-Path $a.Exe) -or $Rebuild) -and $a.Build) { & $a.Build | Out-Null }
    if (-not (Test-Path $a.Exe)) {
        Write-Warning "$($a.Exe) not found. BusyNative must be built in Visual Studio (Release|x64) or with busy-native\build.ps1."
        continue
    }

    try {
        $p = Start-Process $a.Exe -WindowStyle Hidden -PassThru -ErrorAction Stop
        "Started $($a.Process) ($($p.Id))"
    }
    catch {
        Write-Warning "Could not start $($a.Process): $($_.Exception.Message)"
    }
}
