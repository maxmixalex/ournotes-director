. (Join-Path $PSScriptRoot 'TestWorkspace.ps1')
$ErrorActionPreference='Stop'
$out=New-TestWorkspace 'smoke'
$checks=[Collections.Generic.List[string]]::new()
$succeeded=$false
$cli = Get-TestCli
function Run([string[]]$Arguments,[int]$Expected=0){$raw=& $cli @Arguments;if($LASTEXITCODE -ne $Expected){throw "Unexpected exit $Arguments : $raw"};return ($raw|ConvertFrom-Json).result}
function Check($Value,[string]$Name){if(!$Value){throw $Name};$checks.Add($Name)}
try {
    $r=Run @('story','native-check','all')
    Check ($r.stories.Count -eq 175 -and @($r.stories|Where-Object compiled).Count -eq 55) 'Native importer distinguishes 55 normal ADV and 120 host-driven episodes'
    Check (!$r.browser -and !$r.fullFidelityVerified) 'Native status distinguishes initialization from complete visual parity'
    $story=$r.stories|Where-Object advId -eq '10060'
    Check ($story.initialized -and $story.implementedHandlers -contains 'Bgm' -and $story.implementedHandlers -contains 'Talk' -and $story.implementedHandlers -contains 'DoF' -and $story.missing.Count -eq 0) 'Original 10060 actually loads its native scene, dialogue, audio and postprocessing resources'
    $program=Run @('story','native-program','10060')
    Check ($program.rows.Count -eq 1035 -and $program.initialize[0].command -eq 'Bgm' -and $program.finalize.Count -gt 0) 'Actual initialization and finalization compiled from scene settings'
    $simple=$r.stories|Where-Object {!$_.compiled}|Select-Object -First 1
    $null=Run @('story','native-program',[string]$simple.advId) 4
    $checks.Add('SimpleAdvPlayer refuses normal interpreter rather than silently substituting it')
    foreach($id in @('3101040101001','1100000000013')) {
        $sound=Run @('probe','sound',$id,'--adv','10060')
        Check ($sound.rate -eq 48000 -and $sound.samples -gt 0 -and $sound.peak -gt .001) "Native float PCM decode of sound $id"
    }
    $camera=Run @('probe','camera','10060')
    Check ($camera.near -gt 0 -and $camera.far -gt $camera.near -and $camera.projection.Count -eq 16 -and !$camera.rendered) 'Native camera reads real Unity scene and emits a projection matrix'
    $before=@(Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Filter 'ournotes-audio-*' -File | Select-Object -ExpandProperty Name)
    $null=Run @('probe','sound','3101040101001','--adv','10060','--ffmpeg','nonexistent-ournotes-ffmpeg') 3
    $after=@(Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Filter 'ournotes-audio-*' -File | Select-Object -ExpandProperty Name)
    Check (@($after|Where-Object {$_ -notin $before}).Count -eq 0) 'Failed native sound decoding cleans its seekable temporary input'
    $succeeded=$true
    @{passed=$checks.Count;checks=$checks}|ConvertTo-Json -Depth 5
} finally {try{Save-TestReport 'native-engine-smoke' $checks $succeeded}finally{Remove-TestWorkspace $out}}
