#!/bin/sh
# SPDX-License-Identifier: GPL-2.0-or-later
# Installs Weims Guest Tools for the current user: the app goes to ~/Applications and a LaunchAgent
# starts it at login. Nothing is installed system-wide and no administrator password is needed.
#   install.sh [path to the app]   default: "Weims Guest Tools.app" next to this script. The app on the
#   install disk is named "Install Weims Guest Tools.app" and passes its own path; it is installed under
#   the usual name either way.
set -eu
HERE="$(cd "$(dirname "$0")" && pwd)"
LABEL=io.weims.guest-tools
APP="$HOME/Applications/Weims Guest Tools.app"
AGENT="$HOME/Library/LaunchAgents/$LABEL.plist"
SOURCE="${1:-$HERE/Weims Guest Tools.app}"
[ -d "$SOURCE/Contents/MacOS" ] || { echo "No app to install at: $SOURCE" >&2; exit 1; }
launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
mkdir -p "$HOME/Applications" "$HOME/Library/LaunchAgents"
rm -rf "$APP"
cp -R "$SOURCE" "$APP"
# KeepAlive restarts the helper when it crashes or is killed, but not after Quit from its menu.
cat > "$AGENT" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>Label</key><string>$LABEL</string>
	<key>ProgramArguments</key><array><string>$APP/Contents/MacOS/WeimsGuestTools</string></array>
	<key>RunAtLoad</key><true/>
	<key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>
	<key>LimitLoadToSessionType</key><string>Aqua</string>
	<key>ProcessType</key><string>Interactive</string>
</dict>
</plist>
EOF
launchctl bootstrap "gui/$(id -u)" "$AGENT"
echo "Weims Guest Tools is installed and running. Pair it from its menu bar icon, or with:"
echo "  \"$APP/Contents/MacOS/WeimsGuestTools\" --pair   (after clicking Pair Guest Tools in Weims Manager)"
