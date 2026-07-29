param()

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

    dotnet build 'TokenDashboard.slnx' --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }

    dotnet test 'TokenDashboard.slnx' --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed' }
}
finally {
    Pop-Location
}
