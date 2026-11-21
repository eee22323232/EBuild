using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace EBuild.Native
{
    public partial class Launcher
    {
        object catalog;

        void ShowSettings() {
            var tabs = new TabControl { Style = (Style)FindResource("SettingsTabs") };
            var tabStyle = (Style)FindResource("SettingsTab");
            void AddTab(string title, UIElement content) {
                tabs.Items.Add(new TabItem { Header = title, Content = content, Style = tabStyle });
            }
            var editor = new SettingsEditor(this, settings, arranged: true);
            var game = editor.GameFields();
            var manageJava = Button("管理 Java", () => ManageJava(editor));
            game.Children.Add(SettingsRow("Java 管理", manageJava, "查看或下载 Java。"));
            AddTab("全局游戏设置", Card(game));
            var theme = Picker(new[] { Option("system", "跟随系统"), Option("light", "浅色"), Option("dark", "深色") }, settings.S("theme"));
            var motion = Picker(new[] { Option("system", "跟随系统"), Option("on", "开启"), Option("off", "关闭") }, settings.S("motion"));
            theme.SelectionChanged += (_, __) => ApplyTheme(Chosen(theme));
            AddTab("外观", Card(Stack(SettingsRow("主题", theme), SettingsRow("动效", motion))));
            var source = Picker(new[] { Option("bmclapi", "BMCLAPI 国内镜像（默认）"), Option("mojang", "官方源") }, settings.S("downloadSource"));
            var concurrency = Input(settings.S("concurrency"), 2);
            concurrency.Width = 106;
            concurrency.HorizontalAlignment = HorizontalAlignment.Left;
            var proxyMode = Picker(new[] { Option("system", "系统代理"), Option("direct", "直连"), Option("manual", "手动代理") }, settings.S("proxyMode"));
            var proxy = Input(settings.S("proxy"));
            var manual = SettingsRow("HTTP(S) 代理", proxy, "例如 http://127.0.0.1:7890");
            Action showProxy = () => manual.Visibility = Chosen(proxyMode) == "manual" ? Visibility.Visible : Visibility.Collapsed;
            proxyMode.SelectionChanged += (_, __) => showProxy();
            showProxy();
            AddTab("网络", Stack(Card(Stack(Heading("下载", 15),
                SettingsRow("下载渠道", source, "适用于 Minecraft 本体和支持镜像的加载器。镜像不可用时尝试官方源。"),
                SettingsRow("下载并发上限", concurrency, "1–32 · 按下载源动态调整"))),
                Card(Stack(Heading("代理", 15), SettingsRow("代理模式", proxyMode), manual))));
            AddTab("高级", Card(editor.AdvancedFields()));
            AddTab("启动器设置", Stack(Card(UpdateSettings()), Card(Stack(
                Heading("游戏数据目录"), Note(settings.S("currentRoot")),
                Actions(Button("打开", () => Call("data:open")), Button("更改", async () => {
                    string target = (await Call("data:choose")).S();
                    if (target != "") await ChangeData(target);
                }), Button("恢复默认", () => ChangeData(""))),
                SettingsRow("空间管理", Button("查看空间", ShowStorage)),
                SettingsRow("旧版实例", Button("导入旧实例副本", async () => {
                    if (await RunJob("instances:import") == null) return;
                    await RefreshInstances();
                    ShowInstances();
                    Say("实例已导入");
                }))))));
            var save = Button("保存设置", async () => {
                var values = J.Merge(editor.Read(true), J.Map("theme", Chosen(theme), "motion", Chosen(motion),
                    "downloadSource", Chosen(source), "concurrency", Number(concurrency.Text, 1, 32, "下载并发数"),
                    "proxyMode", Chosen(proxyMode), "proxy", proxy.Text.Trim()));
                var old = settings.S("downloadSource");
                settings = J.Merge(settings, await RunJob("settings:save", values));
                if (old != settings.S("downloadSource")) catalog = null;
                ApplyTheme(settings.S("theme"));
                Say("全局设置已保存");
            }, true);
            tabs.SelectionChanged += (_, e) => {
                if (e.Source == tabs) save.Visibility = tabs.SelectedIndex == 4 ? Visibility.Collapsed : Visibility.Visible;
            };
            save.Margin = new Thickness(0, 0, 0, 10);
            tabs.SelectedIndex = 0;
            var page = ShowPage("settings", SettingsSection, "设置", headerActions: save);
            page.Children.Add(tabs);
            leavePage = () => {
                ApplyTheme(settings.S("theme"));
                return Task.FromResult(true);
            };
        }

        static FrameworkElement SettingsRow(string name, Control control, string hint = "", FrameworkElement content = null) {
            if (control != null && !(control is Button) && !(control is CheckBox)) AutomationProperties.SetName(control, name);
            var label = new Label { Content = name, Target = control, FontWeight = FontWeights.SemiBold, Padding = new Thickness(0) };
            UseResource(label, Control.ForegroundProperty, "Ink");
            var caption = Stack(label);
            caption.VerticalAlignment = VerticalAlignment.Center;
            if (hint != "") {
                var detail = Note(hint);
                detail.Margin = new Thickness(0, 3, 0, 0);
                caption.Children.Add(detail);
            }
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var body = content ?? control;
            body.VerticalAlignment = VerticalAlignment.Center;
            if (body is Button) {
                body.HorizontalAlignment = HorizontalAlignment.Right;
                body.Margin = new Thickness(0);
            }
            Grid.SetColumn(body, 1);
            row.Children.Add(caption);
            row.Children.Add(body);
            return row;
        }

        async Task ChangeData(string target) {
            var copy = Check("复制并校验现有数据，保留旧目录", true);
            var display = target == "" ? settings.S("defaultRoot") : target;
            var dialog = new TaskDialog(this, "切换游戏数据目录", Stack(Note(display), copy));
            if (await dialog.Result("切换目录") != true ||
                (await RunJob("data:change", target, copy.IsChecked == true)).S() == "") return;
            catalog = null;
            await RefreshInstances();
            await RefreshDownloads();
            ShowSettings();
            Say("游戏数据目录已切换");
        }

        async Task ManageJava(SettingsEditor editor) {
            var content = Stack(Note("正在检查已安装的 Java…"));
            var major = Picker(new[] { 8, 16, 17, 21, 25, 26 }.Select(value => Option(value.ToString(), "Java " + value)), "21");
            var dialog = new TaskDialog(this, "Java 管理", Stack(content, Field("要下载或检查的版本", major)));
            dialog.Add("关闭", () => { dialog.Close(); return Task.CompletedTask; });
            dialog.Add("下载或检查", async () => {
                dialog.Accept();
                if (await RunJob("java:install", Chosen(major)) != null && route == "settings") await ManageJava(editor);
            }, true);
            var end = dialog.Open();
            try {
                var found = (await Call("java:list")).A();
                if (!dialog.IsVisible) return;
                content.Children.Clear();
                if (found.Length == 0) content.Children.Add(Note("未发现可用的本机 Java。"));
                foreach (var item in found) {
                    string file = item.S("executable");
                    var row = Stack(Heading("Java " + item.S("major")), Note(file));
                    if (Path.IsPathRooted(file))
                        row.Children.Add(Actions(Button("选用", () => { editor.SetJava(file); dialog.Accept(); return Task.CompletedTask; })));
                    content.Children.Add(Card(row));
                }
            } catch (Exception failure) { content.Children.Clear(); content.Children.Add(Note(failure.Message)); }
            await end;
        }

        static readonly Dictionary<string, string> StorageNames = new Dictionary<string, string> {
            { "game", "游戏本体与公共库" }, { "java", "Java" }, { "instances", "实例文件" },
            { "archives", "安装包缓存" }, { "transfers", "未完成的下载" }, { "backups", "模组旧版备份" },
            { "installations", "安装任务" }, { "metadata", "索引与其他文件" }
        };

        static string FormatSize(object bytes) {
            if (!double.TryParse(bytes.S(), out double size)) size = 0;
            if (size >= 1073741824) return (size / 1073741824).ToString("0.##") + " GB";
            if (size >= 1048576) return (size / 1048576).ToString("0.##") + " MB";
            if (size >= 1024) return (size / 1024).ToString("0.##") + " KB";
            return size.ToString("0") + " B";
        }

        async Task ShowStorage() {
            Say("正在统计磁盘空间…");
            var snapshot = await Call("data:storage");
            var content = Stack(Note(snapshot.S("root")),
                Heading("游戏数据 " + FormatSize(snapshot.At("total"))),
                Note("WPF 启动器程序 " + FormatSize(snapshot.At("programBytes"))));
            if (snapshot.I("skipped") != 0) content.Children.Add(Note("已跳过 " + snapshot.I("skipped") + " 个无法读取的项目。"));
            var choices = new Dictionary<string, CheckBox>();
            foreach (var item in snapshot.A("categories")) {
                var id = item.S("id");
                string name = StorageNames.TryGetValue(id, out string title) ? title : id;
                string label = name + " · " + FormatSize(item.At("bytes"));
                if (item.B("cleanable")) {
                    var check = Check(label);
                    choices.Add(id, check);
                    content.Children.Add(check);
                } else content.Children.Add(Note(label));
            }
            var dialog = new TaskDialog(this, "空间管理", content);
            dialog.Add("关闭", () => { dialog.Close(); return Task.CompletedTask; });
            if (choices.Count != 0) dialog.Add("清理所选", async () => {
                var ids = choices.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToArray();
                if (ids.Length == 0) throw new IOException("请先勾选需要清理的项目。");
                var list = String.Join("、", ids.Select(id => StorageNames[id]));
                if (!Confirm("确认清理", "将清理 " + list + "。未完成的下载可能需要重新下载。是否继续？")) return;
                dialog.Accept();
                await Guard(async () => {
                    var result = await RunJob("data:clean", snapshot.S("token"), ids);
                    if (result == null) return;
                    Say("已清理 " + FormatSize(result.At("freed")) + "，跳过 " + result.I("skipped") + " 项");
                    await ShowStorage();
                });
            }, true);
            Say("空间统计完成");
            await dialog.Open();
        }

        sealed class SettingsEditor
        {
            readonly Launcher host;
            readonly object settings;
            readonly object overrides;
            readonly bool arranged;
            readonly Dictionary<string, Func<bool, object>> fields = new Dictionary<string, Func<bool, object>>();
            readonly Dictionary<string, CheckBox> enables = new Dictionary<string, CheckBox>();
            TextBox javaPath;

            internal SettingsEditor(Launcher host, object settings, object overrides = null, bool arranged = false) {
                this.host = host;
                this.settings = settings;
                this.overrides = overrides;
                this.arranged = arranged;
            }

            object ReadValue(string key) => overrides.At(key) ?? settings.At(key);

            FrameworkElement Group(string key, FrameworkElement control, string linked = null) {
                if (overrides == null) return control;
                var enable = Check("覆盖全局", overrides.Obj().ContainsKey(key) || linked != null && overrides.Obj().ContainsKey(linked));
                enables[key] = enable;
                if (linked != null) enables[linked] = enable;
                control.IsEnabled = enable.IsChecked == true;
                enable.Checked += (_, __) => control.IsEnabled = true;
                enable.Unchecked += (_, __) => control.IsEnabled = false;
                return Stack(enable, control);
            }

            TextBox IntegerInput(string key, string name, int min, int max) {
                var input = Input(ReadValue(key).S(), 5);
                fields[key] = validate => validate ? (object)Number(input.Text, min, max, name) : input.Text;
                return input;
            }

            FrameworkElement EditorField(string label, Control control, string hint = "") {
                return arranged ? SettingsRow(label, control, hint) : Field(label, control, hint);
            }

            FrameworkElement Select(string key, string label, IEnumerable<Choice> options) {
                var picker = Picker(options, ReadValue(key).S());
                fields[key] = _ => Chosen(picker);
                return Group(key, EditorField(label, picker));
            }

            internal StackPanel GameFields() {
                javaPath = Input(ReadValue("java").S(), readOnly: true);
                javaPath.ToolTip = "留空时自动匹配本机 Java。";
                fields["java"] = _ => javaPath.Text;
                var pickJava = host.Button("选择", async () => {
                    var path = (await host.Call("java:select")).S();
                    if (path != "") javaPath.Text = path;
                });
                var autoJava = host.Button("自动", () => { javaPath.Clear(); return Task.CompletedTask; });
                var java = arranged ? SettingsRow("Java 路径", javaPath, "留空时自动匹配。", WithActions(javaPath, pickJava, autoJava)) :
                    Stack(Field("Java 路径", javaPath, "留空时自动匹配本机 Java。"), Actions(pickJava, autoJava));
                const string memory = "最大内存（MB）", width = "游戏窗口宽度", height = "游戏窗口高度";
                var maxMemory = IntegerInput("memory", memory, 1024, 65536);
                if (arranged) {
                    maxMemory.Width = 106;
                    maxMemory.HorizontalAlignment = HorizontalAlignment.Left;
                }
                var widthInput = IntegerInput("width", width, 0, 16384);
                var heightInput = IntegerInput("height", height, 0, 16384);
                var window = arranged ? SettingsRow("游戏窗口尺寸", null, "填 0 由游戏决定。", Columns(Field("宽度", widthInput), Field("高度", heightInput))) :
                    Columns(Field(width, widthInput), Field(height, heightInput));
                return Stack(Group("java", java), Group("memory", EditorField(memory, maxMemory)),
                    Group("width", window, "height"),
                    Select("afterLaunch", "启动后行为", new[] { Option("keep", "保留启动器"), Option("minimize", "最小化启动器") }));
            }

            internal void SetJava(string value) { javaPath.Text = value; }

            FrameworkElement Lines(string key, string title) {
                var input = Input(String.Join(Environment.NewLine, ReadValue(key).A().Select(item => item.S())));
                input.AcceptsReturn = true;
                input.MinHeight = 70;
                input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                fields[key] = _ => input.Text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);
                return Group(key, EditorField(title, input, "每行一个参数"));
            }

            internal FrameworkElement AdvancedFields() {
                var logs = Check("保存启动日志", Equals(ReadValue("logs"), true));
                fields["logs"] = _ => logs.IsChecked == true;
                return Stack(Select("gc", "垃圾回收器", new[] { Option("", "Java 默认"), Option("G1", "G1"),
                    Option("Z", "ZGC"), Option("Parallel", "Parallel"), Option("Serial", "Serial") }),
                    Lines("jvmArgs", "JVM 参数"), Lines("gameArgs", "游戏参数"),
                    Group("logs", arranged ? SettingsRow("日志", logs) : (FrameworkElement)logs));
            }

            internal object Read(bool validate) => fields.Where(pair => !enables.TryGetValue(pair.Key, out var check) || check.IsChecked == true)
                .ToDictionary(pair => pair.Key, pair => pair.Value(validate));
        }
    }
}
