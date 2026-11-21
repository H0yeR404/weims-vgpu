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
codesign --force --sign "${WEIMS_SIGN_IDENTITY:--}" "$APP"
cp install.sh uninstall.sh "$OUT/"
chmod +x "$OUT/install.sh" "$OUT/uninstall.sh"
echo "Built $APP"
