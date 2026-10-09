. (Join-Path $PSScriptRoot 'TestWorkspace.ps1')
$ErrorActionPreference = 'Stop'
$out = New-TestWorkspace 'smoke'
$checks = [Collections.Generic.List[string]]::new()
$succeeded = $false
$cli = Get-TestCli
function Run([string[]]$Arguments,[int]$Expected=0) {
    $raw = & $cli @Arguments
    if ($LASTEXITCODE -ne $Expected) { throw "Unexpected exit: $Arguments : $raw" }
    return ($raw | ConvertFrom-Json).result
}
function Check($Value,[string]$Name) { if (!$Value) { throw $Name }; $checks.Add($Name) }
try {
    $r = Run @('story','commands','10060','--offset','9','--limit','1')
    Check ($r.items[0].IgnoreLipSync -eq 1 -and $r.items[0].TargetTextIDs[0] -eq 'adv_hotaru') 'Lossless episode fields available'
    $audit = Run @('story','audit','10060')
    Check (!$audit.stories[0].nativeOriginalPlaybackReady -and $audit.stories[0].commandCount -eq 1035) 'Audit does not overclaim original playback'
    Check ($audit.stories[0].assets.missing.Count -eq 0) 'Story referenced assets exist'
    $plan = Run @('story','dialogue-plan','10060','--limit','2')
    Check ($plan.lines.Count -eq 2 -and $plan.lines[0].contentFrames -eq 38 -and $plan.lines[1].startFrame -eq 50) 'Real source sample counts produce frame-aligned timeline'
    $package = Join-Path $out 'package'
    $r = Run @('story','export-dialogue','10060','--limit','2','-o',$package)
    $info = Run @('media','probe',(Join-Path $package 'dialogue.wav'))
    Check ([math]::Abs([double]$info.format.duration - $r.duration) -lt .00001) 'WAV matches timeline duration exactly'
    Check ($info.streams[0].sample_rate -eq '48000' -and $info.streams[0].channels -eq 2) '48kHz stereo PCM export'
    $pcm = [IO.File]::ReadAllBytes((Join-Path $package 'dialogue.wav'))
    Check (@($pcm[44..([Math]::Min($pcm.Length-1,48000))] | Where-Object { $_ -ne 0 }).Count -gt 100) 'Exported voice is audible PCM rather than silence'
    $manifest = Get-Content (Join-Path $package 'manifest.json') -Raw | ConvertFrom-Json
    foreach ($file in $manifest.files) {
        Check ((Get-FileHash -LiteralPath (Join-Path $package $file.path)).Hash.ToLowerInvariant() -eq $file.sha256) "Verified package file $($file.path)"
    }
    $null = Run @('story','export-dialogue','10060','--limit','2','-o',$package) 3
    $checks.Add('Existing package protected')
    $video = Join-Path $out 'video.mp4'
    & ffmpeg -v error -nostdin -y -f lavfi -i 'color=c=red:s=64x64:r=30:d=2' -c:v libx264 -pix_fmt yuv420p $video
    if ($LASTEXITCODE -ne 0) { throw 'Test video generation failed' }
    $mux = Join-Path $out 'mux.mkv'
    $null = Run @('media','mux',$video,'--audio',(Join-Path $package 'dialogue.wav'),'--subtitles',(Join-Path $package 'dialogue.srt'),'-o',$mux)
    $info = Run @('media','probe',$mux)
    Check ($info.streams.Count -eq 3) 'Video, PCM audio and selectable subtitles muxed'
    Check ([Math]::Abs([double]$info.format.duration - 2) -lt .04) 'Long audio and subtitles cannot extend video duration'
    $first = & ffmpeg -v error -i $video -map 0:v:0 -f hash -hash sha256 -
    if ($LASTEXITCODE -ne 0) { throw 'Source frame hashing failed' }
    $second = & ffmpeg -v error -i $mux -map 0:v:0 -f hash -hash sha256 -
    if ($LASTEXITCODE -ne 0) { throw 'Muxed frame hashing failed' }
    Check ($first -eq $second) 'Mux preserves all decoded video pixels'
    $mp4 = Join-Path $out 'mux.mp4'
    $shortVoice = Get-ChildItem -LiteralPath (Join-Path $package 'voices') -Filter '0b3781*.m4a' | Select-Object -First 1
    $null = Run @('media','mux',$video,'--audio',$shortVoice.FullName,'--subtitles',(Join-Path $package 'dialogue.srt'),'-o',$mp4)
    $info = Run @('media','probe',$mp4)
    Check ([Math]::Abs([double]$info.format.duration - 2) -lt .04 -and @($info.streams | Where-Object codec_name -eq 'mov_text').Count -eq 1) 'MP4 pads short audio and carries clipped selectable subtitles'
    $bad = Join-Path $out 'failed-package'
    $null = Run @('story','export-dialogue','10060','--limit','1','--ffmpeg','nonexistent-ournotes-ffmpeg','-o',$bad) 3
    Check (!(Test-Path $bad) -and @(Get-ChildItem -LiteralPath $out -Directory -Filter '.dialogue-*').Count -eq 0) 'Failed encoding leaves no partial package or scratch directory'
    $succeeded = $true
    @{passed=$checks.Count;checks=$checks} | ConvertTo-Json -Depth 6
} finally {
    try { Save-TestReport 'story-smoke' $checks $succeeded } finally { Remove-TestWorkspace $out }
}
