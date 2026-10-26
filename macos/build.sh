#!/bin/bash
# Сборка Orbit для macOS: универсальный бинарник (Apple Silicon + Intel), Orbit.app и образ .dmg.
# Нужны Xcode или Command Line Tools. Результат — macos/build/Orbit-<версия>-macOS.dmg.
set -euo pipefail
cd "$(dirname "$0")"

VERSION=$(/usr/libexec/PlistBuddy -c "Print CFBundleShortVersionString" Resources/Info.plist)
BUILD="build"
APP="$BUILD/Orbit.app"
ARCHS=(--arch arm64 --arch x86_64)

swift build -c release "${ARCHS[@]}"
BIN="$(swift build -c release "${ARCHS[@]}" --show-bin-path)/Orbit"

rm -rf "$BUILD"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/Orbit"
cp Resources/Info.plist "$APP/Contents/Info.plist"

# Значок из того же PNG, что и у версии для Windows.
ICONSET="$BUILD/Orbit.iconset"
mkdir -p "$ICONSET"
SRC="../src/Orbit/Assets/Orbit.png"
for size in 16 32 128 256; do
    sips -z $size $size "$SRC" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    if [ $double -le 256 ]; then
        sips -z $double $double "$SRC" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
    fi
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/Orbit.icns"
rm -rf "$ICONSET"

# Подпись без сертификата разработчика (ad-hoc): её достаточно, чтобы приложение запускалось на Apple Silicon.
codesign --force --deep --sign - "$APP"
codesign --verify --verbose "$APP"

DMG_DIR="$BUILD/dmg"
mkdir -p "$DMG_DIR"
cp -R "$APP" "$DMG_DIR/"
ln -s /Applications "$DMG_DIR/Applications"
hdiutil create -volname "Orbit $VERSION" -srcfolder "$DMG_DIR" -ov -format UDZO "$BUILD/Orbit-$VERSION-macOS.dmg"
rm -rf "$DMG_DIR"

echo "Готово: macos/$BUILD/Orbit-$VERSION-macOS.dmg"
