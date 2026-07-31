#!/usr/bin/env bash
set -euo pipefail

version='latest'
repository='trtiger4520/token-dashboard'
install_directory=''
no_path=false

while [[ "$#" -gt 0 ]]; do
    case "$1" in
        --version)
            version="${2:?--version requires a value}"
            shift 2
            ;;
        --repository)
            repository="${2:?--repository requires a value}"
            shift 2
            ;;
        --install-dir)
            install_directory="${2:?--install-dir requires a value}"
            shift 2
            ;;
        --no-path)
            no_path=true
            shift
            ;;
        --help)
            echo 'Usage: install.sh [--version latest|VERSION] [--install-dir PATH] [--no-path]'
            exit 0
            ;;
        *)
            echo "Unknown option: $1" >&2
            exit 2
            ;;
    esac
done

case "$(uname -s)" in
    Linux) runtime='linux-x64' ;;
    Darwin) runtime='osx-arm64' ;;
    *) echo 'Only Linux and macOS are supported by this installer' >&2; exit 1 ;;
esac

case "$runtime:$(uname -m)" in
    linux-x64:x86_64|linux-x64:amd64|osx-arm64:arm64|osx-arm64:aarch64) ;;
    linux-x64:*) echo 'Only x64 Linux is supported by this installer' >&2; exit 1 ;;
    osx-arm64:*) echo 'Only Apple Silicon macOS is supported by this installer' >&2; exit 1 ;;
esac

command -v curl >/dev/null 2>&1 || { echo 'curl is required' >&2; exit 1; }
command -v tar >/dev/null 2>&1 || { echo 'tar is required' >&2; exit 1; }

if [[ -z "$install_directory" ]]; then
    if [[ "$runtime" == 'osx-arm64' ]]; then
        install_directory="$HOME/Library/Application Support/TokenDashboard"
    else
        install_directory="${XDG_DATA_HOME:-$HOME/.local/share}/token-dashboard"
    fi
fi

if [[ "$version" == 'latest' ]]; then
    download_base="https://github.com/$repository/releases/latest/download"
else
    tag="$version"
    [[ "$tag" == v* ]] || tag="v$tag"
    download_base="https://github.com/$repository/releases/download/$tag"
fi

case "$runtime" in
    linux-x64) asset_name='token-dashboard-linux-x64.tar.gz' ;;
    osx-arm64) asset_name='token-dashboard-osx-arm64.tar.gz' ;;
esac

root="$(cd "$install_directory" 2>/dev/null && pwd)" || root="$install_directory"
current_directory="$root/current"
previous_directory="$root/previous"
bin_directory="$HOME/.local/bin"
temp_root="$(mktemp -d "${TMPDIR:-/tmp}/token-dashboard-install.XXXXXX")"
archive_path="$temp_root/$asset_name"
checksum_path="$temp_root/SHA256SUMS"
extracted_directory="$temp_root/extracted"
stage_directory="$temp_root/stage"
current_moved=false
cleanup() { rm -rf "$temp_root"; }
trap cleanup EXIT

curl --fail --location --silent --show-error "$download_base/$asset_name" --output "$archive_path"
curl --fail --location --silent --show-error "$download_base/SHA256SUMS" --output "$checksum_path"

expected_hash="$(awk -v asset="$asset_name" '$2 == asset || $2 == "*" asset { print tolower($1); exit }' "$checksum_path")"
if [[ ! "$expected_hash" =~ ^[0-9a-f]{64}$ ]]; then
    echo "Checksum was not found for $asset_name" >&2
    exit 1
fi

if command -v sha256sum >/dev/null 2>&1; then
    actual_hash="$(sha256sum "$archive_path" | awk '{print tolower($1)}')"
else
    actual_hash="$(shasum -a 256 "$archive_path" | awk '{print tolower($1)}')"
fi
if [[ "$actual_hash" != "$expected_hash" ]]; then
    echo "Checksum verification failed for $asset_name" >&2
    exit 1
fi

if pgrep -x TokenDashboard.Api >/dev/null 2>&1; then
    echo 'TokenDashboard is running; close it before updating' >&2
    exit 1
fi

mkdir -p "$extracted_directory" "$stage_directory"
tar -xzf "$archive_path" -C "$extracted_directory"
executable="$extracted_directory/TokenDashboard.Api"
if [[ ! -x "$executable" ]]; then
    echo 'The release archive did not contain an executable TokenDashboard.Api' >&2
    exit 1
fi

installed_version="$($executable --version)"
if [[ -z "$installed_version" ]]; then
    echo 'The published executable did not report a version' >&2
    exit 1
fi
if [[ "$version" != 'latest' ]]; then
    requested_version="${version#v}"
    if [[ "$installed_version" != "$requested_version" ]]; then
        echo "Executable version $installed_version does not match requested release $version" >&2
        exit 1
    fi
fi

cp -R "$extracted_directory"/. "$stage_directory"/
mkdir -p "$root"
if [[ -d "$previous_directory" ]]; then
    rm -rf "$previous_directory"
fi
if [[ -d "$current_directory" ]]; then
    mv "$current_directory" "$previous_directory"
    current_moved=true
fi

if ! mv "$stage_directory" "$current_directory"; then
    if [[ "$current_moved" == true && ! -d "$current_directory" && -d "$previous_directory" ]]; then
        mv "$previous_directory" "$current_directory"
    fi
    exit 1
fi

mkdir -p "$bin_directory"
launcher="$bin_directory/token-dashboard"
printf '%s\n' '#!/usr/bin/env sh' "exec \"$current_directory/TokenDashboard.Api\" \"\$@\"" > "$launcher"
chmod +x "$launcher" "$current_directory/TokenDashboard.Api"
printf '%s\n' "{\"version\":\"$installed_version\",\"runtime\":\"$runtime\",\"installedAtUtc\":\"$(date -u +%Y-%m-%dT%H:%M:%SZ)\"}" > "$root/install-manifest.json"

if [[ "$no_path" == false ]]; then
    case "$(uname -s)" in
        Darwin) path_file="$HOME/.zprofile" ;;
        *) path_file="$HOME/.profile" ;;
    esac
    touch "$path_file"
    if ! grep -Fqx 'export PATH="$HOME/.local/bin:$PATH"' "$path_file"; then
        printf '%s\n' 'export PATH="$HOME/.local/bin:$PATH"' >> "$path_file"
    fi
fi

echo "Installed Token Dashboard $installed_version"
echo 'Run: token-dashboard'
