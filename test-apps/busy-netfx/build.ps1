# Builds FxBusy.exe (.NET Framework 4.x) with the in-box compiler, including a full PDB.
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$out = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory $out -Force | Out-Null
& $csc /nologo /debug:full /optimize+ /out:"$out\FxBusy.exe" "$PSScriptRoot\FxBusy.cs"
