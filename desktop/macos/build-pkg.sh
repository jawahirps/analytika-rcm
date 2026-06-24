#!/usr/bin/env bash
# Assemble a Bix.app bundle from a self-contained macOS publish, then build a
# .pkg installer and a .dmg. Run on a macOS runner (see desktop-release.yml).
#
#   ./build-pkg.sh <arch: osx-arm64|osx-x64> <version> <publish-dir>
#
# The app is a local web server: the bundle launcher sets BIX_DESKTOP=1 and a
# writable DB_DIR (~/Library/Application Support/Bix), runs Analytika --desktop,
# which starts the server on http://localhost:5097 and opens the browser.
set -euo pipefail

ARCH="${1:?usage: build-pkg.sh <osx-arm64|osx-x64> <version> <publish-dir>}"
VERSION="${2:?missing version}"
PUBLISH_DIR="${3:?missing publish dir}"

APP_NAME="Bix"
STAGE="stage-${ARCH}"
APP="${STAGE}/${APP_NAME}.app"

rm -rf "${STAGE}"
mkdir -p "${APP}/Contents/MacOS" "${APP}/Contents/Resources"

# Self-contained publish output goes inside the bundle
cp -R "${PUBLISH_DIR}/." "${APP}/Contents/MacOS/"

# Launcher (the bundle's CFBundleExecutable): desktop mode + writable data dir
cat > "${APP}/Contents/MacOS/${APP_NAME}" <<'LAUNCH'
#!/bin/bash
DIR="$(cd "$(dirname "$0")" && pwd)"
export BIX_DESKTOP=1
export DB_DIR="$HOME/Library/Application Support/Bix"
mkdir -p "$DB_DIR"
exec "$DIR/Analytika" --desktop
LAUNCH
chmod +x "${APP}/Contents/MacOS/${APP_NAME}"
chmod +x "${APP}/Contents/MacOS/Analytika"

# Info.plist
cat > "${APP}/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Bix</string>
  <key>CFBundleDisplayName</key><string>Bix</string>
  <key>CFBundleIdentifier</key><string>ae.ghaf.bix</string>
  <key>CFBundleVersion</key><string>${VERSION}</string>
  <key>CFBundleShortVersionString</key><string>${VERSION}</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleExecutable</key><string>${APP_NAME}</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

# .pkg — installs Bix.app into /Applications
pkgbuild --root "${STAGE}" \
         --identifier ae.ghaf.bix \
         --version "${VERSION}" \
         --install-location /Applications \
         "Bix-${VERSION}-${ARCH}.pkg"

# .dmg — drag-to-Applications style disk image
hdiutil create -volname "Bix" -srcfolder "${STAGE}" -ov -format UDZO \
         "Bix-${VERSION}-${ARCH}.dmg"

echo "Built Bix-${VERSION}-${ARCH}.pkg and Bix-${VERSION}-${ARCH}.dmg"
