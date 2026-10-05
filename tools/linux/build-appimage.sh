#!/usr/bin/env bash
# Builds the Linux app as an AppImage and a tar.gz into dist/. Runs on Linux (CI uses it).
# Usage: tools/linux/build-appimage.sh <version>
set -euo pipefail
version="${1:?usage: build-appimage.sh <version>}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
work="$root/build/linux"
app="$work/Palwyn.AppDir"
rm -rf "$work" && mkdir -p "$app/usr/bin" "$root/dist"

# Self-contained: no .NET install needed on the user's PC.
dotnet publish "$root/apps/linux/src/Palwyn.Linux" -c Release -r linux-x64 --self-contained \
  -p:Version="$version" -o "$app/usr/bin"
tar -C "$app/usr/bin" -czf "$root/dist/Palwyn-$version-linux-x64.tar.gz" .

cp "$root/site/icon-192.png" "$app/palwyn.png"
cat > "$app/palwyn.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Palwyn
Comment=Your Android phone on this PC
Exec=palwyn-linux
Icon=palwyn
Categories=Utility;
EOF
ln -s usr/bin/palwyn-linux "$app/AppRun"

# appimagetool, pinned and checked against the SHA-256 GitHub records for the release asset.
tool="$work/appimagetool"
curl -fsSL -o "$tool" https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage
echo "ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0  $tool" | sha256sum -c -
chmod +x "$tool"
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$tool" "$app" "$root/dist/Palwyn-$version-linux-x86_64.AppImage"
