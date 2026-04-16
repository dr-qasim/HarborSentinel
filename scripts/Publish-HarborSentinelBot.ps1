param(
    [string]$Project = "HarborSentinel.Bot/HarborSentinel.Bot.csproj",
    [string]$Configuration = "Release",
    [string]$Runtime = "linux-x64",
    [bool]$SelfContained = $true,
    [string]$OutputRoot = "artifacts/releases",
    [switch]$CreateArchive,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot $Project
if (-not (Test-Path $projectPath)) {
    throw "Project not found: $projectPath"
}

$resolvedProject = (Resolve-Path $projectPath).Path
$version = (& dotnet msbuild $resolvedProject -nologo -getProperty:Version).Trim()
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Could not resolve project version."
}

$runtimeSuffix = if ([string]::IsNullOrWhiteSpace($Runtime)) { "portable" } else { $Runtime }
$releaseName = "HarborSentinel.Bot-v$version-$runtimeSuffix"
$releaseRoot = Join-Path $repoRoot $OutputRoot
$publishDir = Join-Path $releaseRoot $releaseName
$archivePath = Join-Path $releaseRoot ($releaseName + ".tar.gz")
$hashPath = $archivePath + ".sha256"

$publishArgs = @(
    "publish",
    $resolvedProject,
    "-c", $Configuration,
    "-o", $publishDir,
    "/p:ContinuousIntegrationBuild=true",
    "/p:Deterministic=true",
    "--self-contained", $SelfContained.ToString().ToLowerInvariant()
)

if (-not [string]::IsNullOrWhiteSpace($Runtime)) {
    $publishArgs += @("-r", $Runtime)
}

if ($DryRun) {
    Write-Output "version=$version"
    Write-Output "publish_dir=$publishDir"
    Write-Output ("dotnet " + ($publishArgs -join " "))
    if ($CreateArchive) {
        Write-Output "archive_path=$archivePath"
        Write-Output "sha256_path=$hashPath"
    }
    exit 0
}

New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}

& dotnet @publishArgs

$unsafeConfigFiles = @(
    "appsettings.json",
    "appsettings.Development.json"
)

foreach ($configFile in $unsafeConfigFiles) {
    $configPath = Join-Path $publishDir $configFile
    if (Test-Path $configPath) {
        Remove-Item -Force $configPath
    }
}

$runtimeArtifacts = @(
    "data/harborsentinel.db",
    "data/hadith_daily_settings.json",
    "data/messages-emergency-backup.jsonl",
    "data/flagged_lists",
    "data/tracked_exports",
    "data/admin_exports"
)

foreach ($relativePath in $runtimeArtifacts) {
    $artifactPath = Join-Path $publishDir $relativePath
    if (Test-Path $artifactPath) {
        Remove-Item -Recurse -Force $artifactPath
    }
}

Get-ChildItem -Path (Join-Path $publishDir "data") -Filter "messages-*.jsonl" -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

$exampleConfigSource = Join-Path (Split-Path $resolvedProject -Parent) "appsettings.json.example"
$exampleConfigTarget = Join-Path $publishDir "appsettings.json.example"
if (Test-Path $exampleConfigSource) {
    Copy-Item -Force $exampleConfigSource $exampleConfigTarget
}

$manifest = [ordered]@{
    product = "HarborSentinel.Bot"
    version = $version
    configuration = $Configuration
    runtime = $Runtime
    self_contained = $SelfContained
    built_at_utc = (Get-Date).ToUniversalTime().ToString("o")
}

$manifest
| ConvertTo-Json -Depth 4
| Set-Content -Path (Join-Path $publishDir "release-manifest.json") -Encoding utf8

if ($CreateArchive) {
    $tar = Get-Command tar -ErrorAction SilentlyContinue
    if ($null -eq $tar) {
        throw "tar executable not found. Re-run without -CreateArchive or install tar."
    }

    if (Test-Path $archivePath) {
        Remove-Item -Force $archivePath
    }

    if (Test-Path $hashPath) {
        Remove-Item -Force $hashPath
    }

    & $tar.Source -czf $archivePath -C $releaseRoot $releaseName
    $hash = Get-FileHash -Algorithm SHA256 $archivePath
    "$($hash.Hash.ToLowerInvariant()) *$(Split-Path -Leaf $archivePath)" |
        Set-Content -Path $hashPath -Encoding ascii
}

Write-Output "Prepared release: $publishDir"
if ($CreateArchive) {
    Write-Output "Archive: $archivePath"
    Write-Output "SHA256: $hashPath"
}
