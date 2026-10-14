[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'The D3D11 presentation bridge is Windows-only.' }
$source = Join-Path (Split-Path $PSScriptRoot -Parent) 'native/GpuPreview/GpuPreview.cpp'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'MSVC x64 build tools are required for GPU preview.' }
$vcvars = Join-Path $installation 'VC/Auxiliary/Build/vcvars64.bat'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('ournotes-gpu-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    $script = Join-Path $temporary 'build.cmd'
    @"
@echo off
call "$vcvars" >nul
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /O2 /EHsc /MT /W4 /WX /LD /utf-8 "$source" /Fo"$temporary\GpuPreview.obj" /link /OUT:"$OutputDirectory\OurNotes.GpuPreview.dll" /IMPLIB:"$temporary\GpuPreview.lib" /INCREMENTAL:NO /DYNAMICBASE /NXCOMPAT d3d11.lib dxgi.lib opengl32.lib
exit /b %errorlevel%
"@ | Set-Content -LiteralPath $script -Encoding ascii
    & cmd.exe /d /c $script
    if ($LASTEXITCODE -ne 0) { throw "GPU preview bridge build failed: $LASTEXITCODE" }
    if (-not (Test-Path (Join-Path $OutputDirectory 'OurNotes.GpuPreview.dll'))) { throw 'GPU bridge was not produced.' }
} finally { Remove-Item -LiteralPath $temporary -Recurse -Force }
