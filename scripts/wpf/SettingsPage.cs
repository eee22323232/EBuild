using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace EBuild.Native
{
    public partial class Launcher
    {
        object catalog;

        void ShowSettings() {
            var page = ShowPage("settings", 3, "设置");
            var tabs = new TabControl();
            var editor = new SettingsEditor(this, settings);
            var game = Stack(editor.GameFields(), Actions(Button("管理 Java", () => ManageJava(editor))));
            tabs.Items.Add(Tab("全局游戏设置", Card(game)));
            var theme = Picker(new[] { Option("system", "跟随系统"), Option("light", "浅色"), Option("dark", "深色") }, settings.S("theme"));
            var motion = Picker(new[] { Option("system", "跟随系统"), Option("on", "开启"), Option("off", "关闭") }, settings.S("motion"));
            theme.SelectionChanged += (_, __) => ApplyTheme(Chosen(theme));
            tabs.Items.Add(Tab("外观", Card(Stack(Field("主题", theme), Field("动效", motion)))));
            var source = Picker(new[] { Option("bmclapi", "BMCLAPI 国内镜像（默认）"), Option("mojang", "Mojang 官方源") }, settings.S("downloadSource"));
            var concurrency = Input(settings.S("concurrency"), 2);
            var proxyMode = Picker(new[] { Option("system", "系统代理"), Option("direct", "直连"), Option("manual", "手动代理") }, settings.S("proxyMode"));
            var proxy = Input(settings.S("proxy"));
            var manual = Field("HTTP(S) 代理", proxy, "例如 http://127.0.0.1:7890");
            Action showProxy = () => manual.Visibility = Chosen(proxyMode) == "manual" ? Visibility.Visible : Visibility.Collapsed;
            proxyMode.SelectionChanged += (_, __) => showProxy();
            showProxy();
            tabs.Items.Add(Tab("网络", Card(Stack(Field("游戏本体下载渠道", source, "国内镜像不可用时自动尝试 Mojang 官方源。"),
                Field("下载并发数", concurrency, "1–32"), Field("代理模式", proxyMode), manual))));
            tabs.Items.Add(Tab("高级", Card(editor.AdvancedFields())));
            tabs.Items.Add(Tab("启动器设置", Card(Stack(
                Heading("游戏数据目录"), Note(settings.S("currentRoot")),
                Actions(Button("打开", () => Call("data:open")), Button("更改", async () => {
                    string target = (await Call("data:choose")).S();
                    if (target != "") await ChangeData(target);
                }), Button("恢复默认", () => ChangeData(""))),
                Heading("空间管理"), Actions(Button("查看空间", ShowStorage)),
                Heading("旧版实例"), Actions(Button("导入旧实例副本", async () => {
                    if (await RunJob("instances:import") == null) return;
                    await RefreshInstances();
                    ShowHome();
                    Say("实例已导入");
                })),
                UpdateSettings()))));
            page.Children.Add(tabs);
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
            page.Children.Add(Actions(save));
            leavePage = () => {
                ApplyTheme(settings.S("theme"));
                return Task.FromResult(true);
            };
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
            readonly Dictionary<string, Func<bool, object>> fields = new Dictionary<string, Func<bool, object>>();
            readonly Dictionary<string, CheckBox> enables = new Dictionary<string, CheckBox>();
            TextBox javaPath;

            internal SettingsEditor(Launcher host, object settings, object overrides = null) {
                this.host = host;
                this.settings = settings;
                this.overrides = overrides;
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

            FrameworkElement Integer(string key, string name, int min, int max) {
                var input = Input(ReadValue(key).S(), 5);
                fields[key] = validate => validate ? (object)Number(input.Text, min, max, name) : input.Text;
                return Field(name, input);
            }

            FrameworkElement Select(string key, string label, IEnumerable<Choice> options) {
                var picker = Picker(options, ReadValue(key).S());
                fields[key] = _ => Chosen(picker);
                return Group(key, Field(label, picker));
            }

            internal FrameworkElement GameFields() {
                javaPath = Input(ReadValue("java").S(), readOnly: true);
                fields["java"] = _ => javaPath.Text;
                var java = Stack(Field("Java 路径", javaPath, "留空时自动匹配本机 Java。"), Actions(
                    host.Button("选择", async () => {
                        var path = (await host.Call("java:select")).S();
                        if (path != "") javaPath.Text = path;
                    }), host.Button("自动", () => { javaPath.Clear(); return Task.CompletedTask; })));
                return Stack(Group("java", java), Group("memory", Integer("memory", "最大内存（MB）", 1024, 65536)),
                    Group("width", Columns(Integer("width", "游戏窗口宽度", 0, 16384), Integer("height", "游戏窗口高度", 0, 16384)), "height"),
                    Select("afterLaunch", "启动后行为", new[] { Option("keep", "保留启动器"), Option("minimize", "最小化启动器") }));
            }

            internal void SetJava(string value) { javaPath.Text = value; }

            FrameworkElement Lines(string key, string title) {
                var input = Input(String.Join(Environment.NewLine, ReadValue(key).A().Select(item => item.S())));
                input.AcceptsReturn = true;
                input.MinHeight = 70;
                input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                fields[key] = _ => input.Text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);
                return Group(key, Field(title, input, "每行一个参数"));
            }

            internal FrameworkElement AdvancedFields() {
                var logs = Check("保存启动日志", Equals(ReadValue("logs"), true));
                fields["logs"] = _ => logs.IsChecked == true;
                return Stack(Select("gc", "垃圾回收器", new[] { Option("", "Java 默认"), Option("G1", "G1"),
                    Option("Z", "ZGC"), Option("Parallel", "Parallel"), Option("Serial", "Serial") }),
                    Lines("jvmArgs", "JVM 参数"), Lines("gameArgs", "游戏参数"), Group("logs", logs));
            }

            internal object Read(bool validate) => fields.Where(pair => !enables.TryGetValue(pair.Key, out var check) || check.IsChecked == true)
                .ToDictionary(pair => pair.Key, pair => pair.Value(validate));
        }
    }
}
