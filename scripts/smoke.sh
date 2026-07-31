#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
publish_directory="${1:-$repository_root/artifacts/publish/linux-x64}"
publish_directory="$(cd "$publish_directory" && pwd)"
executable="$publish_directory/TokenDashboard.Api"
if [[ ! -x "$executable" ]]; then
    echo "Published executable was not found: $executable" >&2
    exit 1
fi

database_path="$(mktemp "${TMPDIR:-/tmp}/token-dashboard-smoke.XXXXXX.db")"
output_path="$(mktemp "${TMPDIR:-/tmp}/token-dashboard-smoke.XXXXXX.log")"
cleanup() {
    if [[ -n "${process_id:-}" ]] && kill -0 "$process_id" 2>/dev/null; then
        kill "$process_id" 2>/dev/null || true
        wait "$process_id" 2>/dev/null || true
    fi
    rm -f "$database_path" "$database_path-shm" "$database_path-wal" "$output_path"
}
trap cleanup EXIT

(
    cd "$publish_directory"
    ASPNETCORE_ENVIRONMENT=Development \
    TokenDashboard__OpenBrowser=false \
    TokenDashboard__EmitStartupDiagnostics=true \
    TokenDashboard__ConnectionString="Data Source=$database_path" \
    "$executable"
) >"$output_path" 2>&1 &
process_id=$!

for _ in $(seq 1 300); do
    startup_line="$(grep -m1 '^TOKEN_DASHBOARD_STARTUP_URL=' "$output_path" || true)"
    if [[ -n "$startup_line" ]]; then
        startup_url="${startup_line#TOKEN_DASHBOARD_STARTUP_URL=}"
        base_url="${startup_url%%#key=*}"
        base_url="${base_url%/}"
        key="${startup_url#*#key=}"
        break
    fi
    sleep 0.1
done

if [[ -z "${base_url:-}" || -z "${key:-}" ]]; then
    echo "Timed out waiting for startup diagnostics" >&2
    cat "$output_path" >&2
    exit 1
fi

[[ "$(curl --silent --output /dev/null --write-out '%{http_code}' "$base_url/")" == '200' ]]
[[ "$(curl --silent --output /dev/null --write-out '%{http_code}' "$base_url/health")" == '200' ]]
[[ "$(curl --silent --output /dev/null --write-out '%{http_code}' "$base_url/api/overview")" == '401' ]]
[[ "$(curl --silent --output /dev/null --write-out '%{http_code}' -H "X-Token-Dashboard-Key: $key" "$base_url/api/overview")" == '200' ]]
echo "Smoke passed: $base_url"
