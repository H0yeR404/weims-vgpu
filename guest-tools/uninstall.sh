#!/bin/sh
# SPDX-License-Identifier: GPL-2.0-or-later
# Removes Weims Guest Tools for the current user: the helper, its LaunchAgent, its pairing, its log and its links to shared folders.
set -eu
LABEL=io.weims.guest-tools
launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
# The shared folders. The helper lets go of them when it is ended, but not this time: macOS refuses its unmount
# while launchd takes it out of the session ("Operation not permitted"). Left mounted, they would have no server.
mount | sed -n 's|^//weims-[^ ]* on \(/.*\) (smbfs, .*|\1|p' | while IFS= read -r volume; do
    umount -f "$volume" 2>/dev/null || true
done
rm -f "$HOME/Library/LaunchAgents/$LABEL.plist"
rm -rf "$HOME/Applications/Weims Guest Tools.app" "$HOME/Library/Application Support/Weims Guest Tools"
rm -f "$HOME/Library/Logs/Weims Guest Tools.log"
# The links to shared folders, and their folder unless the user put something else into it.
LINKS="$HOME/Weims Shared Folders"
if [ -d "$LINKS" ]; then
    find "$LINKS" -maxdepth 1 -type l -lname '/Volumes/*' -delete
    rm -f "$LINKS/.DS_Store"
    rmdir "$LINKS" 2>/dev/null || true
fi
echo "Weims Guest Tools is removed."
