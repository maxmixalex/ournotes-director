. (Join-Path $PSScriptRoot 'TestWorkspace.ps1')
$out = New-TestWorkspace 'render-smoke'
$succeeded = $false
try {
$ErrorActionPreference='Stop'
$cli = Get-TestCli

New-Item -ItemType Directory -Force -Path $out | Out-Null
$checks=[Collections.Generic.List[string]]::new()
function Run([string[]]$Arguments,[int]$Expected=0){$raw=& $cli @Arguments;if($LASTEXITCODE -ne $Expected){throw "Failed $Arguments : $raw"};return ($raw|ConvertFrom-Json).result}
function Check($Value,[string]$Name){if(!$Value){throw $Name};$checks.Add($Name)}
function SaveShot($Shot,[string]$Name){$path=Join-Path $out $Name;$Shot|ConvertTo-Json -Depth 20|Set-Content -LiteralPath $path -Encoding utf8;return $path}
$base=Join-Path $out 'base.json'
$null=Run @('shot','init','--template','portrait','-o',$base,'--overwrite')
$shot=Get-Content -Raw -LiteralPath $base|ConvertFrom-Json
$shot.duration=1
$shot.output.width=640;$shot.output.height=360;$shot.output.renderScale=2
$shot.tracks=@(@{target='hotaru';property='param:ParamAngleX';interpolation='smoothstep';keys=@(@{t=0;value=-20},@{t=1;value=20})})
$path=SaveShot $shot 'sequence.json'
$null=Run @('shot','validate',$path)
$checks.Add('Shot validates dependencies, motions, expressions, GPU')
$frames=Join-Path $out 'frames'
$r=Run @('render','sequence',$path,'-o',$frames,'--overwrite')
Check ($r.frameCount -eq 30) '30 exact simulation/output frames'
$r=Run @('render','sequence',$path,'-o',$frames,'--resume')
Check ($r.reused -eq 30 -and $r.written -eq 0) 'Resume verifies and reuses all PNG frames'
$frame=Join-Path $out 'frame.png'
$null=Run @('render','frame',$path,'--time','0.5','-o',$frame,'--overwrite')
$comparison=Run @('image','compare',$frame,(Join-Path $frames '000015.png'))
Check $comparison.withinOneLevel 'Random access matches sequence within one premultiplied RGBA level'
$repeat=Join-Path $out 'repeat.png'
$null=Run @('render','frame',$path,'--time','0.5','-o',$repeat,'--overwrite')
$comparison=Run @('image','compare',$frame,$repeat)
Check $comparison.withinOneLevel 'Same seed/input reproducible within one premultiplied RGBA level'
$r=Run @('image','probe',$frame)
Check ($r.visiblePixels -gt 1000 -and $r.transparentPixels -gt 1000 -and $r.partialAlphaPixels -gt 0) 'PNG contains visible character and transparent antialiased edges'
$report=Get-Content -Raw -LiteralPath (Join-Path $frames 'render.json')|ConvertFrom-Json
Check (($report.frameHashes.PSObject.Properties.Value|Sort-Object -Unique).Count -eq 30) 'All animated frames differ'
[IO.File]::WriteAllText((Join-Path $frames '000004.png'),'corrupt')
$r=Run @('render','sequence',$path,'-o',$frames,'--resume')
Check ($r.written -eq 1 -and $r.reused -eq 29) 'Resume repairs corrupt frame only'
$shot.seed=723;$changed=SaveShot $shot 'changed.json'
$null=Run @('render','sequence',$changed,'-o',$frames,'--resume') 3
$checks.Add('Resume rejects changed shot')
$shot.seed=722
$shot.actors[0].parameters=@{ParamDoesNotExist=1};$bad=SaveShot $shot 'invalid-parameter.json'
$null=Run @('shot','validate',$bad) 2
$checks.Add('Unknown model parameter rejected')
$shot.actors[0].parameters=@{}
$shot.actors[0].expression='exp_anxiety01'
$shot.events=@(@{t=.5;actor='hotaru';expression='exp_smile07';motion='mtn_sad01_C';fade=.2})
$eventPath=SaveShot $shot 'events.json'
$r=Run @('render','contact',$eventPath,'-o',(Join-Path $out 'events.png'),'--overwrite')
Check ($r.written -eq 9) 'Motion/expression events and contact sheet'
$prores=Join-Path $out 'alpha.mov'
$r=Run @('render','clip',$path,'--codec','prores','-o',$prores,'--overwrite')
$info=(& ffprobe -v error -count_frames -show_streams -of json $prores)|ConvertFrom-Json
Check ($LASTEXITCODE -eq 0 -and $info.streams[0].nb_read_frames -eq '30' -and $info.streams[0].pix_fmt -like 'yuva*') 'ProRes 4444 alpha and frame count'
$ffv1=Join-Path $out 'lossless.mkv'
$null=Run @('render','clip',$path,'--codec','ffv1','-o',$ffv1,'--overwrite')
$decoded=Join-Path $out 'ffv1-frame.png'
& ffmpeg -v error -y -i $ffv1 -ss 0.5 -frames:v 1 $decoded
if($LASTEXITCODE -ne 0){throw 'FFV1 decode failed'}
$r=Run @('image','probe',$decoded)
$expected=(Get-Content -Raw -LiteralPath "$ffv1.render.json"|ConvertFrom-Json).frameHashes.'15'
Check ($r.pixelSha256 -eq $expected) 'FFV1 round trip preserves exact encoded RGBA pixels'
$models=Run @('model','list')
foreach($model in $models.items){$shot.actors[0].model=$model.id;$shot.actors[0].expression=$null;$shot.events=@();$shot.tracks=@();$shot.camera.zoom=1;$shot.camera.y=0;$modelPath=SaveShot $shot 'model-check.json';$r=Run @('render','frame',$modelPath,'-o',(Join-Path $out ($model.id+'.png')),'--overwrite');Check $r.complete "Native frame: $($model.id)"}
$duetPath=Join-Path $out 'duet.json';$null=Run @('shot','init','--template','duet','-o',$duetPath,'--overwrite')
$duet=Get-Content -Raw -LiteralPath $duetPath|ConvertFrom-Json
$duet.stage='adv_bkg_stage_000243/adv_bkg_stage_000243';$duet.grade=@{exposure=-.2;contrast=1.1;saturation=.8;vignette=.3}
$duetPath=SaveShot $duet 'duet.json';$r=Run @('render','frame',$duetPath,'--time','1','-o',(Join-Path $out 'duet.png'),'--overwrite')
Check $r.complete 'Two actors, actual stage backdrop and grading'

@{passed=$checks.Count;checks=$checks}|ConvertTo-Json -Depth 10

$succeeded = $true
} finally {
    try { Save-TestReport 'render-smoke' $checks $succeeded } finally { Remove-TestWorkspace $out }
}
