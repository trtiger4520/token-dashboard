#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"

pnpm install --frozen-lockfile
pnpm --filter token-dashboard-web build
dotnet restore TokenDashboard.slnx
dotnet build TokenDashboard.slnx --no-restore
dotnet test TokenDashboard.slnx --no-restore
