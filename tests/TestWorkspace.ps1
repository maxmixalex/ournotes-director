function Get-TestCli {
    if ($env:OURNOTES_CLI) { return $env:OURNOTES_CLI }
    $runtime = if ($env:OS -eq 'Windows_NT') { 'win-x64' } else { 'linux-x64' }
    $name = if ($env:OS -eq 'Windows_NT') { 'advkit.exe' } else { 'advkit' }
    $cli = Join-Path $PSScriptRoot "../artifacts/$runtime/$name"
    if (!(Test-Path -LiteralPath $cli)) { throw 'Publish the native executable first, or set OURNOTES_CLI.' }
    return $cli
}

function New-TestWorkspace([string] $Name) {
    $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../output'))
    $path = Join-Path $root ($Name + '-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $path | Out-Null
    return $path
}

function Remove-TestWorkspace([string] $Path) {
    $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../output'))
    $resolved = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    if (![string]::Equals([IO.Path]::GetDirectoryName($resolved), $root, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^(smoke|render-smoke)-[0-9a-f]{32}$') {
        throw "Refusing cleanup outside owned test workspace: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

function Save-TestReport([string] $Name, $Checks, [bool] $Succeeded) {
    $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../output/reports'))
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    @{ passed = $Checks.Count; checks = @($Checks.ToArray()); succeeded = $Succeeded; utc = [DateTime]::UtcNow.ToString('o') } |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $root ($Name + '.json')) -Encoding utf8
}
