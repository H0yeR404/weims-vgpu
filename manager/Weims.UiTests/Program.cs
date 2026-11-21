// SPDX-License-Identifier: GPL-2.0-or-later
// Runs the actual WPF views against isolated fixture files, never an existing VM.
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Weims.Core;
using Weims.Manager;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--startup")) return StartupCheck(args);
        Text.Chinese = true;
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "ui-evidence"); Directory.CreateDirectory(output);
        var fixture = Path.Combine(output, "fixture"); Directory.CreateDirectory(fixture);
        var app = new App { CreateStartupWindow = false, ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        try
        {
            string Touch(string name) { var p = Path.Combine(fixture, name); File.WriteAllText(p,"fixture"); return p; }
            var config = new VmConfig(Path.Combine(fixture,"demo","state","config.json"),new JsonObject { ["display_name"]="macOS Tahoe", ["lane"]="reims-graphics-pci" });
            config.Cpus=8; config.MemoryMiB=32768; config.Resolution="1920x1080"; config.Mac="52:54:00:12:34:56";
            config.Disk=Touch("current.qcow2"); config.OpenCore=Touch("OpenCore.img"); config.FirmwareCode=Touch("CODE.fd"); config.FirmwareVars=Touch("VARS.fd"); config.Rom=Touch("Graphics.rom"); config.Save();
            var model = new MainViewModel(Path.Combine(fixture,"preferences"), null);
            model.Engine.Qemu=Touch("qemu-system-x86_64.exe"); Touch("qemu-img.exe"); Touch("reims_vgpu_c.dll"); model.Engine.RuntimeBin=fixture; model.Engine.Bios=fixture;
            model.Add(config.ConfigPath); model.CanEdit=true; model.Status="已停止";
            var w = new MainWindow(model, detectOnLoad: false); w.Show(); Pump();
            int checks=0;
            void Check(bool ok,string name) { if(!ok)throw new Exception(name); checks++; Console.WriteLine("PASS "+name); }
            Check(w.ActualWidth >= 960, "window loaded at supported size");
            for(int i=0;i<7;i++) { model.Page=i; Pump(); Shot(w,Path.Combine(output,$"page-{i}.png")); }
            model.Page=1; Pump();
            Check(w.FindName("Qmp")==null,"QMP port is no longer a user setting");
            var ssh=(TextBox)w.FindName("Ssh"); ssh.SetCurrentValue(TextBox.TextProperty,"not-a-port"); ssh.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); Pump();
            Check(Validation.GetHasError(ssh),"invalid port exposes inline WPF validation");
            model.Page=0; Pump(); bool rejected=false; try { model.Selected!.Validate(); }catch(InvalidDataException){rejected=true;}
            Check(rejected,"invalid field on inactive page blocks model validation");
            model.Page=1; Pump(); ssh.SetCurrentValue(TextBox.TextProperty,"2233"); ssh.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            model.Page=2; Pump(); var resolution=(ComboBox)w.FindName("Resolution"); resolution.SetCurrentValue(ComboBox.TextProperty,"2560x1440"); resolution.GetBindingExpression(ComboBox.TextProperty)!.UpdateSource();
            string displayHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture,"reims_vgpu_c.dll"))));
            JsonFiles.Write(Path.Combine(fixture,"weims-display-v1.json"),new { capability="adaptive-display-v1", dll_sha256=displayHash });
            var displayMode=(ComboBox)w.FindName("DisplayMode"); displayMode.SelectedValue="auto"; Pump();
            var save=Descendants(w).OfType<Button>().Single(b=>Equals(b.Content,"保存显示设置")); save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(VmConfig.Load(config.ConfigPath).Resolution=="2560x1440","display save accepts tested 1440p through actual button handler");
            Check(VmConfig.Load(config.ConfigPath).DisplayMode=="auto","display policy saved through actual control");
            Check(!((CheckBox)w.FindName("FreePointer")).IsEnabled,"unsupported free pointer disabled");
            model.CanEdit=false; Pump(); Check(!save.IsEnabled,"running-state configuration disabled");
            // Guest Tools: its own section on this page, usable while the VM runs, stored without touching other fields.
            bool Until(Func<bool> done) { for(int i=0;i<150&&!done();i++) { Pump(); System.Threading.Thread.Sleep(20); } return done(); }
            var integration=(CheckBox)w.FindName("IntegrationEnabled"); var clipboard=(CheckBox)w.FindName("ClipboardText"); var pair=(Button)w.FindName("PairButton"); var reset=(Button)w.FindName("ResetPairingButton");
            var integrationStatus=(TextBlock)w.FindName("IntegrationStatus"); var pairingPanel=(StackPanel)w.FindName("PairingPanel");
            Check(Until(()=>integrationStatus.Text=="集成：关闭") && integration.IsChecked==false && clipboard.IsChecked==true, "Guest Tools is off by default with clipboard text preselected");
            Check(integration.IsEnabled && !clipboard.IsEnabled, "the Guest Tools switch stays available while the VM configuration is locked; its clipboard option waits for it");
            Check(!pair.IsEnabled && !reset.IsEnabled && pairingPanel.Visibility==Visibility.Collapsed, "pairing needs a running VM with its broker; no code is shown");
            model.CanEdit=true;
            model.Selected!.Cpus=16;
            integration.SetCurrentValue(CheckBox.IsCheckedProperty,true); integration.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Until(()=>integrationStatus.Text=="集成：已开启，随虚拟机启动") && integration.IsChecked==true, "switching Guest Tools on is shown at once");
            Check(VmConfig.Load(config.ConfigPath) is { IntegrationEnabled: true, Cpus: 8, Resolution: "2560x1440" }, "the switch is stored without an unsaved edit of another field");
            model.Selected.Cpus=8;
            clipboard.SetCurrentValue(CheckBox.IsCheckedProperty,false); clipboard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Until(()=>VmConfig.Load(config.ConfigPath).ClipboardMode=="off" && clipboard.IsChecked==false && !model.Busy), "clipboard sharing can be switched off on its own");
            clipboard.SetCurrentValue(CheckBox.IsCheckedProperty,true); clipboard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Until(()=>VmConfig.Load(config.ConfigPath).ClipboardMode=="text" && clipboard.IsChecked==true && !model.Busy), "and on again");
            // A broker with an open pairing window is played by a state file that names this test process, so that it reads as alive.
            void Broker(string? digits, bool open=true) => JsonFiles.Write(Integration.StatePath(config), new JsonObject { ["pid"]=Environment.ProcessId, ["started_utc"]=System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("o"), ["heartbeat_utc"]=DateTime.UtcNow.ToString("o"), ["status"]="running", ["port"]=46412, ["fingerprint"]=new string('a',64), ["paired_users"]=new JsonArray(), ["pairing_until_utc"]=open?DateTime.UtcNow.AddMinutes(10).ToString("o"):null, ["pairing_digits"]=digits, ["pairing_user"]=digits==null?null:"hoyer" });
            var confirm=(Button)w.FindName("ConfirmPairingButton"); var decline=(Button)w.FindName("DeclinePairingButton"); var digitsText=(TextBlock)w.FindName("PairingDigits"); var prompt=(TextBlock)w.FindName("PairingPrompt");
            Broker(null); _ = model.Refresh();
            Check(Until(()=>pairingPanel.Visibility==Visibility.Visible && prompt.Text.Contains("正在等待 macOS")) && digitsText.Text=="" && !confirm.IsEnabled && !decline.IsEnabled && !pair.IsEnabled && reset.IsEnabled, "an open pairing window says what to do in macOS; there is nothing to answer yet");
            Broker("0427"); _ = model.Refresh();
            Check(Until(()=>digitsText.Text=="0427" && prompt.Text.Contains("hoyer")) && confirm.IsEnabled && decline.IsEnabled, "when a helper asks, its four digits and its macOS user are shown with the two answers");
            ((FrameworkElement)pairingPanel.Children[3]).BringIntoView(); Pump(); Shot(w,Path.Combine(output,"guest-tools-zh.png"));
            Broker(null, open:false); _ = model.Refresh();
            Check(Until(()=>pairingPanel.Visibility==Visibility.Collapsed && digitsText.Text=="" && pair.IsEnabled), "when pairing is over the number leaves the screen and pairing can be started again");
            var installButton=(Button)w.FindName("InstallToolsButton"); var installPrompt=(TextBlock)w.FindName("InstallPrompt");
            Check(!installButton.IsEnabled && installPrompt.Visibility==Visibility.Collapsed, "without the install image next to Manager, installing Guest Tools is not offered");
            var withTicket=JsonNode.Parse(File.ReadAllText(Integration.StatePath(config)))!.AsObject(); withTicket["heartbeat_utc"]=DateTime.UtcNow.ToString("o"); withTicket["install_until_utc"]=DateTime.UtcNow.AddMinutes(10).ToString("o");
            JsonFiles.Write(Integration.StatePath(config), withTicket); _ = model.Refresh();
            Check(Until(()=>installPrompt.Visibility==Visibility.Visible && installPrompt.Text.Contains("Install Weims Guest Tools")), "while the install disk is in the VM, Manager says what to do in macOS");
            Broker(null, open:false); _ = model.Refresh();
            Check(Until(()=>installPrompt.Visibility==Visibility.Collapsed), "the hint goes away once the install code is used or has expired");
            File.Delete(Integration.StatePath(config)); _ = model.Refresh();
            Check(Until(()=>!pair.IsEnabled && !reset.IsEnabled), "without a broker there is nothing to pair with");
            integration.SetCurrentValue(CheckBox.IsCheckedProperty,false); integration.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Until(()=>integrationStatus.Text=="集成：关闭" && !VmConfig.Load(config.ConfigPath).IntegrationEnabled && !model.Busy), "switching Guest Tools off is stored and shown");
            w.Width=960; w.Height=660; Pump(); Shot(w,Path.Combine(output,"display-minimum.png"));
            model.Page=5; Pump(); var preview=Descendants(w).OfType<Button>().Single(b=>Equals(b.Content,"预览启动参数")); preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(model.Details.Contains("resolution=2560x1440")&&model.Details.Contains("WEIMS_DISPLAY_MODE=auto")&&model.Details.Contains("<redacted>"),"UI preview uses edited display policy and redacts key");
            var wizard = new RecoveryWindow(model.Engine,null) { Owner=w }; wizard.Show(); Pump(); Shot(wizard,Path.Combine(output,"recovery-wizard.png")); wizard.Close();
            w.Close();
            Text.Chinese = false;
            app.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri("/Weims.Manager;component/Strings/en.xaml", UriKind.Relative) };
            var enModel = new MainViewModel(Path.Combine(fixture,"english-preferences"), null);
            Check(enModel.Page == 4, "first launch opens one-click runtime setup");
            enModel.Add(config.ConfigPath); enModel.CanEdit = true;
            var english = new MainWindow(enModel, detectOnLoad: false); english.Show(); Pump();
            Check(english.Title == "Weims · Virtual Machine Manager", "English window title");
            Check(((ComboBox)english.FindName("LanguagePicker")).SelectedIndex == 1, "English is selected by default");
            for (int i = 0; i < 7; i++) { enModel.Page = i; Pump(); Shot(english, Path.Combine(output, $"en-page-{i}.png")); }
            enModel.Page = 4; english.Width = 960; english.Height = 660; Pump(); Shot(english, Path.Combine(output,"en-runtime-minimum.png"));
            Check(Descendants(english).OfType<Button>().Any(b => Equals(b.Content,"One-click setup")), "English automatic setup action is present");
            Check(!((Button)english.FindName("CancelSetup")).IsEnabled, "cancel setup disabled while idle");
            var advancedRuntime = (Expander)english.FindName("AdvancedRuntime");
            Check(!advancedRuntime.IsExpanded && !((TextBox)english.FindName("QemuPath")).IsVisible, "manual runtime paths hidden by default");
            advancedRuntime.IsExpanded = true; Pump();
            Check(((TextBox)english.FindName("QemuPath")).IsVisible, "custom paths remain available in advanced settings");
            advancedRuntime.IsExpanded = false; Pump();

            enModel.Selected!.SshPortInput = "unsaved-invalid-port";
            var language = (ComboBox)english.FindName("LanguagePicker"); language.SelectedIndex = 0; Pump();
            Check(File.ReadAllText(Path.Combine(enModel.DataDirectory,"language.txt")) == "zh-CN", "language choice persisted");
            Check(english.Title != "Weims · Virtual Machine Manager", "language switches current window title immediately");
            Check(enModel.Page == 4 && enModel.Selected!.Resolution == "2560x1440", "language switch preserves current page and VM configuration");

            Shot(english, Path.Combine(output,"zh-runtime-simple.png"));
            Check(enModel.Selected!.SshPortInput == "unsaved-invalid-port", "language change preserves unsaved invalid field text");
            var theme = (ComboBox)english.FindName("ThemePicker");
            Check(Equals(theme.SelectedValue, "light"), "light appearance is the default");
            theme.SelectedValue = "dark"; Pump();
            Color BrushColor(Brush brush) => ((SolidColorBrush)brush).Color;
            Check(BrushColor(english.Background) == Color.FromRgb(25,27,32), "dark palette applied to the existing window immediately");
            Check(File.ReadAllText(Path.Combine(enModel.DataDirectory, "theme.txt")) == "dark", "appearance selection saved");
            Check(enModel.Page == 4 && enModel.Selected!.SshPortInput == "unsaved-invalid-port", "theme switch preserves current page and unsaved edits");
            Check(ReferenceEquals(english.DataContext, enModel), "theme switch keeps existing view model");
            var themeBottom = theme.TranslatePoint(new Point(0, theme.ActualHeight), english);
            var navigation = (ListBox)english.FindName("Navigation");
            var navigationBottom = navigation.TranslatePoint(new Point(0, navigation.ActualHeight), english);
            var languageTop = language.TranslatePoint(new Point(0, 0), english);
            Check(themeBottom.Y < english.ActualHeight && navigationBottom.Y < languageTop.Y, "appearance and navigation fit minimum window height");
            language.SelectedIndex = 1; Pump();
            Check(app.Theme == "dark" && BrushColor(english.Background) == Color.FromRgb(25,27,32), "language switch does not reset dark appearance");
            for (int i=0;i<7;i++) { enModel.Page=i; Pump(); Shot(english,Path.Combine(output,$"dark-page-{i}.png")); }
            enModel.Page=2; Pump();
            Check(Descendants(english).OfType<TextBlock>().Any(t=>t.Text=="GUEST TOOLS") && Equals(((CheckBox)english.FindName("IntegrationEnabled")).Content,"Enable Guest Tools integration for this VM") && Equals(((Button)english.FindName("PairButton")).Content,"Pair Guest Tools…"), "Guest Tools section is present in English");
            Broker("0427"); _ = enModel.Refresh();
            var darkDigits=(TextBlock)english.FindName("PairingDigits");
            Check(Until(()=>darkDigits.Text=="0427") && Equals(((Button)english.FindName("ConfirmPairingButton")).Content,"They match — pair") && Equals(((Button)english.FindName("DeclinePairingButton")).Content,"They differ") && ((TextBlock)english.FindName("PairingPrompt")).Text.Contains("macOS user hoyer asks to pair"), "the number comparison is presented in English");
            ((FrameworkElement)((StackPanel)english.FindName("PairingPanel")).Children[3]).BringIntoView(); Pump(); Shot(english,Path.Combine(output,"guest-tools-en-dark.png"));
            Check(BrushColor(darkDigits.Foreground)==BrushColor((Brush)app.FindResource("Ink")), "the pairing number is readable in the dark appearance");
            File.Delete(Integration.StatePath(config)); _ = enModel.Refresh(); Until(()=>((StackPanel)english.FindName("PairingPanel")).Visibility==Visibility.Collapsed);
            var darkResolution=(ComboBox)english.FindName("Resolution");
            var editor=(TextBox)darkResolution.Template.FindName("PART_EditableTextBox",darkResolution);
            editor.SetCurrentValue(TextBox.TextProperty,"2560x1600"); Pump();
            Check(enModel.Selected!.Resolution=="2560x1600", "themed editable resolution still updates configuration");
            Check(BrushColor(editor.Foreground)==BrushColor((Brush)app.FindResource("Ink")), "editable combo uses readable dark foreground");
            darkResolution.IsDropDownOpen=true; Pump();
            var popup=(System.Windows.Controls.Primitives.Popup)darkResolution.Template.FindName("PART_Popup",darkResolution);
            Check(BrushColor(((Border)popup.Child).Background)==BrushColor((Brush)app.FindResource("Card")), "dropdown popup uses dark surface");
            var resolutionItem=(ComboBoxItem)darkResolution.ItemContainerGenerator.ContainerFromIndex(0);
            resolutionItem.IsSelected=true; darkResolution.IsDropDownOpen=false; Pump();
            Check(enModel.Selected.Resolution=="1280x800", "themed dropdown selection still updates editable configuration");
            var darkWizard=new RecoveryWindow(enModel.Engine,null) {Owner=english}; darkWizard.Show(); Pump();
            Check(BrushColor(darkWizard.Background)==BrushColor(english.Background), "new recovery window uses selected theme");
            Check(Descendants(darkWizard).OfType<Label>().All(l=>BrushColor(l.Foreground)==BrushColor((Brush)app.FindResource("Ink"))), "wizard labels use dark foreground");
            Check(Descendants(darkWizard).OfType<CheckBox>().All(c=>BrushColor(c.Foreground)==BrushColor((Brush)app.FindResource("Ink"))), "wizard switches use dark foreground");
            Shot(darkWizard,Path.Combine(output,"dark-recovery-wizard.png"));
            theme.SelectedValue="light"; Pump();
            Check(BrushColor(english.Background)==Color.FromRgb(246,247,250) && BrushColor(darkWizard.Background)==BrushColor(english.Background), "switching back updates all open windows");
            darkWizard.Close();
            theme.SelectedValue="dark"; Pump(); app.ApplyTheme("light"); app.LoadTheme(enModel.DataDirectory); Pump();
            Check(app.Theme=="dark", "persisted dark preference reloads");
            theme.SelectedValue="light"; Pump();
            File.WriteAllText(Path.Combine(enModel.DataDirectory,"theme.txt"),"unknown"); app.LoadTheme(enModel.DataDirectory);
            Check(app.Theme=="light", "unknown saved theme falls back to light");
            language.SelectedIndex = 1; Pump();
            Check(english.Title == "Weims · Virtual Machine Manager", "switch back to English without reopening window");
            enModel.Page = 6; Pump();
            Check(Descendants(english).OfType<Button>().Any(b => Equals(b.Content, "Check for updates")), "update page available in English");
            Check(!((Button)english.FindName("InstallUpdateButton")).IsEnabled, "install is disabled without a newer verified release");
            language.SelectedIndex = 0; Pump();
            Check(((TextBlock)english.FindName("UpdateChannel")).Text.Contains("稳定"), "stable update channel switches language");
            Shot(english, Path.Combine(output,"zh-updates.png"));
            language.SelectedIndex = 1; Pump();
            Check(((TextBox)english.FindName("ReleaseNotes")).Text.Contains("\n"), "bundled update notes preserve readable paragraphs");
            Shot(english, Path.Combine(output,"en-updates.png"));
            var englishWizard = new RecoveryWindow(enModel.Engine, null) { Owner = english }; englishWizard.Show(); Pump();
            Check(((CheckBox)englishWizard.FindName("DownloadRecovery")).IsChecked == true, "Apple recovery download enabled by default");
            Check(((CheckBox)englishWizard.FindName("ManualBoot")).IsChecked != true, "manual boot preparation disabled by default");
            Check(Descendants(englishWizard).OfType<Expander>().All(e => !e.IsExpanded), "advanced boot fields collapsed");
            Check(englishWizard.Title == "Weims · Recovery Setup", "English recovery wizard title");
            Shot(englishWizard, Path.Combine(output,"en-recovery-wizard.png")); englishWizard.Close(); english.Close();
            File.WriteAllText(Path.Combine(output,"result.txt"),$"{checks} WPF UI contract checks passed. Both languages, both themes, seven pages, minimum window and recovery wizard rendered.");
            Console.WriteLine($"{checks} WPF UI checks passed."); app.Shutdown(); return 0;
        }
        catch(Exception e) { File.WriteAllText(Path.Combine(output,"error.txt"),e.ToString()); Console.Error.WriteLine(e); app.Shutdown(); return 1; }
    }
    static int StartupCheck(string[] args)
    {
        var app = new App(); app.InitializeComponent();
        int code = 1;
        app.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var window = (MainWindow)app.MainWindow;
                Console.WriteLine($"Startup: title={window.Title}; language={Text.Chinese}; theme={app.Theme}; resource={app.Resources.MergedDictionaries[0].Source}");
                if (window.Title != "Weims · Virtual Machine Manager" || Text.Chinese) throw new Exception("Fresh startup must use English.");
                if (app.Theme != "dark") throw new Exception("Startup must reload saved dark preference.");
                code=0; Console.WriteLine("PASS actual startup uses English and restores saved dark appearance");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { app.Shutdown(code); }
        }, DispatcherPriority.ApplicationIdle);
        app.Run(); return code;
    }
    static IEnumerable<DependencyObject> Descendants(DependencyObject root) { for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i); yield return child; foreach(var item in Descendants(child))yield return item;} }
    static void Pump() => Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
    static void Shot(Window w,string file)
    {
        w.UpdateLayout(); var bitmap=new RenderTargetBitmap((int)w.ActualWidth,(int)w.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(w);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream=File.Create(file); encoder.Save(stream);
    }
}
