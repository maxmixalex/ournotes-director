[CmdletBinding(DefaultParameterSetName = 'Directory')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Directory')] [string] $Directory,
    [Parameter(Mandatory, ParameterSetName = 'Package')] [string] $Package,
    [string] $ReportDirectory = 'artifacts/desktop-smoke',
    [string] $FixtureDirectory,
    [string] $RealSceneFixtureDirectory,
    [string] $OpenGLDriver,
    [ValidateRange(10, 900)] [int] $TimeoutSeconds = 120,
    [ValidateRange(10, 300)] [int] $ProgressTimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'The real WinUI smoke test requires a Windows desktop session.' }
$repository = Split-Path $PSScriptRoot -Parent
if (-not [IO.Path]::IsPathFullyQualified($ReportDirectory)) { $ReportDirectory = Join-Path $repository $ReportDirectory }
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('ournotes-desktop-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$process = $null
try {
    if ($PSCmdlet.ParameterSetName -eq 'Package') {
        $Package = (Resolve-Path -LiteralPath $Package).Path
        $Directory = Join-Path $scratch 'extracted package'
        Expand-Archive -LiteralPath $Package -DestinationPath $Directory
    }
    $Directory = (Resolve-Path -LiteralPath $Directory).Path
    $executable = Join-Path $Directory 'OurNotes.Director.exe'
    $backend = Join-Path $Directory 'advkit.exe'
    foreach ($path in @($executable, $backend, (Join-Path $Directory 'DESKTOP.md'), (Join-Path $Directory 'desktop-dependencies.json'))) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Incomplete desktop package: $path" }
    }
    $manifest = Get-Content -LiteralPath (Join-Path $Directory 'desktop-dependencies.json') -Raw | ConvertFrom-Json
    if ($manifest.schema -ne 'ournotes.desktop.bundle/v1' -or $manifest.runtimeIdentifier -ne 'win-x64') {
        throw 'The desktop dependency manifest has an unexpected schema or target.'
    }
    $prefix = $Directory.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    foreach ($entry in $manifest.files) {
        $path = [IO.Path]::GetFullPath((Join-Path $Directory $entry.file))
        if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "A manifest path escapes the package: $($entry.file)" }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing desktop dependency: $($entry.file)" }
        if ((Get-Item -LiteralPath $path).Length -ne $entry.size -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) {
            throw "Desktop dependency checksum mismatch: $($entry.file)"
        }
    }
    if ((Get-FileHash -LiteralPath $backend -Algorithm SHA256).Hash -ne $manifest.workerSha256) {
        throw 'The published Native AOT worker changed during desktop packaging.'
    }

    # Start the shipped EXE from outside its directory, without an external .NET runtime,
    # archive, SDK environment, or FFmpeg PATH. RenderTargetBitmap runs in this same UI.
    $reportPath = Join-Path $ReportDirectory 'report.json'
    if (Test-Path -LiteralPath $reportPath) { Remove-Item -LiteralPath $reportPath }
    $startInfo = [Diagnostics.ProcessStartInfo]::new($executable)
    $startInfo.UseShellExecute = $false
    $startInfo.WorkingDirectory = $scratch
    $startInfo.ArgumentList.Add('--smoke-test')
    $startInfo.ArgumentList.Add($reportPath)
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment['DOTNET_ROOT'] = Join-Path $scratch 'no-external-dotnet'
    $startInfo.Environment['DOTNET_ROOT_X64'] = $startInfo.Environment['DOTNET_ROOT']
    $startInfo.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $startInfo.Environment['OURNOTES_DIRECTOR_DATA'] = Join-Path $scratch 'desktop profile'
    $startInfo.Environment['OURNOTES_ARCHIVE'] = Join-Path $scratch 'no-game-archive'
    $startInfo.Environment['PATH'] = "$env:WINDIR\System32;$env:WINDIR"
    if ($FixtureDirectory) {
        $startInfo.Environment['OURNOTES_STUDIO_FIXTURE'] = (Resolve-Path -LiteralPath $FixtureDirectory).Path
    }
    if ($RealSceneFixtureDirectory) {
        $startInfo.Environment['OURNOTES_REAL_SCENE_FIXTURE'] = (Resolve-Path -LiteralPath $RealSceneFixtureDirectory).Path
    }
    if ($OpenGLDriver) {
        $startInfo.Environment['OURNOTES_TEST_OPENGL'] = (Resolve-Path -LiteralPath $OpenGLDriver).Path
        $startInfo.Environment['GALLIUM_DRIVER'] = 'llvmpipe'
        $startInfo.Environment['LIBGL_ALWAYS_SOFTWARE'] = '1'
    }
    foreach ($name in @('CUBISM_NATIVE_SDK', 'CUBISM_MOTIONSYNC_DLL', 'FFMPEG_PATH', 'FFPROBE_PATH')) {
        [void] $startInfo.Environment.Remove($name)
    }
    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) { throw 'The WinUI process could not be started.' }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    # The complete suite contains several independent native scene and export
    # phases. Track actual completed checks as well as its overall ceiling, so a
    # hung operation is reported promptly and a growing suite retains all gates.
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $lastProgressAt = 0.0
    $lastProgress = ''
    $lastPhase = 'startup'
    try {
        while (-not $process.WaitForExit(500)) {
            if (Test-Path -LiteralPath $reportPath -PathType Leaf) {
                try {
                    $progress = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
                    if ($null -ne $progress -and $progress.PSObject.Properties.Name -contains 'checks') {
                        $lastPhase = if ($progress.PSObject.Properties.Name -contains 'phase') { [string]$progress.phase } else { 'unreported' }
                        $signature = "$lastPhase/$(@($progress.checks).Count)"
                        if ($signature -ne $lastProgress) {
                            $lastProgress = $signature
                            $lastProgressAt = $clock.Elapsed.TotalSeconds
                            Write-Output ("WinUI progress {0:N1}s: {1}" -f $lastProgressAt, $signature)
                        }
                    }
                }
                # Only a complete, valid report counts as progress. A diagnostic
                # writer from an older package may briefly replace its contents.
                catch { }
            }
            if ($clock.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                throw "The complete WinUI suite exceeded $TimeoutSeconds seconds at $lastProgress."
            }
            if ($clock.Elapsed.TotalSeconds - $lastProgressAt -ge $ProgressTimeoutSeconds) {
                throw "The WinUI suite made no check or phase progress for $ProgressTimeoutSeconds seconds at $lastProgress."
            }
        }
    }
    finally {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        # Preserve diagnostics on timeout too; previously the throw above lost
        # the redirected renderer and export-worker output from a failed run.
        $stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $ReportDirectory 'stdout.log') -Encoding utf8NoBOM
        $stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $ReportDirectory 'stderr.log') -Encoding utf8NoBOM
    }
    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
        throw "WinUI exited with code $($process.ExitCode) before writing a Loaded/UI test report. See stderr.log."
    }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($process.ExitCode -ne 0 -or $report.ok -ne $true) {
        throw "WinUI smoke test failed (exit $($process.ExitCode)). Report: $(Get-Content -LiteralPath $reportPath -Raw)"
    }
    if ($report.schema -ne 'ournotes-director/desktop-smoke/1') { throw 'The WinUI test report has an unexpected schema.' }
    if ($report.startup.uiLoaded -ne $true -or $report.startup.windowHandle -eq 0 -or $report.startup.width -le 0 -or $report.startup.height -le 0) {
        throw 'The report does not prove that a real WinUI window and XAML surface loaded.'
    }
    foreach ($runtimePath in @($report.startup.runtimeDirectory, $report.startup.winUiNativePath)) {
        if (-not ([IO.Path]::GetFullPath($runtimePath)).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "The GUI loaded an external runtime instead of its packaged dependency: $runtimePath"
        }
    }
    $checkCount = @($report.checks).Count
    if ($report.checks -isnot [Array] -or $report.pages -isnot [Array] -or $checkCount -eq 0 -or @($report.pages).Count -eq 0) {
        throw 'No actual page and command checks were reported.'
    }
    if (@($report.pages).Count -ne 3 -or $report.pages -notcontains 'scenes' -or $report.pages -notcontains 'home' -or $report.pages -notcontains 'live2d') {
        throw 'The application must expose the scene, Live2D and home studios as three primary pages.'
    }
    if (@($report.captures).Count -lt 2) { throw 'The smoke test did not capture its initial and final WinUI surfaces.' }
    foreach ($capture in $report.captures) {
        if ($capture.source -ne 'WinUI RenderTargetBitmap') { throw 'The screenshot did not come from the running WinUI surface.' }
        $png = [IO.File]::ReadAllBytes($capture.path)
        if ($png.Length -lt 24 -or [Convert]::ToHexString([byte[]]$png[0..7]) -ne '89504E470D0A1A0A') { throw "Invalid screenshot PNG: $($capture.path)" }
        $width = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($png, 16))
        $height = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($png, 20))
        if ($width -ne $capture.width -or $height -ne $capture.height -or $width -le 0 -or $height -le 0) {
            throw "The rendered screenshot dimensions do not match the UI report: $($capture.path)"
        }
    }
    Write-Output "Passed $checkCount real WinUI checks across $(@($report.pages).Count) pages; $(@($report.captures).Count) rendered screenshots."
    Write-Output "Verified $(@($manifest.files).Count) desktop files and the unchanged Native AOT backend. Report: $reportPath"
}
finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
