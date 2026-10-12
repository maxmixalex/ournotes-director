[CmdletBinding()]
param(
    [string] $OutputDirectory = 'artifacts/win-x64',
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [switch] $KeepPublishOutput
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'The WinUI frontend must be built on Windows.' }
$repository = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repository 'src/OurNotes.Director.WinUI/OurNotes.Director.WinUI.csproj'
if (-not [IO.Path]::IsPathFullyQualified($OutputDirectory)) {
    $OutputDirectory = Join-Path $repository $OutputDirectory
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$backend = Join-Path $OutputDirectory 'advkit.exe'
if (-not (Test-Path -LiteralPath $backend -PathType Leaf)) {
    throw "Publish and stage the Native AOT backend first; missing $backend"
}

# Keep the already verified worker, SDK, media tools, and source archives unchanged.
$protectedFiles = @((Get-Item -LiteralPath $backend))
foreach ($name in @('native', 'tools', 'media-sources')) {
    $directory = Join-Path $OutputDirectory $name
    if (Test-Path -LiteralPath $directory) {
        $protectedFiles += @(Get-ChildItem -LiteralPath $directory -File -Recurse)
    }
}
$protectedHashes = @{}
foreach ($file in $protectedFiles) {
    $protectedHashes[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
}

$publishDirectory = Join-Path $repository ('artifacts/desktop-publish/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
try {
    $publishArguments = @(
        'publish', $project, '--configuration', $Configuration,
        '--runtime', 'win-x64', '--self-contained', 'true', '--nologo',
        '-p:Platform=x64', '-p:WindowsPackageType=None',
        '-p:WindowsAppSDKSelfContained=true', '-p:PublishAot=false',
        '-p:PublishTrimmed=false', '-p:PublishSingleFile=false',
        "-p:PublishDir=$publishDirectory"
    )
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw "WinUI publish failed with exit code $LASTEXITCODE." }
    foreach ($name in @(
        'OurNotes.Director.exe', 'OurNotes.Director.dll', 'OurNotes.Director.deps.json',
        'OurNotes.Director.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll',
        'hostpolicy.dll', 'Microsoft.UI.Xaml.dll', 'resources.pri'
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $name) -PathType Leaf)) {
            throw "The self-contained WinUI publish is incomplete: $name is missing."
        }
    }

    # Carry the exact resolved packages' license/notice files and NuGet metadata.
    $assetsPath = Join-Path (Split-Path $project -Parent) 'obj/project.assets.json'
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
    $packageKeys = @($assets.libraries.Keys | Where-Object { $assets.libraries[$_].type -eq 'package' })
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $publishDirectory 'OurNotes.Director.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
    foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
        $packageKeys += "$($framework.name).Runtime.win-x64/$($framework.version)"
    }
    # Framework packs (including Windows.SDK.NET.Ref / WinRT.Runtime) are downloads,
    # not ordinary entries in the NuGet libraries table.
    foreach ($framework in $assets.project.frameworks.Values) {
        if (-not $framework.ContainsKey('downloadDependencies')) { continue }
        foreach ($download in $framework.downloadDependencies) {
            if ($download.version -notmatch '^\[(?<version>[^,\]]+)(?:,\s*\k<version>)?\]$') {
                throw "Cannot resolve an exact framework license version: $($download.name) $($download.version)"
            }
            $packageKeys += "$($download.name)/$($Matches['version'])"
        }
    }
    $packageLicenses = @()
    foreach ($key in ($packageKeys | Sort-Object -Unique)) {
        $packageDirectory = $null
        foreach ($cache in $assets.packageFolders.Keys) {
            $candidate = Join-Path $cache $key.ToLowerInvariant()
            if (Test-Path -LiteralPath $candidate -PathType Container) { $packageDirectory = [IO.Path]::GetFullPath($candidate); break }
        }
        if ($null -eq $packageDirectory) { throw "Missing resolved NuGet package while collecting licenses: $key" }
        $noticeFiles = @(Get-ChildItem -LiteralPath $packageDirectory -File -Recurse | Where-Object {
            $_.Name -match '(?i)^(licen[cs]e|notice|third[-_. ]?party[-_. ]?notices?|copying)([._-].*)?$' -or $_.Extension -eq '.nuspec'
        })
        $specFiles = @(Get-ChildItem -LiteralPath $packageDirectory -File -Filter '*.nuspec')
        if ($specFiles.Count -ne 1) { throw "Expected one NuGet metadata file for $key." }
        [xml] $specification = Get-Content -LiteralPath $specFiles[0].FullName -Raw
        $license = $specification.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='license']")
        $licenseType = 'url'
        $licenseValue = ''
        if ($null -ne $license) {
            $licenseType = $license.GetAttribute('type')
            $licenseValue = $license.InnerText.Trim()
            if ($licenseType -eq 'file') {
                $licensePath = [IO.Path]::GetFullPath((Join-Path $packageDirectory $licenseValue))
                $packagePrefix = $packageDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
                if (-not $licensePath.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase) -or
                    -not (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
                    throw "The declared NuGet license file is missing or invalid: $key / $licenseValue"
                }
                # Some packages use names such as sdk_license.txt instead of LICENSE.
                $noticeFiles += Get-Item -LiteralPath $licensePath
            }
        }
        else {
            $licenseUrl = $specification.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='licenseUrl']")
            if ($null -ne $licenseUrl) { $licenseValue = $licenseUrl.InnerText.Trim() }
        }
        if ([string]::IsNullOrWhiteSpace($licenseValue)) { throw "The resolved NuGet package has no license declaration: $key" }
        $noticeFiles = @($noticeFiles | Sort-Object -Property FullName -Unique)
        $noticePaths = @()
        foreach ($notice in $noticeFiles) {
            $relative = [IO.Path]::GetRelativePath($packageDirectory, $notice.FullName)
            $destination = Join-Path $publishDirectory "licenses/desktop/$key/$relative"
            New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $notice.FullName -Destination $destination
            $noticePaths += "licenses/desktop/$key/$($relative.Replace('\', '/'))"
        }
        $packageLicenses += [ordered]@{
            package = $key
            licenseType = $licenseType
            license = $licenseValue
            notices = $noticePaths
        }
    }
    Copy-Item -LiteralPath (Join-Path $repository 'DESKTOP.md') -Destination (Join-Path $publishDirectory 'DESKTOP.md')

    $manifestFiles = @()
    foreach ($file in (Get-ChildItem -LiteralPath $publishDirectory -File -Recurse | Sort-Object FullName)) {
        $relative = [IO.Path]::GetRelativePath($publishDirectory, $file.FullName)
        $destination = [IO.Path]::GetFullPath((Join-Path $OutputDirectory $relative))
        if ($protectedHashes.ContainsKey($destination)) {
            if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $protectedHashes[$destination]) {
                throw "The frontend would replace a protected backend file: $relative"
            }
        }
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        $manifestFiles += [ordered]@{
            file = $relative.Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
            size = (Get-Item -LiteralPath $destination).Length
        }
    }
    foreach ($path in $protectedHashes.Keys) {
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $protectedHashes[$path]) {
            throw "Staging changed an existing backend dependency: $path"
        }
    }
    $manifest = [ordered]@{
        schema = 'ournotes.desktop.bundle/v1'
        runtimeIdentifier = 'win-x64'
        windowsAppSdk = '2.5.1'
        windowsSdkBuildTools = '10.0.26100.9169'
        minimumWindowsVersion = '10.0.17763.0'
        deployment = 'unpackaged-self-contained'
        executable = 'OurNotes.Director.exe'
        worker = 'advkit.exe'
        workerSha256 = $protectedHashes[$backend].ToLowerInvariant()
        dotnetFrameworks = @($runtimeConfig.runtimeOptions.includedFrameworks)
        nugetPackages = $packageLicenses
        files = $manifestFiles
    }
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'desktop-dependencies.json') -Encoding utf8NoBOM
    Write-Output "Staged self-contained WinUI frontend and $($manifestFiles.Count) verified files: $OutputDirectory"
}
finally {
    if (-not $KeepPublishOutput -and (Test-Path -LiteralPath $publishDirectory)) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }
}
