#!/usr/bin/env bash
set -euo pipefail

RID="${1:-osx-x64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PUBLISH_DIR="$ROOT/artifacts/publish-$RID"
APP_DIR="$ROOT/artifacts/VSMixer-$RID.app"

rm -rf "$PUBLISH_DIR" "$APP_DIR"
dotnet publish "$ROOT/VSMixer.csproj" \
  -p:PublishProfile="$RID" \
  --output "$PUBLISH_DIR"

mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"
cp -R "$PUBLISH_DIR/"* "$APP_DIR/Contents/MacOS/"
cp "$ROOT/Assets/Brand/vsmixer-logo.icns" "$APP_DIR/Contents/Resources/VSMixer.icns"
cp "$ROOT/packaging/macos/Info.plist" "$APP_DIR/Contents/Info.plist"
chmod +x "$APP_DIR/Contents/MacOS/VSMixer"

if [[ -n "${APPLE_SIGN_IDENTITY:-}" ]]; then
  codesign --force --deep --options runtime \
    --sign "$APPLE_SIGN_IDENTITY" \
    "$APP_DIR"
fi

echo "$APP_DIR"
