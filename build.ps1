param(
    [ValidateSet('win-x64', 'linux-x64')]
    [string]$Runtime = $(if ($env:OS -eq 'Windows_NT') { 'win-x64' } else { 'linux-x64' }),
    [switch]$NoPublish
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:DOTNET_NOLOGO = 'true'
$hostRuntime = if ($env:OS -eq 'Windows_NT') { 'win-x64' } else { 'linux-x64' }
if (-not $NoPublish -and $Runtime -ne $hostRuntime) {
    throw "Native AOT requires the target OS. Build $Runtime on that OS, or use the GitHub Actions matrix."
}

Push-Location $PSScriptRoot
try {
    & dotnet build OurNotes.Director.sln -c Release --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & dotnet run --project tests/OurNotes.Director.Tests -c Release --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    if (-not $NoPublish) {
        & dotnet publish src/OurNotes.Director.Cli -c Release -r $Runtime -o "artifacts/$Runtime" --nologo
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        & python scripts/stage-native.py --rid $Runtime --output "artifacts/$Runtime"
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        if ($Runtime -eq 'win-x64') {
            $msysRoot = if ($env:MSYS2_ROOT) { $env:MSYS2_ROOT } else { 'C:/msys64' }
            $msysBash = Join-Path $msysRoot 'usr/bin/bash.exe'
            if (-not (Test-Path -LiteralPath $msysBash)) {
                throw 'Media builds require MSYS2 UCRT64 (GCC, NASM, make, pkgconf, curl and Python). Set MSYS2_ROOT if it is installed outside C:/msys64.'
            }
            $previousMsystem = $env:MSYSTEM
            $previousChere = $env:CHERE_INVOKING
            try {
                $env:MSYSTEM = 'UCRT64'
                $env:CHERE_INVOKING = '1'
                & $msysBash --login -c 'bash scripts/build-media.sh win-x64 artifacts/win-x64'
                if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
            } finally {
                $env:MSYSTEM = $previousMsystem
                $env:CHERE_INVOKING = $previousChere
            }
        } else {
            & bash scripts/build-media.sh $Runtime "artifacts/$Runtime"
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        & "artifacts/$Runtime/advkit$(if ($Runtime -eq 'win-x64') { '.exe' })" probe runtime
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        Write-Host "Native executable: artifacts/$Runtime/advkit$(if ($Runtime -eq 'win-x64') { '.exe' })"
    }
}
finally { Pop-Location }
