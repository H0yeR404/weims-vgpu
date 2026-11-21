#!/bin/sh
# SPDX-License-Identifier: GPL-2.0-or-later
# Builds the two things Weims Guest Tools are delivered as, from one set of contents:
#   build/weims-guest-tools.img       the install disk that Weims Manager plugs into a VM (raw image, HFS+).
#                                     It is the same for every VM: a VM's pairing ticket travels as the
#                                     disk's USB serial number, not in a file.
#   build/Weims-Guest-Tools-<v>.dmg   the same for download
# Run in the macOS guest after make-identity.sh. Signs with "Weims Guest Tools Signing" unless
# WEIMS_SIGN_IDENTITY says otherwise ("-" for an ad-hoc signature, for testing only).
set -eu
cd "$(dirname "$0")"
VOLUME="Weims Guest Tools"
export WEIMS_SIGN_IDENTITY="${WEIMS_SIGN_IDENTITY:-Weims Guest Tools Signing}"

KEYCHAIN="$HOME/Library/Keychains/weims-guest-tools-signing.keychain-db"
SECRET="$HOME/Library/Application Support/Weims Guest Tools Build/keychain-password"
if [ "$WEIMS_SIGN_IDENTITY" != "-" ] && [ -f "$SECRET" ]; then security unlock-keychain -p "$(cat "$SECRET")" "$KEYCHAIN"; fi
./build.sh
VERSION="$(sed -n 's/^let helperVersion = "\(.*\)"$/\1/p' Sources/main.swift)"
[ -n "$VERSION" ] || { echo "helperVersion not found in Sources/main.swift" >&2; exit 1; }

ROOT=build/image-root
rm -rf "$ROOT" build/image-mount build/image.dmg build/weims-guest-tools.cdr build/weims-guest-tools.img
mkdir -p "$ROOT"
# The app is its own installer under this name; see Installer in Sources/main.swift.
cp -R "build/Weims Guest Tools.app" "$ROOT/Install Weims Guest Tools.app"
cp uninstall.sh "$ROOT/Uninstall Weims Guest Tools.command"
chmod 755 "$ROOT/Uninstall Weims Guest Tools.command"
codesign --verify --strict "$ROOT/Install Weims Guest Tools.app"

hdiutil create -size 8m -fs HFS+ -volname "$VOLUME" -layout GPTSPUD -ov build/image.dmg > /dev/null
hdiutil attach build/image.dmg -nobrowse -mountpoint "$PWD/build/image-mount" > /dev/null
cp -R "$ROOT/." build/image-mount/
rm -rf build/image-mount/.fseventsd build/image-mount/.Trashes
sync
hdiutil detach "$PWD/build/image-mount" > /dev/null
hdiutil convert build/image.dmg -format UDTO -o build/weims-guest-tools > /dev/null
mv build/weims-guest-tools.cdr build/weims-guest-tools.img
rm -f build/image.dmg

rm -f "build/Weims-Guest-Tools-$VERSION.dmg"
hdiutil create -srcfolder "$ROOT" -volname "$VOLUME" -fs HFS+ -format UDZO -ov "build/Weims-Guest-Tools-$VERSION.dmg" > /dev/null
( cd build && shasum -a 256 weims-guest-tools.img "Weims-Guest-Tools-$VERSION.dmg" > SHA256SUMS && cat SHA256SUMS )
echo "Signed as: $WEIMS_SIGN_IDENTITY"
