# Builds BusyNative.exe (x64 C++) with MSVC, including a full PDB beside the exe.
$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$out = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory $out -Force | Out-Null
$cmd = "call `"$vcvars`" >nul && cl /nologo /EHsc /O2 /Zi /Fd`"$out\BusyNative.pdb`" /Fo`"$out\\`" /Fe`"$out\BusyNative.exe`" `"$PSScriptRoot\busy.cpp`" /link /DEBUG"
& $env:ComSpec /c $cmd
