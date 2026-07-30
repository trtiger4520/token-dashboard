param(
    [string[]]$Runtime = @('win-x64', 'linux-x64', 'osx-arm64'),
    [string]$Version = '',
    [string]$OutputRoot = 'artifacts/publish'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repositoryRoot
try {
    pnpm install --frozen-lockfile
    if ($LASTEXITCODE -ne 0) { throw 'Web dependency installation failed' }

    pnpm --filter 'token-dashboard-web' build
    if ($LASTEXITCODE -ne 0) { throw 'Web build failed' }

    dotnet restore 'TokenDashboard.slnx'
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed' }

    foreach ($rid in $Runtime) {
        $output = Join-Path $repositoryRoot (Join-Path $OutputRoot $rid)
        dotnet restore 'src/TokenDashboard.Api/TokenDashboard.Api.csproj' --runtime $rid
        if ($LASTEXITCODE -ne 0) { throw "runtime restore failed for $rid" }

        $versionArguments = @()
        if (-not [string]::IsNullOrWhiteSpace($Version)) {
            $versionArguments = @("-p:Version=$Version", "-p:InformationalVersion=$Version")
        }

        dotnet publish 'src/TokenDashboard.Api/TokenDashboard.Api.csproj' --configuration Release --runtime $rid --self-contained true --output $output --no-restore @versionArguments
        if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }
    }
}
finally {
    Pop-Location
}
