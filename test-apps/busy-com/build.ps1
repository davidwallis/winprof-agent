# Builds BusyCom.dll (ATL in-proc COM server) and BusyComClient.exe with PDBs into out\.
$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$out = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory $out -Force | Out-Null

$steps = @(
    "midl /nologo /env x64 /h BusyCom_h.h /iid BusyCom_i.c /tlb `"$out\BusyCom.tlb`" /out `"$out`" `"$PSScriptRoot\BusyCom.idl`"",
    "cl /nologo /LD /D_WINDLL /EHsc /O2 /Zi /I`"$out`" /Fd`"$out\BusyCom.pdb`" /Fo`"$out\\`" /Fe`"$out\BusyCom.dll`" `"$PSScriptRoot\BusyCom.cpp`" `"$out\BusyCom_i.c`" /link /DEBUG /DEF:`"$PSScriptRoot\BusyCom.def`" ole32.lib oleaut32.lib uuid.lib advapi32.lib",
    "cl /nologo /O2 /Zi /I`"$out`" /Fd`"$out\BusyComClient.pdb`" /Fo`"$out\\`" /Fe`"$out\BusyComClient.exe`" `"$PSScriptRoot\client.cpp`" `"$out\BusyCom_i.c`" /link /DEBUG ole32.lib uuid.lib"
)
& $env:ComSpec /c ("call `"$vcvars`" >nul && " + ($steps -join ' && '))
