using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EBuild.Native
{
    public partial class Launcher
    {
        object microsoft;
        object offline;
        BitmapSource officialSkin;
        BitmapSource officialCape;
        string loadedSkin = "";
        string loadedCape = "";
        TextBox accountName;
        TextBox codeDisplay;
        FrameworkElement devicePanel;
        ComboBox accountMode;
        ComboBox skinModel;
        StackPanel accountActions;
        FrameworkElement armField;
        PlayerPreview player;

        void ShowAccounts() {
            var page = ShowPage("accounts", 4, "账号");
            accountMode = Picker(new[] { Option("offline", "离线档案"), Option("microsoft", "微软正版账号") }, settings.S("accountMode"));
            accountName = Input(settings.S("username"), 16);
            skinModel = Picker(new[] { Option("classic", "经典"), Option("slim", "纤细") }, settings.S("offlineSkinModel"));
            armField = Field("手臂模型", skinModel);
            accountActions = Stack();
            codeDisplay = Input("", readOnly: true);
            devicePanel = Card(Stack(Note("在微软登录页输入设备代码，然后完成登录。"),
                Field("设备代码", codeDisplay), Actions(Button("复制代码", () => {
                    if (deviceCode != "") Clipboard.SetText(deviceCode);
                    return Task.CompletedTask;
                }), Button("打开微软登录页", () => Call("account:browser"), true),
                Button("取消登录", () => Call("task:cancel")))));
            var info = Stack(Field("启动档案", accountMode), Field("离线玩家名称", accountName), accountActions, devicePanel,
                Actions(Button("保存账号设置", async () => {
                    var mode = Chosen(accountMode);
                    if (mode == "microsoft" && microsoft == null) throw new IOException("请先登录微软账号。");
                    await RunJob("settings:save", J.Map("accountMode", mode, "username", accountName.Text.Trim(), "offlineSkinModel", Chosen(skinModel)));
                    await RefreshInstances();
                    UpdateAccountIdentity();
                    Say("账号设置已保存");
                }, true)));
            player = new PlayerPreview();
            var preview = Stack(Heading("角色预览"), player, Actions(Button("正面", () => { player.Turn(false); return Task.CompletedTask; }),
                Button("背面", () => { player.Turn(true); return Task.CompletedTask; })), Note("拖动或按方向键旋转"));
            page.Children.Add(Columns(Card(info), Card(preview), .85));
            accountMode.SelectionChanged += (_, __) => UpdateAccountControls();
            skinModel.SelectionChanged += (_, __) => UpdateAccountTextures();
            UpdateAccountControls();
            UpdateDeviceCode();
            _ = Guard(LoadOfficialTextures);
        }

        void UpdateDeviceCode() {
            if (route != "accounts" || codeDisplay == null) return;
            codeDisplay.Text = deviceCode;
            devicePanel.Visibility = deviceCode == "" ? Visibility.Collapsed : Visibility.Visible;
        }

        void UpdateAccountControls() {
            if (route != "accounts" || accountActions == null) return;
            accountActions.Children.Clear();
            accountActions.Children.Add(Heading(microsoft == null ? "微软账号 · 未登录" : "微软账号 · " + microsoft.S("name"), 14));
            var signIn = Button("登录微软账号", async () => {
                microsoft = await RunJob("account:login") ?? microsoft;
                await RefreshInstances();
                if (route == "accounts") {
                    accountMode.SelectedItem = accountMode.Items.Cast<Choice>().FirstOrDefault(item => item.Key == settings.S("accountMode"));
                    UpdateAccountControls();
                }
                await LoadOfficialTextures();
            }, microsoft == null);
            var tools = Actions(signIn);
            if (microsoft != null) {
                tools.Children.Add(Button("刷新档案", RefreshMicrosoft));
                tools.Children.Add(Button("退出登录", async () => {
                    if (!Confirm("退出微软账号", "退出后将切换到离线档案。是否继续？")) return;
                    await RunJob("account:logout");
                    microsoft = null;
                    await RefreshInstances();
                    if (route == "accounts") {
                        accountMode.SelectedIndex = 0;
                        UpdateAccountControls();
                    }
                    await LoadOfficialTextures();
                }));
            }
            accountActions.Children.Add(tools);
            if (Chosen(accountMode) == "offline") {
                accountActions.Children.Add(armField);
                foreach (string part in new[] { "skin", "cape" }) {
                    string label = part == "skin" ? "皮肤" : "披风";
                    var actions = Actions(Button("导入 " + label, async () => {
                        var appearance = await RunJob("account:appearance-import", part);
                        if (appearance == null) return;
                        offline = appearance;
                        if (route == "accounts") player.Turn(part == "cape");
                        UpdateAccountControls();
                        UpdateAccountIdentity();
                        Say(label + "已导入");
                    }));
                    if (offline.S(part) != "") actions.Children.Add(Button(part == "skin" ? "恢复默认皮肤" : "移除披风", async () => {
                        offline = await RunJob("account:appearance-remove", part);
                        UpdateAccountControls();
                        UpdateAccountIdentity();
                        Say(label + "已还原");
                    }));
                    accountActions.Children.Add(actions);
                }
                accountActions.Children.Add(Note("离线皮肤和披风用于启动器内预览。"));
            } else if (microsoft != null) {
                accountActions.Children.Add(Actions(Button("改名", () => OfficialAppearance("name")),
                    Button("换皮肤", () => OfficialAppearance("skin")), Button("管理披风", () => OfficialAppearance("cape"))));
                accountActions.Children.Add(Note("官网修改后，可回到这里刷新档案。"));
            }
            UpdateAccountTextures();
        }

        async Task RefreshMicrosoft() {
            var updated = await RunJob("account:refresh");
            if (updated == null) return;
            microsoft = updated;
            UpdateAccountControls();
            await LoadOfficialTextures();
            Say("正版档案已刷新");
        }

        Task OfficialAppearance(string part) => Call("resources:open-link", part == "skin" ?
            "https://www.minecraft.net/msaprofile/mygames/editskin" : "https://www.minecraft.net/msaprofile/mygames");

        Task ManageAppearance(string part) {
            if (settings.S("accountMode") == "microsoft" && microsoft != null) return OfficialAppearance(part);
            ShowAccounts();
            if (part == "name") { accountName.Focus(); accountName.SelectAll(); }
            if (part == "cape") player.Turn(true);
            return Task.CompletedTask;
        }

        async Task LoadOfficialTextures() {
            string skin = microsoft.S("skin"), cape = microsoft.S("cape");
            if (loadedSkin == skin && loadedCape == cape) return;
            var pictures = await Task.WhenAll(RemoteImage(skin, 0, "textures.minecraft.net"),
                RemoteImage(cape, 0, "textures.minecraft.net"));
            if (skin != microsoft.S("skin") || cape != microsoft.S("cape")) return;
            officialSkin = pictures[0];
            officialCape = pictures[1];
            loadedSkin = pictures[0] != null ? skin : "";
            loadedCape = pictures[1] != null ? cape : "";
            UpdateAccountTextures();
            UpdateAccountIdentity(false);
        }

        void UpdateAccountTextures() {
            if (route != "accounts" || player == null) return;
            bool official = Chosen(accountMode) == "microsoft";
            player.Set(official ? officialSkin : ImageFromData(offline.S("skin")),
                official ? officialCape : ImageFromData(offline.S("cape")),
                (official ? microsoft.S("variant") : Chosen(skinModel)) == "slim");
        }

        void UpdateAccountIdentity(bool fetch = true) {
            if (route != "home" || homeIdentity == null) return;
            bool official = settings.S("accountMode") == "microsoft";
            homeIdentity.Text = official ? "官方登录 · " + (microsoft?.S("name") ?? "未登录") : "离线登录 · " + settings.S("username");
            homeAvatar.Source = PlayerPreview.Avatar(official ? officialSkin : ImageFromData(offline.S("skin")));
            if (fetch && official && microsoft != null && loadedSkin == "") _ = Guard(LoadOfficialTextures);
        }
    }
}
