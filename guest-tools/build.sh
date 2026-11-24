#!/bin/sh
# SPDX-License-Identifier: GPL-2.0-or-later
# Builds Weims Guest Tools.app in the macOS guest. Needs the Xcode command line tools (swiftc, codesign).
#   WEIMS_SIGN_IDENTITY  signing identity; the default "-" is an ad-hoc signature.
set -eu
cd "$(dirname "$0")"
OUT=build
APP="$OUT/Weims Guest Tools.app"
rm -rf "$OUT"
mkdir -p "$APP/Contents/MacOS"
swiftc -O -swift-version 5 -target x86_64-apple-macos13.0 Sources/*.swift -o "$APP/Contents/MacOS/WeimsGuestTools"
cp Info.plist "$APP/Contents/Info.plist"
# The app carries its own installer and uninstaller: started as "Install Weims Guest Tools.app" it runs install.sh on itself.
mkdir -p "$APP/Contents/Resources"
cp install.sh uninstall.sh "$APP/Contents/Resources/"
chmod +x "$APP/Contents/Resources/install.sh" "$APP/Contents/Resources/uninstall.sh"
# The icon Finder shows for the app is drawn by make-icon.swift and packed by iconutil; no picture is kept in the sources.
swiftc -O make-icon.swift -o "$OUT/make-icon"
"$OUT/make-icon" "$OUT/AppIcon.iconset"
iconutil -c icns "$OUT/AppIcon.iconset" -o "$APP/Contents/Resources/AppIcon.icns"
rm -rf "$OUT/make-icon" "$OUT/AppIcon.iconset"
codesign --force --sign "${WEIMS_SIGN_IDENTITY:--}" "$APP"
cp install.sh uninstall.sh "$OUT/"
chmod +x "$OUT/install.sh" "$OUT/uninstall.sh"
echo "Built $APP"
