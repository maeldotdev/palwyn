#!/usr/bin/env bash
# Puts the files the Linux app bundles but git doesn't keep into apps/linux/src/Palwyn.Linux/Assets:
# - adb (Android SDK Platform-Tools, Apache-2.0) in Assets/adb, pinned and checked against the SHA-1 Google publishes
#   for it in its repository index (repository2-3.xml)
# - the emergency-screen phone helper in Assets/Emergency/palwyn-emergency.jar, built from apps/android/emergency
# Usage: tools/linux/fetch-deps.sh [--skip-emergency]
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
assets="$root/apps/linux/src/Palwyn.Linux/Assets"

version=r37.0.1
sha1=477254aa5f903c15cf51001717bdf347fb6b53e0
if [ "$(cat "$assets/adb/version.txt" 2>/dev/null)" != "$version" ]; then
  tmp="$(mktemp -d)"
  curl -fsSL -o "$tmp/platform-tools.zip" "https://dl.google.com/android/repository/platform-tools_${version}-linux.zip"
  echo "$sha1  $tmp/platform-tools.zip" | sha1sum -c -
  unzip -q "$tmp/platform-tools.zip" -d "$tmp"
  mkdir -p "$assets/adb"
  cp "$tmp/platform-tools/adb" "$tmp/platform-tools/NOTICE.txt" "$assets/adb/"
  chmod +x "$assets/adb/adb"
  echo "$version" > "$assets/adb/version.txt"
  rm -rf "$tmp"
  echo "adb $version -> $assets/adb"
fi

[ "${1:-}" = "--skip-emergency" ] && exit 0
(cd "$root/apps/android" && ./gradlew :emergency:assembleRelease --console=plain -q)
mkdir -p "$assets/Emergency"
cp "$root/apps/android/emergency/build/outputs/apk/release/emergency-release-unsigned.apk" "$assets/Emergency/palwyn-emergency.jar"
echo "emergency helper -> $assets/Emergency/palwyn-emergency.jar"
