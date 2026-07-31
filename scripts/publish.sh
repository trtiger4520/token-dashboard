#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"

if [[ "$#" -eq 0 ]]; then
    set -- win-x64 linux-x64 osx-arm64
fi

version=''
output_root='artifacts/publish'
runtimes=()
while [[ "$#" -gt 0 ]]; do
    case "$1" in
        --version)
            version="${2:?--version requires a value}"
            shift 2
            ;;
        --output-root)
            output_root="${2:?--output-root requires a value}"
            shift 2
            ;;
        --*)
            echo "Unknown option: $1" >&2
            exit 2
            ;;
        *)
            runtimes+=("$1")
            shift
            ;;
    esac
done

if [[ "${#runtimes[@]}" -eq 0 ]]; then
    runtimes=(win-x64 linux-x64 osx-arm64)
fi

pnpm install --frozen-lockfile
pnpm --filter token-dashboard-web build
dotnet restore TokenDashboard.slnx

for rid in "${runtimes[@]}"; do
    dotnet restore src/TokenDashboard.Api/TokenDashboard.Api.csproj --runtime "$rid"
    version_arguments=()
    if [[ -n "$version" ]]; then
        version_arguments=(-p:Version="$version" -p:InformationalVersion="$version")
    fi
    dotnet publish src/TokenDashboard.Api/TokenDashboard.Api.csproj \
        --configuration Release \
        --runtime "$rid" \
        --self-contained true \
        --output "$output_root/$rid" \
        --no-restore \
        "${version_arguments[@]}"
done
