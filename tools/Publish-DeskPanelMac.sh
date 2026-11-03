#!/bin/zsh
# Builds dist/DeskPanel.app (self-contained, Apple Silicon) and signs it ad hoc.
#
#   ./tools/Publish-DeskPanelMac.sh            -> dist/DeskPanel.app
#
# Ad-hoc signing (no Apple developer account) is enough to run it on this Mac. If the app is copied through AirDrop,
# a download or a sync client, macOS marks it as quarantined: right-click › Open the first time, or run
#   xattr -dr com.apple.quarantine dist/DeskPanel.app

set -eu

ROOT="${0:A:h:h}"
RID="osx-arm64"
APP="$ROOT/dist/DeskPanel.app"
STAGE="$ROOT/dist/publish-mac"
ICON="$ROOT/assets/icons/deskpanel.ico"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -1)"
VERSION="${VERSION:-0.1.0}"

rm -rf "$APP" "$STAGE"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

echo "publicando ($RID, autocontenida)…"
dotnet publish "$ROOT/src/DeskPanel.Mac/DeskPanel.Mac.csproj" -c Release -r "$RID" --self-contained \
  -p:DebugType=None -p:DebugSymbols=false -o "$STAGE" --nologo -v q

# Whisper.net ships native libraries for every platform; only the Mac ones are useful here.
if [[ -d "$STAGE/runtimes" ]]; then
  for dir in "$STAGE"/runtimes/*(/N); do
    case "${dir:t}" in
      macos-arm64|osx-arm64|osx) ;;
      *) rm -rf "$dir" ;;
    esac
  done
fi

cp -R "$STAGE"/. "$APP/Contents/MacOS/"
rm -rf "$STAGE"

echo "generando el icono…"
ICONSET="$(mktemp -d)/DeskPanel.iconset"
mkdir -p "$ICONSET"
PNG="$(mktemp -d)/base.png"
sips -s format png "$ICON" --out "$PNG" >/dev/null
for size in 16 32 128 256; do
  sips -z $size $size "$PNG" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
done
# @2x variants reuse the next size up; the source icon is 256 px, so nothing larger exists.
cp "$ICONSET/icon_32x32.png" "$ICONSET/icon_16x16@2x.png"
sips -z 64 64 "$PNG" --out "$ICONSET/icon_32x32@2x.png" >/dev/null
cp "$ICONSET/icon_256x256.png" "$ICONSET/icon_128x128@2x.png"
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/DeskPanel.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>DeskPanel</string>
  <key>CFBundleDisplayName</key><string>DeskPanel</string>
  <key>CFBundleIdentifier</key><string>com.rfog.DeskPanel</string>
  <key>CFBundleExecutable</key><string>DeskPanel</string>
  <key>CFBundleIconFile</key><string>DeskPanel</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSPrincipalClass</key><string>NSApplication</string>
  <!-- The article preview is a WebKit view: without this, images served over plain http would be blocked there. -->
  <key>NSAppTransportSecurity</key><dict><key>NSAllowsArbitraryLoadsInWebContent</key><true/></dict>
  <!-- Moving a processed audio to the Trash goes through Finder (Apple Events). -->
  <key>NSAppleEventsUsageDescription</key><string>DeskPanel envía a la Papelera, a través de Finder, los audios originales que ya ha procesado.</string>
</dict>
</plist>
PLIST

chmod +x "$APP/Contents/MacOS/DeskPanel"
echo "firmando (ad hoc)…"
codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict "$APP" && echo "firma verificada"
echo "Listo: $APP ($(du -sh "$APP" | cut -f1))"
