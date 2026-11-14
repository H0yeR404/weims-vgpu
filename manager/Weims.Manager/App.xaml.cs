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
    public string Theme { get; private set; } = "light";
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    protected override void OnStartup(StartupEventArgs e)
    {
        if (!CreateStartupWindow) return;
        base.OnStartup(e);
        SetCurrentProcessExplicitAppUserModelID("Weims.Manager");
        DispatcherUnhandledException += (_, args) => { MessageBox.Show(args.Exception.Message, Text.Get("Weims · 操作未完成", "Weims · Operation incomplete"), MessageBoxButton.OK, MessageBoxImage.Error); args.Handled = true; };
        SystemParameters.StaticPropertyChanged += SystemAppearanceChanged;
        string? Arg(string name) { int i = Array.IndexOf(e.Args, name); return i >= 0 && i + 1 < e.Args.Length ? e.Args[i + 1] : null; }
        var repo = Arg("--repo");
        if (repo == null) for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent) if (File.Exists(Path.Combine(dir.FullName, "sources.json"))) { repo = dir.FullName; break; }
        var data = ProfileDirectory.Resolve(Arg("--data-dir"), AppContext.BaseDirectory);
        var languageFile = Path.Combine(data, "language.txt");
        string language = File.Exists(languageFile) ? File.ReadAllText(languageFile).Trim() : "en";
        ApplyLanguage(language);
        var window = new MainWindow(new MainViewModel(data, repo)); MainWindow = window; window.Show();
    }
    public void ApplyLanguage(string language)
    {
        Text.Chinese = language == "zh-CN";
        Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri("pack://application:,,,/Weims.Manager;component/Strings/" + (Text.Chinese ? "zh-CN" : "en") + ".xaml", UriKind.Absolute) };
    }
    public void LoadTheme(string dataDirectory)
    {
        var file = Path.Combine(dataDirectory, "theme.txt");
        string choice = "light";
        try { if (File.Exists(file)) choice = File.ReadAllText(file).Trim(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        ApplyTheme(choice);
    }
    public void ApplyTheme(string choice)
    {
        Theme = choice == "dark" ? "dark" : "light";
        var palette = new ResourceDictionary { Source = new Uri("pack://application:,,,/Weims.Manager;component/Themes/" + (Theme == "dark" ? "Dark" : "Light") + ".xaml", UriKind.Absolute) };
        foreach (var key in palette.Keys) Resources.Remove(key);
        Resources.MergedDictionaries[1] = palette;
        if (SystemParameters.HighContrast)
        {
            Resources["Ink"] = Resources["Muted"] = SystemColors.WindowTextBrush;
            Resources["Canvas"] = Resources["Card"] = SystemColors.WindowBrush;
            Resources["Sidebar"] = SystemColors.ControlBrush;
            Resources["Line"] = Resources["InputBorder"] = SystemColors.WindowTextBrush;
            Resources["Accent"] = Resources["Hover"] = SystemColors.HighlightBrush;
            Resources["OnAccent"] = SystemColors.HighlightTextBrush;
            Resources["SwitchOff"] = SystemColors.GrayTextBrush;
        }
        foreach (Window window in Windows) ApplyTitleBar(window);
    }
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    internal void ApplyTitleBar(Window window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        int dark = Theme == "dark" && !SystemParameters.HighContrast ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }
    void SystemAppearanceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast)) Dispatcher.Invoke(() => ApplyTheme(Theme));
    }
    protected override void OnExit(ExitEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= SystemAppearanceChanged;
        base.OnExit(e);
    }
}
