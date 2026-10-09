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
        Write-Host "Native executable: artifacts/$Runtime/advkit$(if ($Runtime -eq 'win-x64') { '.exe' })"
    }
}
finally { Pop-Location }
