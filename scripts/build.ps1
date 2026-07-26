param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repositoryRoot
try {
    pnpm --dir 'src/TokenDashboard.Web' install --lockfile=false
    if ($LASTEXITCODE -ne 0) { throw 'Web dependency installation failed' }

    pnpm --dir 'src/TokenDashboard.Web' build
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
