param(
    [string[]]$Runtime = @('win-x64', 'linux-x64', 'osx-x64')
)

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

    foreach ($rid in $Runtime) {
        $output = Join-Path $repositoryRoot ('artifacts/publish/' + $rid)
        dotnet restore 'src/TokenDashboard.Api/TokenDashboard.Api.csproj' --runtime $rid
        if ($LASTEXITCODE -ne 0) { throw "runtime restore failed for $rid" }

        dotnet publish 'src/TokenDashboard.Api/TokenDashboard.Api.csproj' --configuration Release --runtime $rid --self-contained false --output $output --no-restore
        if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }
    }
}
finally {
    Pop-Location
}
