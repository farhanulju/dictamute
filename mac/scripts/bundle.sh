#!/bin/bash
# Builds a universal (Apple Silicon + Intel) DictaMute.app and zips it into mac/dist/.
# Runs on macOS (GitHub Actions or a real Mac).
set -euo pipefail
cd "$(dirname "$0")/.."

swift build -c release --arch arm64 --arch x86_64

APP=dist/DictaMute.app
rm -rf dist
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp .build/apple/Products/Release/DictaMute "$APP/Contents/MacOS/DictaMute"
cp Info.plist "$APP/Contents/Info.plist"
iconutil -c icns Resources/AppIcon.iconset -o "$APP/Contents/Resources/AppIcon.icns"

# Ad-hoc signature: required for Apple Silicon to run it at all. Not notarized.
codesign --force --deep --sign - "$APP"

lipo -info "$APP/Contents/MacOS/DictaMute"
codesign --verify --verbose "$APP"
(cd dist && ditto -c -k --keepParent DictaMute.app DictaMute-mac.zip)
ls -l dist
