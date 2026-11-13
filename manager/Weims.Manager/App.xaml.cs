using Weims.Core;
// SPDX-License-Identifier: GPL-2.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Runtime.InteropServices;

namespace Weims.Manager;
public partial class App : Application
{
    public bool CreateStartupWindow { get; set; } = true;
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    protected override void OnStartup(StartupEventArgs e)
    {
        if (!CreateStartupWindow) return;
        base.OnStartup(e);
        SetCurrentProcessExplicitAppUserModelID("Weims.Manager");
        DispatcherUnhandledException += (_, args) => { MessageBox.Show(args.Exception.Message, Text.Get("Weims · 操作未完成", "Weims · Operation incomplete"), MessageBoxButton.OK, MessageBoxImage.Error); args.Handled = true; };
        if (SystemParameters.HighContrast)
        {
            Resources["Ink"] = SystemColors.WindowTextBrush; Resources["Muted"] = SystemColors.WindowTextBrush;
            Resources["Canvas"] = SystemColors.WindowBrush; Resources["Card"] = SystemColors.WindowBrush;
            Resources["Sidebar"] = SystemColors.ControlBrush; Resources["Line"] = SystemColors.WindowTextBrush; Resources["Accent"] = SystemColors.HighlightBrush;
        }
        string? Arg(string name) { int i = Array.IndexOf(e.Args, name); return i >= 0 && i + 1 < e.Args.Length ? e.Args[i + 1] : null; }
        var repo = Arg("--repo");
        if (repo == null) for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent) if (File.Exists(Path.Combine(dir.FullName, "sources.json"))) { repo = dir.FullName; break; }
        var data = Arg("--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Weims", "Manager-Preview");
        var languageFile = Path.Combine(data, "language.txt");
        string language = File.Exists(languageFile) ? File.ReadAllText(languageFile).Trim() : "en";
        Text.Chinese = language == "zh-CN";
        Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri("pack://application:,,,/Weims.Manager;component/Strings/" + (Text.Chinese ? "zh-CN" : "en") + ".xaml", UriKind.Absolute) };
        var window = new MainWindow(new MainViewModel(data, repo)); MainWindow = window; window.Show();
    }
}
