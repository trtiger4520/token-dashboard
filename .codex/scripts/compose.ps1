[CmdletBinding()]
param(
    [ValidateSet('up', 'status')]
    [string]$Action = 'up'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$runtimeDirectory = Join-Path $repositoryRoot '.codex\environments\runtime'
$runtimeEnvironmentFile = Join-Path $runtimeDirectory 'compose.env'

function Get-NormalizedBranchName {
    $branchName = (& git -c "safe.directory=$repositoryRoot" -C $repositoryRoot branch --show-current).Trim()
    if ([string]::IsNullOrWhiteSpace($branchName)) {
        $commit = (& git -c "safe.directory=$repositoryRoot" -C $repositoryRoot rev-parse --short HEAD).Trim()
        $branchName = "detached-$commit"
    }

    $normalizedName = ($branchName.ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-')
    if ([string]::IsNullOrWhiteSpace($normalizedName)) {
        return 'worktree'
    }

    return $normalizedName.Substring(0, [Math]::Min(32, $normalizedName.Length))
}

function Get-WorktreeHash {
    $bytes = [Text.Encoding]::UTF8.GetBytes($repositoryRoot.ToLowerInvariant())
    $hashBytes = [Security.Cryptography.SHA256]::HashData($bytes)
    return [Convert]::ToHexString($hashBytes).Substring(0, 8).ToLowerInvariant()
}

function Get-AvailablePort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Get-ComposeEnvironment {
    if (Test-Path -LiteralPath $runtimeEnvironmentFile) {
        $values = ConvertFrom-StringData -StringData (Get-Content -LiteralPath $runtimeEnvironmentFile -Raw)
        if ($values.ContainsKey('TOKEN_DASHBOARD_PORT') -and $values.ContainsKey('COMPOSE_PROJECT_NAME')) {
            return $values
        }
    }

    $projectName = "token-dashboard-$(Get-NormalizedBranchName)-$(Get-WorktreeHash)"
    $port = Get-AvailablePort
    New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
    @(
        "COMPOSE_PROJECT_NAME=$projectName"
        "TOKEN_DASHBOARD_PORT=$port"
    ) | Set-Content -LiteralPath $runtimeEnvironmentFile -Encoding utf8

    return @{
        COMPOSE_PROJECT_NAME = $projectName
        TOKEN_DASHBOARD_PORT = $port.ToString()
    }
}

$composeEnvironment = Get-ComposeEnvironment
$composeArguments = @(
    '--project-name', $composeEnvironment['COMPOSE_PROJECT_NAME'],
    '--env-file', $runtimeEnvironmentFile
)

if ($Action -eq 'up') {
    $composeArguments += @('up', '-d')
}
else {
    $composeArguments += @('ps')
}

Write-Output "Compose project: $($composeEnvironment['COMPOSE_PROJECT_NAME'])"
Write-Output "Dashboard URL: http://127.0.0.1:$($composeEnvironment['TOKEN_DASHBOARD_PORT'])"
& docker compose @composeArguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
