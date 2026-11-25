#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP_NAME="LedVisual"
DIST_DIR="$ROOT_DIR/dist"
APP_BUNDLE="$DIST_DIR/$APP_NAME.app"
DMG_NAME="LedVisual-macOS-arm64.dmg"
DMG_PATH="$DIST_DIR/$DMG_NAME"

"$ROOT_DIR/scripts/build-app.sh"

if [[ ! -d "$APP_BUNDLE" ]]; then
  echo "Error: app bundle not found at $APP_BUNDLE" >&2
  exit 1
fi

STAGING_DIR="$(mktemp -d "${TMPDIR:-/tmp}/ledvisual-dmg.XXXXXX")"
trap 'rm -rf "$STAGING_DIR"' EXIT

echo "Staging $APP_NAME.app for DMG..."
ditto "$APP_BUNDLE" "$STAGING_DIR/$APP_NAME.app"
ln -s /Applications "$STAGING_DIR/Applications"

rm -f "$DMG_PATH"

echo "Creating compressed DMG at $DMG_PATH..."
hdiutil create \
  -volname "$APP_NAME" \
  -srcfolder "$STAGING_DIR" \
  -ov \
  -format UDZO \
  "$DMG_PATH"

echo "Built DMG: $DMG_PATH"
