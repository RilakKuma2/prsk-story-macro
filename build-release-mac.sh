#!/bin/bash
# Original .app/WKWebView + Vision OCR packaging, for this independent story project.
set -euo pipefail
cd "$(dirname "$0")"
dotnet_bin=$(command -v dotnet || echo "$HOME/.dotnet/dotnet")
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' SekaiStorySkip.csproj)
target=arm64-apple-macos13.0
app=dist/mac/SekaiStorySkip.app
data=$app/Contents/Resources/app
mkdir -p "$data" "$app/Contents/MacOS"
"$dotnet_bin" publish SekaiStorySkip.csproj -p:SekaiPlatform=mac -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "$data"
cp README.md "$data/"
xcrun swiftc -O -target "$target" -o "$data/sekai-ocr" mac/ocr.swift
xcrun swiftc -O -target "$target" -o "$app/Contents/MacOS/SekaiStorySkip" mac/App.swift
cp mac/AppIcon.icns "$app/Contents/Resources/"
sed "s/@VERSION@/$version/g" mac/Info.plist > "$app/Contents/Info.plist"
codesign --force --deep --sign - "$app"
echo "$app"
