[CmdletBinding()]
param(
    [string]$Version = 'latest',
    [string]$Repository = 'trtiger4520/token-dashboard',
    [string]$InstallDirectory = '',
    [switch]$NoPath,
    [switch]$Help
)

$ErrorActionPreference = 'Stop'
$assetName = 'token-dashboard-win-x64.zip'
$userAgent = 'token-dashboard-installer'

if ($Help) {
    Write-Output 'Usage: install.ps1 [-Version latest|VERSION] [-InstallDirectory PATH] [-NoPath]'
    return
}

function Get-ReleaseTag([string]$RequestedVersion) {
    if ($RequestedVersion -eq 'latest') {
        return 'latest'
    }

    if ($RequestedVersion.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) {
        return $RequestedVersion
    }

    return "v$RequestedVersion"
}

function Get-ExpectedHash([string]$Checksums, [string]$Asset) {
    $escapedAsset = [Regex]::Escape($Asset)
    $match = ($Checksums -split "`r?`n" | Where-Object { $_ -match "^(?<hash>[0-9a-fA-F]{64})\s+\*?$escapedAsset$" } | Select-Object -First 1)
    if ($null -eq $match) {
        throw "Checksum was not found for $Asset"
    }

    return ([Regex]::Match($match, '^[0-9a-fA-F]{64}')).Value.ToLowerInvariant()
}

function Add-UserPath([string]$Path) {
    $current = [Environment]::GetEnvironmentVariable('Path', 'User')
    $entries = @($current -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if (-not ($entries | Where-Object { [StringComparer]::OrdinalIgnoreCase.Equals($_.TrimEnd('\'), $Path.TrimEnd('\')) })) {
        $updated = (($entries + $Path) -join ';')
        [Environment]::SetEnvironmentVariable('Path', $updated, 'User')
    }
}

if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        throw 'LOCALAPPDATA is required for a user-scoped installation'
    }

    $InstallDirectory = Join-Path $env:LOCALAPPDATA 'TokenDashboard'
}

$tag = Get-ReleaseTag $Version
$downloadBase = if ($tag -eq 'latest') {
    "https://github.com/$Repository/releases/latest/download"
}
else {
    "https://github.com/$Repository/releases/download/$tag"
}

$root = [IO.Path]::GetFullPath($InstallDirectory)
$currentDirectory = Join-Path $root 'current'
$previousDirectory = Join-Path $root 'previous'
$binDirectory = Join-Path $root 'bin'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('token-dashboard-install-' + [Guid]::NewGuid().ToString('N'))
$archivePath = Join-Path $tempRoot $assetName
$checksumPath = Join-Path $tempRoot 'SHA256SUMS'
$extractedDirectory = Join-Path $tempRoot 'extracted'
$stageDirectory = Join-Path $tempRoot 'stage'
$currentMoved = $false

try {
    $null = New-Item -ItemType Directory -Path $tempRoot -Force
    Invoke-WebRequest -Uri "$downloadBase/$assetName" -OutFile $archivePath -Headers @{ 'User-Agent' = $userAgent }
    Invoke-WebRequest -Uri "$downloadBase/SHA256SUMS" -OutFile $checksumPath -Headers @{ 'User-Agent' = $userAgent }

    $expectedHash = Get-ExpectedHash (Get-Content -LiteralPath $checksumPath -Raw) $assetName
    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "Checksum verification failed for $assetName"
    }

    if (Get-Process -Name 'TokenDashboard.Api' -ErrorAction SilentlyContinue) {
        throw 'TokenDashboard is running; close it before updating'
    }

    $null = New-Item -ItemType Directory -Path $extractedDirectory -Force
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractedDirectory -Force
    $executable = Join-Path $extractedDirectory 'TokenDashboard.Api.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw 'The release archive did not contain TokenDashboard.Api.exe'
    }

    $installedVersion = (& $executable '--version').Trim()
    if ([string]::IsNullOrWhiteSpace($installedVersion)) {
        throw 'The published executable did not report a version'
    }
    if ($tag -ne 'latest' -and $installedVersion -ne $tag.TrimStart('v')) {
        throw "Executable version $installedVersion does not match requested release $tag"
    }

    $null = New-Item -ItemType Directory -Path $stageDirectory -Force
    Get-ChildItem -LiteralPath $extractedDirectory -Force | Copy-Item -Destination $stageDirectory -Recurse -Force
    $null = New-Item -ItemType Directory -Path $root -Force

    if (Test-Path -LiteralPath $previousDirectory) {
        Remove-Item -LiteralPath $previousDirectory -Recurse -Force
    }
    if (Test-Path -LiteralPath $currentDirectory) {
        Move-Item -LiteralPath $currentDirectory -Destination $previousDirectory
        $currentMoved = $true
    }

    Move-Item -LiteralPath $stageDirectory -Destination $currentDirectory
    $null = New-Item -ItemType Directory -Path $binDirectory -Force
    $launcher = Join-Path $binDirectory 'token-dashboard.cmd'
    Set-Content -LiteralPath $launcher -Encoding ascii -Value @(
        '@echo off'
        '"%~dp0..\current\TokenDashboard.Api.exe" %*'
    )
    $manifest = [ordered]@{
        version = $installedVersion
        runtime = 'win-x64'
        installedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'install-manifest.json') -Encoding utf8

    if (-not $NoPath) {
        Add-UserPath $binDirectory
        $env:Path = "$binDirectory;$env:Path"
    }

    Write-Output "Installed Token Dashboard $installedVersion"
    Write-Output "Run: token-dashboard"
}
catch {
    if ($currentMoved -and -not (Test-Path -LiteralPath $currentDirectory) -and (Test-Path -LiteralPath $previousDirectory)) {
        Move-Item -LiteralPath $previousDirectory -Destination $currentDirectory
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
