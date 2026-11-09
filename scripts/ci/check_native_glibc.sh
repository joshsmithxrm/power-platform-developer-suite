#!/usr/bin/env bash
set -euo pipefail
# Invoked only inside the disposable, network-disabled/read-only UBI8 container.
test "$(id -u)" = 10001
test "$(getconf GNU_LIBC_VERSION)" = "glibc 2.28"
echo "PPDS_GLIBC=$(getconf GNU_LIBC_VERSION)" >&2
root=$(mktemp -d /tmp/ppds-native10.XXXXXX)
chmod 700 "$root"
mkdir -m 700 "$root/home" "$root/config" "$root/data" "$root/temp" "$root/inputs"
export HOME="$root/home" USERPROFILE="$root/home" PPDS_CONFIG_DIR="$root/config"
export XDG_CONFIG_HOME="$root/config" XDG_DATA_HOME="$root/data"
export TEMP="$root/temp" TMP="$root/temp" TMPDIR="$root/temp"
export DOTNET_CLI_HOME="$root/home" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_BUNDLE_EXTRACT_BASE_DIR="$root/temp/bundle"
cp /fixture/SingleFilePlugin.dll /fixture/PPDS.Plugins.dll "$root/inputs/"
command -v timeout >/dev/null
version=$(timeout 30s /native/ppds --version)
printf 'PPDS_NATIVE_VERSION=%s\n' "$version" >&2
# SDK/BCL sidecars are deliberately absent from this isolated input directory.
timeout 60s /native/ppds plugins extract --input "$root/inputs/SingleFilePlugin.dll" --output-format json --quiet
test ! -f "$root/config/profiles.json"
# Nothing in host storage is removed; Docker owns this container's tmpfs lifetime.
