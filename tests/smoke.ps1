. (Join-Path $PSScriptRoot 'TestWorkspace.ps1')
$out = New-TestWorkspace 'smoke'
$succeeded = $false
try {
$ErrorActionPreference = 'Stop'
$cli = Get-TestCli
$checks = [System.Collections.Generic.List[string]]::new()
function Invoke-Adv([string[]] $Arguments, [int] $ExpectedExit = 0) {
    $raw = & $cli @Arguments
    if ($LASTEXITCODE -ne $ExpectedExit) { throw "Unexpected exit for $Arguments : $raw" }
    $response = $raw | ConvertFrom-Json
    if ($response.schema -ne 'ournotes-director/1') { throw 'Invalid envelope' }
    return $response.result
}
function Check($Condition, [string] $Name) {
    if (!$Condition) { throw "Failed: $Name" }
    $checks.Add($Name)
}
$r = Invoke-Adv @('story', 'list', '--limit', '1')
Check ($r.total -eq 175 -and $r.nextOffset -eq 1) '175 stories and pagination'
$r = Invoke-Adv @('dialogue', 'search', '幸福', '--speaker', 'hotaru', '--lang', 'zh-Hans', '--limit', '1')
Check ($r.total -eq 43 -and $r.items[0].voices[0].soundId -eq 3101040101002) 'Chinese dialogue to voice mapping'
$r = Invoke-Adv @('dialogue', 'search', '幸せ', '--lang', 'ja')
Check ($r.total -gt 0) 'Japanese substring search'
$models = Invoke-Adv @('model', 'list')
Check ($models.total -eq 31) '31 model manifests'
foreach ($model in $models.items) {
    $r = Invoke-Adv @('model', 'show', $model.id)
    Check ($r.id -eq $model.id) "Model prefab $($model.id)"
}
$id = 'adv_live2d_hotaru_016_casual_spring_01'
$r = Invoke-Adv @('motion', 'list', $id)
Check ($r.total -eq 66) 'Hotaru 66 motions'
$r = Invoke-Adv @('expression', 'list', $id)
Check ($r.total -eq 46) 'Hotaru 46 expressions'
foreach ($pair in @(@('stage',44), @('postfx',10), @('still',7), @('frame',14), @('media',2))) {
    $r = Invoke-Adv @($pair[0], 'list')
    Check ($r.total -eq $pair[1]) "$($pair[0]) registry count"
    $detail = Invoke-Adv @($pair[0], 'show', [string]$r.items[0].id)
    Check ($null -ne $detail) "$($pair[0]) stable ID round trip"
}
$r = Invoke-Adv @('probe', 'native', $id)
Check ($r.updated -and !$r.rendered -and $r.parameters -eq 189 -and $r.drawables -eq 356) 'Actual Cubism Core load and update'
$output = Join-Path $out 'hotaru-happiness.m4a'
$r = Invoke-Adv @('voice', 'extract', '3101040101002', '-o', $output, '--overwrite')
Check ($r.size -eq 148721 -and (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant() -eq $r.sha256) 'Verified voice export'
$null = Invoke-Adv @('voice', 'extract', '3101040101002', '-o', $output) 3
$checks.Add('Existing output protected')
$null = Invoke-Adv @('render', 'frame', 'missing.json') 2
$checks.Add('Render missing output argument fails explicitly')
$null = Invoke-Adv @('model', 'list', '--limit', '0') 2
$checks.Add('Invalid paging fails explicitly')
$r = Invoke-Adv @('asset', 'verify', "$id.moc3", '--model', $id)
Check ($r.verified -and $r.size -eq 11036032) 'Real gzip moc3 integrity'
$r = Invoke-Adv @('asset', 'verify', "$id.prefab.json", '--model', $id)
Check $r.verified 'Real split prefab integrity'
$r = Invoke-Adv @('asset', 'search', '.webm', '--kind', 'video', '--limit', '1')
Check ($r.total -eq 2 -and (Test-Path -LiteralPath $r.items[0].resolvedPath)) 'Video search resolves actual file'
$report = [ordered]@{ passed = $checks.Count; checks = $checks }

$report | ConvertTo-Json -Depth 5

$succeeded = $true
} finally {
    try { Save-TestReport 'smoke' $checks $succeeded } finally { Remove-TestWorkspace $out }
}
