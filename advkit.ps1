$ErrorActionPreference = 'Stop'
$runtime = if ($env:OS -eq 'Windows_NT') { 'win-x64' } else { 'linux-x64' }
$name = if ($env:OS -eq 'Windows_NT') { 'advkit.exe' } else { 'advkit' }
$cli = Join-Path $PSScriptRoot "artifacts/$runtime/$name"
if (!(Test-Path -LiteralPath $cli)) {
    [Console]::Error.WriteLine('Run ./build.ps1 first.')
    exit 3
}
& $cli @args
exit $LASTEXITCODE
