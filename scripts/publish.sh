#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"

if [[ "$#" -eq 0 ]]; then
    set -- win-x64 linux-x64 osx-x64
fi

pnpm --dir src/TokenDashboard.Web install --lockfile=false
pnpm --dir src/TokenDashboard.Web build
dotnet restore TokenDashboard.slnx

for rid in "$@"; do
    dotnet restore src/TokenDashboard.Api/TokenDashboard.Api.csproj --runtime "$rid"
    dotnet publish src/TokenDashboard.Api/TokenDashboard.Api.csproj \
        --configuration Release \
        --runtime "$rid" \
        --self-contained false \
        --output "artifacts/publish/$rid" \
        --no-restore
done
