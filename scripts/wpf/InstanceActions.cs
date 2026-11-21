using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace EBuild.Native
{
    public partial class Launcher
    {
        async Task SetInstancePinned(string id, bool pinned) {
            await Call("instances:pin", id, pinned);
            await RefreshInstances();
            RenderInstanceLibrary();
            Say(pinned ? "实例已置顶" : "已取消置顶");
        }

        async Task RenameInstance(object instance) {
            var name = Input(instance.S("name"), 80);
            var dialog = new TaskDialog(this, "重命名实例", Stack(Field("实例名称", name)));
            dialog.Loaded += (_, __) => { name.Focus(); name.SelectAll(); };
            if (await dialog.Result("保存", async () => {
                await Call("instances:rename", instance.S("id"), name.Text);
                return true;
            }) != true) return;
            await RefreshInstances();
            RenderInstanceLibrary();
            UpdateHomeCard();
            Say("实例已重命名");
        }

        void ShowInstanceSettings(object instance) {
            var id = instance.S("id");
            bool fromHome = section == HomeSection;
            var page = ShowPage("instance", fromHome ? HomeSection : InstancesSection, "实例设置", async () => {
                if (!await CanLeavePage()) return;
                if (fromHome) ShowHome(); else ShowInstances();
            });
            var tabs = new TabControl();
            var name = Input(instance.S("name"), 80);
            var origin = instance.S("externalPath");
            var basic = Stack(Field("实例名称", name), Note(InstanceVersion(instance) +
                (instance.S("loaderVersion") == "" ? "" : " · " + instance.S("loaderVersion"))));
            if (origin != "") basic.Children.Add(Note("原游戏目录 · " + origin));
            var tools = Actions(Button("打开实例文件夹", () => Call("instances:folder", id)));
            if (instance.B("externalMissing")) tools.Children.Add(Button("重新定位", () => Relocate(instance)));
            else tools.Children.Add(Button("完整校验", async () => {
                if (await RunJob("instances:verify", id) != null) Say("游戏文件已校验");
            }));
            tools.Children.Add(Button(origin == "" ? "删除游戏" : "移除实例", () => DeleteInstance(instance, false)));
            if (instance.B("canDeleteOriginal")) tools.Children.Add(Button("删除原游戏", () => DeleteInstance(instance, true)));
            basic.Children.Add(tools);
            var mods = Stack(Actions(Button("打开 mods 文件夹", () => Call("instances:mods-folder", id)),
                Button("去下载模组", async () => {
                    if (!await CanLeavePage()) return;
                    await ShowDownloads(1, instance.S("version"), instance.S("loader") == "vanilla" ? "" : instance.S("loader"), "mod");
                }, true)));
            var installed = Stack();
            mods.Children.Add(installed);
            var editor = new SettingsEditor(this, settings, instance.At("overrides") ?? J.Map());
            var java = Stack(editor.GameFields(), editor.AdvancedFields());
            tabs.Items.Add(Tab("基本信息", Card(basic)));
            tabs.Items.Add(Tab("模组", Card(mods)));
            tabs.Items.Add(Tab("Java 与高级设置", Card(java)));
            page.Children.Add(tabs);
            var revision = pageRevision;
            bool loadedMods = false;
            tabs.SelectionChanged += async (_, e) => {
                if (e.Source != tabs || tabs.SelectedIndex != 1 || loadedMods) return;
                loadedMods = true;
                installed.Children.Add(Note("正在读取模组…"));
                try {
                    var info = (await Call("instances:resources", id, "mods")).A().FirstOrDefault();
                    if (pageRevision != revision) return;
                    installed.Children.Clear();
                    installed.Children.Add(FileList(info));
                } catch (Exception error) { loadedMods = false; installed.Children.Clear(); installed.Children.Add(Note(error.Message)); }
            };
            Func<string> snapshot = () => J.Dump(new object[] { name.Text, editor.Read(false) });
            var saved = snapshot();
            leavePage = () => Task.FromResult(saved == snapshot() || Confirm("放弃实例设置？", "实例设置有未保存的修改。是否离开？"));
            var save = Button("保存实例设置", async () => {
                var values = editor.Read(true);
                if (await RunJob("instances:edit", id, J.Map("name", name.Text.Trim(), "overrides", values)) == null) return;
                saved = snapshot();
                await RefreshInstances();
                Say("实例设置已保存");
            }, true);
            tabs.SelectionChanged += (_, e) => {
                if (e.Source == tabs) save.Visibility = tabs.SelectedIndex == 1 ? Visibility.Collapsed : Visibility.Visible;
            };
            page.Children.Add(Actions(save));
        }

        async Task DeleteInstance(object instance, bool original) {
            var id = instance.S("id");
            var name = instance.S("name");
            var external = instance.S("externalPath") != "";
            var remove = external && !original;
            var typed = original ? Input() : null;
            var content = Stack(Heading(name), Note(InstanceVersion(instance)));
            InstanceDuplicateNumbers().TryGetValue(id, out int duplicateNumber);
            string identity = duplicateNumber > 0 ? DuplicateInstanceLabel(instance, duplicateNumber) : "";
            var duplicate = duplicateNumber > 0 ? Note(identity + " · 正在读取模组数量…") : null;
            if (duplicate != null) content.Children.Add(duplicate);
            if (external) content.Children.Add(Note(instance.S("externalPath")));
            var consequence = Note(remove ? "从 EBuild 移除此实例记录，原游戏文件夹会保留。" :
                original ? "将永久删除原游戏文件夹及其中的存档、模组和设置。" :
                "将永久删除此实例的存档、模组和设置。共用库及 Java 会保留。");
            consequence.Margin = new Thickness(0, 8, 0, 6);
            content.Children.Add(consequence);
            if (typed != null) content.Children.Add(Field("输入实例名称以确认", typed));
            var dialog = new TaskDialog(this, remove ? "移除实例" : "永久删除游戏", content);
            if (typed != null) dialog.Add("复制名称", () => Call("resources:copy-name", name));
            if (duplicate != null) dialog.Loaded += async (_, __) => await Guard(async () => {
                var counts = await backend.InstanceModCounts(new[] { instance });
                if (dialog.IsVisible) duplicate.Text = identity +
                    (counts.TryGetValue(id, out int count) ? " · " + count + " 个模组" : " · 模组数量不可用");
            });
            if (await dialog.Result(remove ? "移除实例" : "永久删除", confirmation: typed, exact: name,
                cancelByDefault: typed == null) != true) return;
            if (await RunJob("instances:delete", id, typed?.Text ?? name, remove ? "remove" : original ? "original" : "") == null) return;
            await RefreshInstances();
            if (route == "instance" || route == "instances") ShowInstances();
            else if (route == "home") UpdateHomeCard();
            Say(remove ? "实例已移除" : "游戏已删除");
        }

        async Task Relocate(object instance) {
            if (await RunJob("instances:relocate", instance.S("id")) == null) return;
            await RefreshInstances();
            if (route == "instance") ShowInstanceSettings(FindInstance(instance.S("id")));
            else if (route == "home") UpdateHomeCard();
            else ShowInstances();
            Say("实例目录已更新");
        }

        async Task ShowImport() {
            var menu = new TaskDialog(this, "导入实例", Stack(Note("选择游戏文件夹或 Modrinth 整合包（.mrpack / .zip）。")));
            menu.Add("导入游戏文件夹", async () => {
                string dir = backend.Folder("选择 Minecraft 游戏文件夹", owner: menu);
                if (dir != "") { menu.Accept(); await PreviewImport(dir); }
            });
            menu.Add("导入整合包", async () => {
                var chooser = new OpenFileDialog { Title = "导入 Modrinth 整合包", Filter = "Modrinth 整合包|*.mrpack;*.zip" };
                if (chooser.ShowDialog(menu) == true) { menu.Accept(); await PreviewImport(chooser.FileName); }
            });
            await menu.Open();
        }

        void DragFiles(object sender, DragEventArgs e) {
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            bool accept = !initializing && !leaving && paths != null && paths.Length == 1;
            DropHint.Visibility = accept ? Visibility.Visible : Visibility.Collapsed;
            e.Effects = accept ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        async Task DropFiles(DragEventArgs e) {
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            DropHint.Visibility = Visibility.Collapsed;
            if (!initializing && paths != null && paths.Length == 1 && await CanLeavePage()) await PreviewImport(paths[0]);
        }

        async Task PreviewImport(string path) {
            var preview = await backend.PreviewDroppedImport(path);
            if (preview.S("kind") == "pack") {
                var spec = preview.At("instance");
                if (await new TaskDialog(this, "确认导入整合包", Stack(Heading(spec.S("name")), Note(InstanceVersion(spec)), Note(path))).Result("开始安装") != true) return;
                await TrackInstallation(() => RunJob("resources:import", path));
                return;
            }
            var choices = Picker(preview.A("choices").Select(item => Option(item.S("externalProfile"), InstanceLabel(item))));
            if (await new TaskDialog(this, "确认导入游戏文件夹", Stack(Note(path), Field("选择游戏版本", choices),
                Note("EBuild 会引用此文件夹中的游戏数据。"))).Result("导入") != true) return;
            var result = await RunJob("instances:external", preview.S("source"), Chosen(choices));
            if (result.S("id") != "") { await RefreshInstances(); ShowInstances(); Say("实例已导入"); }
        }

        FrameworkElement FileList(object info) {
            var files = info.A("files");
            var list = Stack(Note(files.Length == 0 ? "此文件夹还没有文件。" : "共 " + files.Length + " 个文件"));
            if (info.S("directory") != "") list.Children.Add(Note(info.S("directory")));
            if (files.Length == 0) return list;
            var rows = Stack();
            foreach (var file in files) {
                var name = file.S();
                var icon = ImageFromData(info.At("icons").S(name));
                rows.Children.Add(Row(icon == null ? AssetIcon("resource-file", 18) : new Image { Source = icon, Width = 21, Height = 21 },
                    name, ""));
            }
            list.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 215, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            return list;
        }
    }
}
