// SPDX-License-Identifier: GPL-2.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Weims.Core;

namespace Weims.Manager;

public partial class MainWindow
{
    bool changingTheme;
    void ChangeTheme(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || changingTheme || ThemePicker.SelectedValue is not string choice) return;
        var app = (App)Application.Current;
        changingTheme = true;
        try
        {
            File.WriteAllText(Path.Combine(vm.DataDirectory, "theme.txt"), choice);
            app.ApplyTheme(choice);
            vm.Message = Text.Get("外观已切换，无需重启。", "Appearance updated. No restart needed.");
        }
        catch (Exception ex) { ThemePicker.SelectedValue = app.Theme; vm.Message = ex.Message; }
        finally { changingTheme = false; }
    }
}
