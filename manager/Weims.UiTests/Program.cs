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
            var qmp=(TextBox)w.FindName("Qmp"); qmp.SetCurrentValue(TextBox.TextProperty,"not-a-port"); qmp.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); Pump();
            Check(Validation.GetHasError(qmp),"invalid port exposes inline WPF validation");
            model.Page=0; Pump(); bool rejected=false; try { model.Selected!.Validate(); }catch(InvalidDataException){rejected=true;}
            Check(rejected,"invalid field on inactive page blocks model validation");
            model.Page=1; Pump(); qmp.SetCurrentValue(TextBox.TextProperty,"4455"); qmp.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            model.Page=2; Pump(); var resolution=(ComboBox)w.FindName("Resolution"); resolution.SetCurrentValue(ComboBox.TextProperty,"2560x1440"); resolution.GetBindingExpression(ComboBox.TextProperty)!.UpdateSource();
            string displayHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture,"reims_vgpu_c.dll"))));
            JsonFiles.Write(Path.Combine(fixture,"weims-display-v1.json"),new { capability="adaptive-display-v1", dll_sha256=displayHash });
            var displayMode=(ComboBox)w.FindName("DisplayMode"); displayMode.SelectedValue="auto"; Pump();
            var save=Descendants(w).OfType<Button>().Single(b=>Equals(b.Content,"保存显示设置")); save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(VmConfig.Load(config.ConfigPath).Resolution=="2560x1440","display save accepts tested 1440p through actual button handler");
            Check(VmConfig.Load(config.ConfigPath).DisplayMode=="auto","display policy saved through actual control");
            Check(!((CheckBox)w.FindName("FreePointer")).IsEnabled,"unsupported free pointer disabled");
            model.CanEdit=false; Pump(); Check(!save.IsEnabled,"running-state configuration disabled"); model.CanEdit=true;
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

            enModel.Selected!.QmpPortInput = "unsaved-invalid-port";
            var language = (ComboBox)english.FindName("LanguagePicker"); language.SelectedIndex = 0; Pump();
            Check(File.ReadAllText(Path.Combine(enModel.DataDirectory,"language.txt")) == "zh-CN", "language choice persisted");
            Check(english.Title != "Weims · Virtual Machine Manager", "language switches current window title immediately");
            Check(enModel.Page == 4 && enModel.Selected!.Resolution == "2560x1440", "language switch preserves current page and VM configuration");

            Shot(english, Path.Combine(output,"zh-runtime-simple.png"));
            Check(enModel.Selected!.QmpPortInput == "unsaved-invalid-port", "language change preserves unsaved invalid field text");
            language.SelectedIndex = 1; Pump();
            Check(english.Title == "Weims · Virtual Machine Manager", "switch back to English without reopening window");
            enModel.Page = 6; Pump();
            Check(Descendants(english).OfType<Button>().Any(b => Equals(b.Content, "Check for updates")), "update page available in English");
            Check(!((Button)english.FindName("InstallUpdateButton")).IsEnabled, "install is disabled without a newer verified release");
            language.SelectedIndex = 0; Pump();
            Check(((TextBlock)english.FindName("UpdateChannel")).Text.Contains("预览"), "dynamic update channel switches language");
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
            File.WriteAllText(Path.Combine(output,"result.txt"),$"{checks} WPF UI contract checks passed. Both languages, six pages, minimum window and recovery wizard rendered.");
            Console.WriteLine($"{checks} WPF UI checks passed."); app.Shutdown(); return 0;
        }
        catch(Exception e) { File.WriteAllText(Path.Combine(output,"error.txt"),e.ToString()); Console.Error.WriteLine(e); app.Shutdown(); return 1; }
    }
    static IEnumerable<DependencyObject> Descendants(DependencyObject root) { for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i); yield return child; foreach(var item in Descendants(child))yield return item;} }
    static void Pump() => Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
    static void Shot(Window w,string file)
    {
        w.UpdateLayout(); var bitmap=new RenderTargetBitmap((int)w.ActualWidth,(int)w.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(w);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream=File.Create(file); encoder.Save(stream);
    }
}
