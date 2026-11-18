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
                if (!fillingHosts) { suggestedPort = 0; lanPort.Clear(); await Guard(() => DetectPort(false)); }
            };
            lanPort = Input("", 5);
            lanInvite = Input("", 1200);
            lanDetect = Button("识别端口", () => DetectPort(true));
            lanCreate = Button("创建房间", async () => {
                if (lanPort.Text.Trim() == "" && suggestedPort == 0) await DetectPort(true);
                int port = lanPort.Text.Trim() == "" && suggestedPort != 0 ? suggestedPort : Number(lanPort.Text, 1, 65535, "局域网端口");
                await Call("lan:create", Chosen(lanHosts), port);
            }, true);
            lanJoin = Button("加入房间", async () => {
                if (lanInvite.Text.Trim() == "") throw new IOException("请粘贴房主的邀请码。");
                await Call("lan:join", lanInvite.Text.Trim());
            }, true);
            lanLobby = Stack(Columns(Card(Stack(Heading("创建房间"), Note("在单人世界中按 Esc，选择「对局域网开放」。"),
                Field("房主实例", lanHosts), Field("局域网端口", lanPort), Actions(lanDetect, lanCreate))),
                Card(Stack(Heading("加入房间"), Note("向房主索取邀请码。"), Field("邀请码", lanInvite), Actions(lanJoin)))));
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
                if (Chosen(lanHosts) != previous) { suggestedPort = 0; lanPort.Clear(); }
                lanHostKey = key;
            }
            lanOptions.IsEnabled = !working && room == null;
            lanHosts.IsEnabled = lanDetect.IsEnabled = lanPort.IsEnabled = lanCreate.IsEnabled = !working && Chosen(lanHosts) != "";
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
                content.Children.Add(Field("邀请码", Input(room.S("invite"), readOnly: true)));
                var copy = Button("复制邀请码", () => Call("lan:copy", "invite"), true);
                var rotate = Button("更换邀请码", async () => {
                    if (Confirm("更换邀请码", "原邀请码将失效。是否生成新邀请码？")) await Call("lan:rotate");
                });
                copy.IsEnabled = rotate.IsEnabled = !working;
                content.Children.Add(Actions(copy, rotate));
            } else {
                content.Children.Add(Field("游戏地址", Input(room.S("address"), readOnly: true)));
                content.Children.Add(Actions(Button("复制地址", () => Call("lan:copy", "address"))));
                content.Children.Add(Note("也可以在 Minecraft 的「多人游戏 → 直接连接」中粘贴此地址。"));
                content.Children.Add(new Expander { Header = "遇到「无效会话」？", Content =
                    Note("房主可能要求正版验证。请用拥有 Java 版资格的微软账号重新登录，然后重启 Minecraft。") });
                if (ready.Length != 0) {
                    var choicesForGuest = Picker(ready.Select(InstanceOption), selected);
                    content.Children.Add(Field("匹配实例", choicesForGuest));
                    content.Children.Add(Actions(Button("启动并加入", () => StartGame(Chosen(choicesForGuest), 0, room.S("id")), true)));
                } else content.Children.Add(Note(compatible.Length == 0 ? "未找到匹配实例。请使用和房主相同的游戏及加载器版本。" :
                    "可以从已打开的游戏复制地址加入。"));
            }
            content.Children.Add(Actions(Button(host ? "关闭房间" : "退出联机", () => Call("lan:leave"))));
            lanRoom.Children.Add(Card(content));
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
