<#
.SYNOPSIS
Registers (or with -Unregister removes) BusyCom for the current user, hosted in a dllhost.exe surrogate. No admin needed.
#>
param([switch]$Unregister)

$clsid = '{5A6E1B1E-7C0D-4D8E-9B53-0A1D5F7A1C03}'
$iid = '{5A6E1B1E-7C0D-4D8E-9B53-0A1D5F7A1C01}'
$libid = '{5A6E1B1E-7C0D-4D8E-9B53-0A1D5F7A1C02}'
$appid = '{5A6E1B1E-7C0D-4D8E-9B53-0A1D5F7A1C04}'
$classes = 'HKCU:\Software\Classes'
$keys = "$classes\CLSID\$clsid", "$classes\AppID\$appid", "$classes\Interface\$iid", "$classes\TypeLib\$libid"

if ($Unregister) {
    $keys | ForEach-Object { Remove-Item $_ -Recurse -Force -ErrorAction SilentlyContinue }
    'Unregistered'
    return
}

$dll = Join-Path $PSScriptRoot 'out\BusyCom.dll'
$tlb = Join-Path $PSScriptRoot 'out\BusyCom.tlb'
if (-not (Test-Path $dll)) { throw "Build first: $PSScriptRoot\build.ps1" }

function Set-Default($path, $value) {
    New-Item $path -Force | Out-Null
    Set-Item $path -Value $value
}

Set-Default "$classes\CLSID\$clsid" 'BusyCom.Busy'
Set-ItemProperty "$classes\CLSID\$clsid" -Name AppID -Value $appid
Set-Default "$classes\CLSID\$clsid\InprocServer32" $dll
Set-ItemProperty "$classes\CLSID\$clsid\InprocServer32" -Name ThreadingModel -Value 'Both'

Set-Default "$classes\AppID\$appid" 'BusyCom'
Set-ItemProperty "$classes\AppID\$appid" -Name DllSurrogate -Value ''

Set-Default "$classes\TypeLib\$libid\1.0" 'BusyComLib'
Set-Default "$classes\TypeLib\$libid\1.0\0\win64" $tlb

Set-Default "$classes\Interface\$iid" 'IBusy'
Set-Default "$classes\Interface\$iid\ProxyStubClsid32" '{00020424-0000-0000-C000-000000000046}'
Set-Default "$classes\Interface\$iid\TypeLib" $libid
Set-ItemProperty "$classes\Interface\$iid\TypeLib" -Name Version -Value '1.0'

'Registered (HKCU, DllSurrogate)'
