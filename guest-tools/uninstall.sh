#!/bin/sh
# SPDX-License-Identifier: GPL-2.0-or-later
# Removes Weims Guest Tools for the current user: the helper, its LaunchAgent, its pairing and its log.
set -eu
LABEL=io.weims.guest-tools
launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
rm -f "$HOME/Library/LaunchAgents/$LABEL.plist"
rm -rf "$HOME/Applications/Weims Guest Tools.app" "$HOME/Library/Application Support/Weims Guest Tools"
rm -f "$HOME/Library/Logs/Weims Guest Tools.log"
echo "Weims Guest Tools is removed."
