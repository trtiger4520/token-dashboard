#!/usr/bin/env bash
set -euo pipefail

version=''
publish_root='artifacts/publish'
output_root='artifacts/release'
runtime=''

while [[ "$#" -gt 0 ]]; do
    case "$1" in
        --version)
            version="${2:?--version requires a value}"
            shift 2
            ;;
        --publish-root)
            publish_root="${2:?--publish-root requires a value}"
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
            if [[ -n "$runtime" ]]; then
                echo 'Only one runtime can be packaged per invocation' >&2
                exit 2
            fi
            runtime="$1"
            shift
            ;;
    esac
done

case "$runtime" in
    win-x64) archive_name='token-dashboard-win-x64.zip' ;;
    linux-x64) archive_name='token-dashboard-linux-x64.tar.gz' ;;
    osx-arm64) archive_name='token-dashboard-osx-arm64.tar.gz' ;;
    *) echo 'Runtime must be win-x64, linux-x64 or osx-arm64' >&2; exit 2 ;;
esac

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source="$repository_root/$publish_root/$runtime"
output_directory="$repository_root/$output_root"
staging_root="$(mktemp -d "${TMPDIR:-/tmp}/token-dashboard-package.XXXXXX")"
staging="$staging_root/payload"
archive_path="$output_directory/$archive_name"
mkdir -p "$staging" "$output_directory"
trap 'rm -rf "$staging_root"' EXIT

if [[ ! -d "$source" ]]; then
    echo "Publish directory was not found: $source" >&2
    exit 1
fi

cp -R "$source"/. "$staging"/
find "$staging" -type f -name '*.pdb' -delete
printf '%s' "$version" > "$staging/VERSION"
rm -f "$archive_path"

if [[ "$runtime" == 'win-x64' ]]; then
    echo 'Windows archives must be built on Windows' >&2
    exit 1
fi

tar -czf "$archive_path" -C "$staging" .
