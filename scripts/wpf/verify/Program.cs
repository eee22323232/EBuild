using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

static class Program
{
    [STAThread]
    static int Main(string[] args) {
        if (args.Length != 0 && args[0].StartsWith("--", StringComparison.Ordinal)) return UpdateProbe.Run(args);
        if (args.Length != 1) {
            Console.Error.WriteLine("Pass the path of the built EBuild-WPF.exe.");
            return 2;
        }
        var profile = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "EBuild-WpfVerification-" + Guid.NewGuid().ToString("N")));
        try {
            Verify(Path.GetFullPath(args[0]), profile.FullName);
            return 0;
        } catch (Exception error) {
            Console.Error.WriteLine(error);
            return 1;
        } finally {
            Directory.Delete(profile.FullName, true);
        }
    }

    static void Verify(string executable, string profile) {
        var data = Directory.CreateDirectory(Path.Combine(profile, "游戏数据")).FullName;
        File.WriteAllText(Path.Combine(data, ".ebuild-csharp-data"), "EBuild C#");
        var settings = Path.Combine(profile, "settings.json");
        File.WriteAllText(settings, "{\"checkUpdatesOnStart\":false,\"motion\":\"off\",\"selected\":\"first\"}");
        var first = WriteInstance(data, "first", "26.1.2 Fabric", "26.1.2", "fabric", 2);
        var second = WriteInstance(data, "second", "26.1.2 Fabric", "26.1.2", "fabric", 1);
        WriteInstance(data, "survival", "生存存档", "1.20.1", "forge", 0);

        var assembly = Assembly.LoadFrom(executable);
        var type = assembly.GetType("EBuild.Native.Launcher", true);
        var constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
        var launcher = (Window)constructor.Invoke(new object[] { profile });
        launcher.ShowInTaskbar = false;
        launcher.WindowStartupLocation = WindowStartupLocation.Manual;
        launcher.Left = 66;
        launcher.Top = 55;
        launcher.Width = 750;
        launcher.Height = 460;

        Exception failure = null;
        launcher.Loaded += async (_, __) => {
            try {
                await VerifyHomepage(launcher, settings);
                await VerifyInstances(launcher, first, second);
                await VerifyBuildSource(launcher, assembly);
            } catch (Exception error) { failure = error; }
            finally { Application.Current.Shutdown(); }
        };
        new Application().Run(launcher);
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    static async Task VerifyHomepage(Window launcher, string settings) {
        Button Switcher() => Descendants<Button>(launcher).SingleOrDefault(item =>
            AutomationProperties.GetAutomationId(item) == "HomeInstanceSwitcher");
        await WaitFor(() => Switcher() != null, "Homepage did not finish loading.");
        var anchor = Switcher();
        Click(anchor);
        var popup = ((Grid)anchor.Parent).Children.OfType<Popup>().Single();
        await WaitFor(() => popup.IsOpen && (popup.Child as FrameworkElement)?.ActualHeight > 0, "Homepage picker did not open.");
        var panel = (FrameworkElement)popup.Child;
        Require(panel.PointToScreen(new Point(0, panel.ActualHeight)).Y < anchor.PointToScreen(new Point()).Y,
            "The instance picker should open above its anchor.");
        Descendants<TextBox>(panel).Single().Text = "1.20.1 forge";
        var result = Descendants<Button>(panel).Single(item => item.Visibility == Visibility.Visible);
        Require(AutomationProperties.GetName(result).Contains("生存存档"), "Homepage search returned the wrong instance.");
        Click(result);
        await WaitFor(() => Switcher() is Button updated && AutomationProperties.GetName(updated).Contains("生存存档"),
            "Homepage did not update to the selected instance.");
        Require(!popup.IsOpen && File.ReadAllText(settings).Contains("\"selected\":\"survival\""), "The choice should be saved and the picker closed.");
        Require(!Descendants<Button>(launcher).Any(item => item.IsVisible && Equals(item.Content, "停止")), "Picking an instance started a game.");
        Console.WriteLine("PASS: upward homepage picker, filtering, and persistent selection.");
    }

    static async Task VerifyInstances(Window launcher, string first, string second) {
        Click(Descendants<Button>(launcher).Single(item => Equals(item.Content, "实例")));
        TextBox Search() => Descendants<TextBox>(launcher).SingleOrDefault(item =>
            AutomationProperties.GetAutomationId(item) == "InstanceSearch");
        await WaitFor(() => Search() != null, "Instance page did not open.");
        var search = Search();
        search.Text = "26.1.2 FABRIC";
        Button[] Rows() => Descendants<Button>(launcher).Where(item => item.IsVisible && item.ContextMenu != null &&
            AutomationProperties.GetName(item).Contains("同名")).ToArray();
        await WaitFor(() => Rows().Length == 2, "Instance search should find both duplicates.");
        var row = Rows().Single(item => AutomationProperties.GetName(item).Contains("同名 1"));
        Action requestDelete = () => row.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "删除游戏"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Window Prompt() => Application.Current.Windows.Cast<Window>().SingleOrDefault(item => item.Title == "永久删除游戏");
        requestDelete();
        await WaitFor(() => Prompt() != null && Descendants<TextBlock>(Prompt()).Any(item => item.Text.Contains("2 个模组")),
            "Delete confirmation did not identify the exact duplicate.");
        var cancel = Descendants<Button>(Prompt()).Single(item => Equals(item.Content, "取消"));
        Require(cancel.IsDefault && !Descendants<TextBox>(Prompt()).Any(), "Managed deletion must default to cancel and require no typing.");
        Click(cancel);
        await WaitFor(() => Prompt() == null, "Cancel did not dismiss the delete confirmation.");
        Require(File.Exists(first) && File.Exists(second), "Cancel must preserve both instances.");
        requestDelete();
        await WaitFor(() => Prompt() != null, "Delete confirmation did not reopen.");
        Click(Descendants<Button>(Prompt()).Single(item => Equals(item.Content, "永久删除")));
        await WaitFor(() => !File.Exists(first) && Descendants<TextBlock>(launcher).Any(item => item.Text == "游戏已删除"),
            "Confirmed deletion did not finish.");
        Require(File.Exists(second), "The other identically named instance must be preserved.");
        Console.WriteLine("PASS: instance search, duplicate identity, cancel, and precise deletion.");
    }

    static string WriteInstance(string data, string id, string name, string version, string loader, int mods) {
        var instance = Directory.CreateDirectory(Path.Combine(data, "instances", id)).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(instance, "mods")).FullName;
        for (int i = 0; i < mods; i++) File.WriteAllText(Path.Combine(folder, i + ".jar"), "fixture");
        var manifest = Path.Combine(instance, ".ebuild.json");
        File.WriteAllText(manifest, "{\"name\":\"" + name + "\",\"version\":\"" + version +
            "\",\"loader\":\"" + loader + "\",\"loaderVersion\":\"0.19.1\"}");
        return manifest;
    }

    static async Task VerifyBuildSource(Window launcher, Assembly assembly) {
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;
        int separator = version.IndexOf('+');
        var expected = separator < 0 ? "源码提交未知" : "源码 " + version.Substring(separator + 1, Math.Min(12, version.Length - separator - 1));
        Click(Descendants<Button>(launcher).Single(item => Equals(item.Content, "设置")));
        TabItem Updates() => Descendants<TabItem>(launcher).SingleOrDefault(item => Equals(item.Header, "启动器设置"));
        await WaitFor(() => Updates() != null, "Launcher settings did not open.");
        Updates().IsSelected = true;
        await WaitFor(() => Descendants<TextBlock>(launcher).Any(item => item.IsVisible && item.Text.StartsWith(expected, StringComparison.Ordinal)),
            "Launcher settings did not show the source revision embedded in the executable.");
        Console.WriteLine("PASS: launcher settings show build provenance.");
    }

    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject {
        if (root is T item) yield return item;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var descendant in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return descendant;
    }

    static async Task WaitFor(Func<bool> ready, string message) {
        var timer = Stopwatch.StartNew();
        while (!ready() && timer.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(30);
        Require(ready(), message);
    }

    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    static void Require(bool condition, string message) {
        if (!condition) throw new InvalidOperationException(message);
    }
}
