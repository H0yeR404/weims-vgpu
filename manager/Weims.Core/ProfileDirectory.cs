// SPDX-License-Identifier: GPL-2.0-or-later
namespace Weims.Core;

public static class ProfileDirectory
{
    // Both GUI and CLI honor the profile retained by the updater. Existing preview users
    // keep their library; fresh stable installs use Manager, as stable 1.0.1 did.
    public static string Resolve(string? requested, string applicationDirectory, string? localApplicationData = null)
    {
        if (requested != null) return Path.GetFullPath(requested);
        var marker = Path.Combine(applicationDirectory, "profile-directory.txt");
        if (File.Exists(marker))
        {
            var saved = File.ReadAllText(marker).Trim();
            if (Path.IsPathFullyQualified(saved) && Directory.Exists(saved)) return Path.GetFullPath(saved);
        }
        var root = Path.Combine(localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Weims");
        var preview = Path.Combine(root, "Manager-Preview");
        return Directory.Exists(preview) ? preview : Path.Combine(root, "Manager");
    }
}
