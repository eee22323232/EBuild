using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace EBuild.Native
{
    public partial class Launcher
    {
        object lanState;
        ComboBox lanHosts;
        TextBox lanPort;
        TextBox lanInvite;
        TextBlock lanNotice;
        StackPanel lanLobby;
        StackPanel lanRoom;
        Button lanOptions;
        Button lanCreate;
        Button lanJoin;
        Button lanDetect;
        Button lanCancel;
        Button lanShareOptions;
        CheckBox lanShare;
        object lanShareFiles;
        string lanHostKey = "";
        bool fillingHosts;
        string roomViewKey = "";
        int suggestedPort;

        void ShowLan() {
            var page = ShowPage("lan", 2, "好友联机");
            lanOptions = Button("联机设置", EditNode);
            var tools = Actions(lanOptions);
            tools.HorizontalAlignment = HorizontalAlignment.Right;
            page.Children.Add(tools);
            lanHosts = Picker(Array.Empty<Choice>());
            lanHosts.SelectionChanged += async (_, __) => {
                if (!fillingHosts) {
                    ResetLanShareFiles();
                    suggestedPort = 0;
                    lanPort.Clear();
                    await Guard(() => DetectPort(false));
                }
            };
            lanPort = Input("", 5);
            lanInvite = Input("", 1200);
            lanShare = Check("允许好友下载实例文件", true);
            lanShareOptions = Button("共享内容…", EditLanShareFiles);
            ResetLanShareFiles();
            lanShare.Checked += (_, __) => UpdateLan();
            lanShare.Unchecked += (_, __) => UpdateLan();
            lanDetect = Button("识别端口", () => DetectPort(true));
            lanCreate = Button("创建房间", async () => {
                if (lanPort.Text.Trim() == "" && suggestedPort == 0) await DetectPort(true);
                int port = lanPort.Text.Trim() == "" && suggestedPort != 0 ? suggestedPort : Number(lanPort.Text, 1, 65535, "局域网端口");
                await Call("lan:create", Chosen(lanHosts), port, lanShare.IsChecked == true, lanShareFiles);
            }, true);
            lanJoin = Button("加入房间", async () => {
                if (lanInvite.Text.Trim() == "") throw new IOException("请输入或粘贴房主的邀请码。");
                await Call("lan:join", lanInvite.Text.Trim());
            }, true);
            lanLobby = Stack(Columns(Card(Stack(Heading("创建房间"), Note("在单人世界中按 Esc，选择「对局域网开放」。"),
                Field("房主实例", lanHosts), Field("局域网端口", lanPort), Actions(lanShare, lanShareOptions), Actions(lanDetect, lanCreate))),
                Card(Stack(Heading("加入房间"), Field("邀请码", lanInvite, "短码可直接输入，字母大小写和连字符均可忽略。"), Actions(lanJoin)))));
            lanRoom = Stack();
            lanNotice = Note("");
            lanCancel = Button("取消联机操作", () => Call("lan:leave"));
            page.Children.Add(lanLobby);
            page.Children.Add(lanRoom);
            page.Children.Add(lanNotice);
            page.Children.Add(Actions(lanCancel));
            lanHostKey = "";
            roomViewKey = "";
            suggestedPort = 0;
            UpdateLan();
            if (Chosen(lanHosts) != "" && lanState.At("room") == null)
                Dispatcher.BeginInvoke(new Action(async () => await Guard(() => DetectPort(false))));
        }

        void UpdateLan() {
            if (route != "lan" || lanLobby == null) return;
            var room = lanState.At("room");
            bool working = lanState.B("working");
            var choices = instances.Where(item => Game(item.S("id")).S("state") == "running").Select(InstanceOption).ToArray();
            var key = String.Join("|", choices.Select(item => item.Key + "=" + item.Label));
            if (key != lanHostKey) {
                var previous = Chosen(lanHosts);
                fillingHosts = true;
                SetOptions(lanHosts, choices, choices.Any(item => item.Key == previous) ? previous : selected);
                fillingHosts = false;
                if (Chosen(lanHosts) != previous) { ResetLanShareFiles(); suggestedPort = 0; lanPort.Clear(); }
                lanHostKey = key;
            }
            lanOptions.IsEnabled = !working && room == null;
            lanHosts.IsEnabled = lanDetect.IsEnabled = lanPort.IsEnabled = lanCreate.IsEnabled = !working && Chosen(lanHosts) != "";
            lanShare.IsEnabled = lanHosts.IsEnabled;
            lanShareOptions.IsEnabled = lanShare.IsEnabled && lanShare.IsChecked == true;
            lanInvite.IsEnabled = lanJoin.IsEnabled = !working;
            lanLobby.Visibility = room == null ? Visibility.Visible : Visibility.Collapsed;
            lanRoom.Visibility = room == null ? Visibility.Collapsed : Visibility.Visible;
            lanCancel.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
            string progress = lanState.S("progress");
            string notice = lanState.S("notice");
            lanNotice.Text = progress != "" ? progress : notice != "" ? notice :
                room == null && Chosen(lanHosts) == "" ? "通过 EBuild 启动游戏后，可在这里选择房主实例。" : "";
            if (room == null) { roomViewKey = ""; lanRoom.Children.Clear(); return; }
            var compatible = instances.Where(item => !item.B("externalMissing") && item.S("version") == room.S("version") &&
                item.S("loader") == room.S("loader") && (room.S("loaderVersion") == "" || item.S("loaderVersion") == room.S("loaderVersion"))).ToArray();
            var ready = compatible.Where(item => Minecraft.CanQuickJoin(item.S("version")) && Game(item.S("id")) == null).ToArray();
            key = J.Dump(new object[] { room, working, ready.Select(item => item.S("id")).ToArray() });
            if (roomViewKey == key) return;
            roomViewKey = key;
            lanRoom.Children.Clear();
            var host = room.B("host");
            var content = Stack(Heading(host ? room.S("name") : "已加入好友房间"), Note(InstanceVersion(room) + " " + room.S("loaderVersion")),
                Note(room.S("connection")));
            if (host) {
                var shortCode = room.B("shortInvite");
                var code = Input(room.S("invite"), readOnly: true);
                if (shortCode) code.FontFamily = new System.Windows.Media.FontFamily("Consolas");
                content.Children.Add(Field("邀请码", code));
                if (room.B("shareMods")) content.Children.Add(Note("已共享 " + room.I("sharedCount") + " 个实例文件"));
                var copy = Button("复制邀请码", async () => { await Call("lan:copy", "invite"); Say("已复制邀请码"); }, true);
                var rotate = Button("更换邀请码", async () => {
                    if (Confirm("更换邀请码", "原邀请码将失效。是否生成新邀请码？")) await Call("lan:rotate");
                });
                copy.IsEnabled = rotate.IsEnabled = !working;
                var roomActions = Actions(copy, rotate);
                if (shortCode) {
                    var legacy = Button("复制兼容长码", async () => { await Call("lan:copy", "legacy-invite"); Say("已复制兼容长码"); });
                    legacy.IsEnabled = !working;
                    roomActions.Children.Add(legacy);
                }
                content.Children.Add(roomActions);
            } else {
                content.Children.Add(Field("游戏地址", Input(room.S("address"), readOnly: true)));
                content.Children.Add(Actions(Button("复制地址", () => Call("lan:copy", "address"))));
                content.Children.Add(Note("也可以在 Minecraft 的「多人游戏 → 直接连接」中粘贴此地址。"));
                content.Children.Add(new Expander { Header = "遇到「无效会话」？", Content =
                    Note("房主可能要求正版验证。请用拥有 Java 版资格的微软账号重新登录，然后重启 Minecraft。") });
                if (ready.Length != 0) {
                    var choicesForGuest = Picker(ready.Select(InstanceOption), selected);
                    content.Children.Add(Field("同版本实例", choicesForGuest));
                    content.Children.Add(Actions(Button("启动并加入", () => StartGame(Chosen(choicesForGuest), 0, room.S("id")), true)));
                } else content.Children.Add(Note(compatible.Length == 0 ? "本机缺少同版本实例。" :
                    "可以从已打开的游戏复制地址加入。"));
                bool shared = room.B("shareMods");
                if (shared || compatible.Length == 0) content.Children.Add(Actions(
                    Button(room.B("packFiles") ? "下载同款整合包" : shared ? "下载相同实例" : "下载同版本游戏",
                        () => DownloadRoomInstance(room.S("id")), ready.Length == 0)));
            }
            content.Children.Add(Actions(Button(host ? "关闭房间" : "退出联机", () => Call("lan:leave"))));
            lanRoom.Children.Add(Card(content));
        }

        void ResetLanShareFiles() {
            lanShareFiles = J.Map("quests", true, "scripts", true, "resources", true);
        }

        async Task EditLanShareFiles() {
            var instance = backend.InstancePath(Chosen(lanHosts));
            var quests = Check("FTB 任务书", lanShareFiles.B("quests"));
            var scripts = Check("KubeJS 和 CraftTweaker 脚本及数据", lanShareFiles.B("scripts"));
            var resources = Check("资源包", lanShareFiles.B("resources"));
            var configs = lanShareFiles.A("configs").Select(file => file.S()).ToList();
            var list = new ListBox { MaxHeight = 95, IsTabStop = false };
            Button clear = null;
            Action refresh = () => {
                list.ItemsSource = configs.Select(file => LanPackFiles.ConfigPath(instance, file)).ToArray();
                list.Visibility = configs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                clear.IsEnabled = configs.Count != 0;
            };
            clear = Button("清空已选配置", () => { configs.Clear(); refresh(); return Task.CompletedTask; });
            var content = Stack(Heading("共享内容", 16), Note("已启用的模组会自动加入清单。"), quests, scripts, resources,
                Note("其他配置可从 config 或 defaultconfigs 文件夹手动添加。"), list);
            var dialog = new TaskDialog(this, "共享实例文件", content);
            content.Children.Add(Actions(Button("添加配置文件…", () => {
                var start = Path.Combine(instance, "config");
                var picker = new Microsoft.Win32.OpenFileDialog { Title = "选择要共享的配置文件", Multiselect = true,
                    InitialDirectory = Directory.Exists(start) ? start : instance };
                if (picker.ShowDialog(dialog) == true) {
                    foreach (var file in picker.FileNames) LanPackFiles.ConfigPath(instance, file);
                    configs = configs.Concat(picker.FileNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    refresh();
                }
                return Task.CompletedTask;
            }), clear));
            refresh();
            if (await dialog.Result("确定") == true)
                lanShareFiles = J.Map("quests", quests.IsChecked == true, "scripts", scripts.IsChecked == true,
                    "resources", resources.IsChecked == true, "configs", configs.ToArray());
        }

        async Task DownloadRoomInstance(string room) {
            if (jobActive || mutations != 0) throw new IOException("请等待当前下载任务结束。");
            object plan;
            mutations++;
            Say("正在核对共享文件…");
            try { plan = await Call("lan:copy-plan", room); }
            finally { mutations--; }
            Say("就绪");
            if (route != "lan" || lanState.At("room").S("id") != room) return;
            var files = plan.A("files");
            var reuse = files.Count(file => file.B("reuse"));
            var description = files.Length == 0 ? "将安装同版本游戏和加载器。" :
                "待下载 " + (files.Length - reuse) + " 个文件 · 可复用本机 " + reuse + " 个文件";
            var summary = Stack(Heading(InstanceVersion(plan) + " " + plan.S("loaderVersion"), 16),
                Note(description), Note("将创建一个新实例。"));
            if (files.Length != 0) {
                var counts = files.GroupBy(file => LanPackFiles.Group(LanModShare.Relative(file)))
                    .Select(group => group.Key + " " + group.Count());
                summary.Children.Add(Note(String.Join(" · ", counts)));
                summary.Children.Add(new ListBox {
                    ItemsSource = files.Select(file => (file.B("reuse") ? "本机复用  " : "待下载  ") + LanModShare.Relative(file)).ToArray(),
                    MaxHeight = 145, IsTabStop = false
                });
            }
            var original = plan.At("original");
            if (original != null) summary.Children.Add(Note("可从 Modrinth 安装原版整合包，房主的自定义改动需通过实例文件下载。"));
            var dialog = new TaskDialog(this, "下载房主实例", summary);
            var installOriginal = false;
            if (original != null) dialog.Add("安装原版整合包", () => {
                installOriginal = true;
                dialog.Accept();
                return Task.CompletedTask;
            });
            if (await dialog.Result(original == null ? "下载并安装" : "下载房主实例") != true) return;
            await TrackInstallation(() => installOriginal ? RunJob("lan:install-original", room, original.S("sha512")) :
                RunJob("lan:install-copy", room, plan.S("manifestKey")));
            UpdateLan();
        }

        async Task DetectPort(bool explicitRequest) {
            string id = Chosen(lanHosts);
            if (route != "lan" || id == "" || lanState.B("working")) return;
            var revision = pageRevision;
            int.TryParse((await Call("lan:port", id)).S(), out int port);
            if (revision != pageRevision || id != Chosen(lanHosts)) return;
            suggestedPort = port;
            if (port > 0 && lanPort.Text == "") lanPort.Text = port.ToString();
            if (explicitRequest) lanNotice.Text = port > 0 ? "已识别端口 " + port : "暂未识别。请开放局域网，或填入游戏聊天栏显示的端口。";
        }

        async Task EditNode() {
            var node = Input(lanState.S("node"));
            var dialog = new TaskDialog(this, "联机设置", Stack(Field("自定义联机节点地址", node, "留空使用默认节点。")));
            dialog.Add("恢复默认", () => { node.Clear(); return Task.CompletedTask; });
            if (await dialog.Result("保存节点", async () => {
                lanState = await Call("lan:node", node.Text.Trim());
                return true;
            }) == true) { await RefreshInstances(); UpdateLan(); Say("联机设置已保存"); }
        }
    }
}
