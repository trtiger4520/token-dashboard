param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'linux-x64', 'osx-x64')]
    [string]$Runtime,
    [string]$Version = '',
    [string]$PublishRoot = 'artifacts/publish',
    [string]$OutputRoot = 'artifacts/release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repositoryRoot (Join-Path $PublishRoot $Runtime)
$outputDirectory = Join-Path $repositoryRoot $OutputRoot

if (-not (Test-Path -LiteralPath $source -PathType Container)) {
    throw "Publish directory was not found: $source"
}

$archiveName = switch ($Runtime) {
    'win-x64' { 'token-dashboard-win-x64.zip' }
    'linux-x64' { 'token-dashboard-linux-x64.tar.gz' }
    'osx-x64' { 'token-dashboard-osx-x64.tar.gz' }
}

$null = New-Item -ItemType Directory -Path $outputDirectory -Force
$stagingRoot = Join-Path ([IO.Path]::GetTempPath()) ('token-dashboard-package-' + [Guid]::NewGuid().ToString('N'))
$staging = Join-Path $stagingRoot 'payload'
$archivePath = Join-Path $outputDirectory $archiveName

try {
    $null = New-Item -ItemType Directory -Path $staging -Force
    Get-ChildItem -LiteralPath $source -File -Recurse |
        Where-Object { $_.Extension -ne '.pdb' } |
        ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($source, $_.FullName)
            $target = Join-Path $staging $relative
            $targetDirectory = Split-Path -Parent $target
            $null = New-Item -ItemType Directory -Path $targetDirectory -Force
            Copy-Item -LiteralPath $_.FullName -Destination $target -Force
        }

    Set-Content -LiteralPath (Join-Path $staging 'VERSION') -Value $Version -NoNewline -Encoding utf8
    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }

    if ($Runtime -eq 'win-x64') {
        Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $archivePath -CompressionLevel Optimal
    }
    else {
        tar -czf $archivePath -C $staging .
        if ($LASTEXITCODE -ne 0) { throw "Archive creation failed for $Runtime" }
    }
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
