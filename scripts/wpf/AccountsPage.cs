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
        FrameworkElement offlineFields;
        PlayerPreview player;

        void ShowAccounts() {
            accountMode = Picker(new[] { Option("offline", "离线档案"), Option("microsoft", "微软正版账号") }, settings.S("accountMode"));
            accountName = Input(settings.S("username"), 16);
            skinModel = Picker(new[] { Option("classic", "经典"), Option("slim", "纤细") }, settings.S("offlineSkinModel"));
            offlineFields = Columns(Field("离线玩家名称", accountName), Field("手臂模型", skinModel), .65);
            accountActions = Stack();
            codeDisplay = Input("", readOnly: true);
            devicePanel = Card(Stack(Note("在微软登录页输入设备代码，然后完成登录。"),
                Field("设备代码", codeDisplay), Actions(Button("复制代码", () => {
                    if (deviceCode != "") Clipboard.SetText(deviceCode);
                    return Task.CompletedTask;
                }), Button("打开微软登录页", () => Call("account:browser"), true),
                Button("取消登录", () => Call("task:cancel")))));
            var save = Button("保存账号设置", async () => {
                var mode = Chosen(accountMode);
                if (mode == "microsoft" && microsoft == null) throw new IOException("请先登录微软账号。");
                await RunJob("settings:save", J.Map("accountMode", mode, "username", accountName.Text.Trim(), "offlineSkinModel", Chosen(skinModel)));
                await RefreshInstances();
                UpdateAccountIdentity();
                Say("账号设置已保存");
            }, true);
            save.Margin = new Thickness(0, 0, 0, 10);
            var page = ShowPage("accounts", AccountsSection, "账号", headerActions: save);
            var modeField = Field("启动档案", accountMode);
            modeField.Margin = new Thickness(0, 0, 0, 12);
            var info = Stack(Card(Stack(modeField, accountActions)), devicePanel);
            player = new PlayerPreview { Height = 144 };
            var stage = new Border { Child = player, Padding = new Thickness(0, 4, 0, 4), CornerRadius = new CornerRadius(6) };
            UseResource(stage, Border.BackgroundProperty, "Canvas");
            var previewActions = Actions(Button("正面", () => { player.Turn(false); return Task.CompletedTask; }),
                Button("背面", () => { player.Turn(true); return Task.CompletedTask; }));
            previewActions.Margin = new Thickness(0, 9, 0, 0);
            var preview = Stack(Heading("角色预览"), stage, previewActions, Note("拖动或按方向键旋转"));
            page.Children.Add(Columns(info, Stack(Card(preview)), .64));
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
            if (Chosen(accountMode) == "offline") {
                accountActions.Children.Add(offlineFields);
                accountActions.Children.Add(AccountAppearanceRow("skin", "皮肤"));
                accountActions.Children.Add(AccountAppearanceRow("cape", "披风"));
                accountActions.Children.Add(Note("离线皮肤和披风用于启动器内预览。"));
            } else {
                BuildMicrosoftAccountControls();
            }
            UpdateAccountTextures();
        }

        FrameworkElement AccountAppearanceRow(string part, string label) {
            var import = Button("导入" + label, async () => {
                var appearance = await RunJob("account:appearance-import", part);
                if (appearance == null) return;
                offline = appearance;
                if (route == "accounts") player.Turn(part == "cape");
                UpdateAccountControls();
                UpdateAccountIdentity();
                Say(label + "已导入");
            });
            var caption = Text(label);
            caption.VerticalAlignment = VerticalAlignment.Center;
            var row = offline.S(part) == "" ? WithActions(caption, import) : WithActions(caption, import,
                Button(part == "skin" ? "恢复默认皮肤" : "移除披风", async () => {
                    offline = await RunJob("account:appearance-remove", part);
                    UpdateAccountControls();
                    UpdateAccountIdentity();
                    Say(label + "已还原");
                }));
            row.Margin = new Thickness(0, 0, 0, 5);
            return row;
        }

        void BuildMicrosoftAccountControls() {
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
            if (microsoft != null) {
                accountActions.Children.Add(Actions(Button("改名", () => OfficialAppearance("name")),
                    Button("换皮肤", () => OfficialAppearance("skin")), Button("管理披风", () => OfficialAppearance("cape"))));
            }
        }

        async Task RefreshMicrosoft() {
            var updated = await RunJob("account:refresh");
            if (updated == null) return;
            microsoft = updated;
            UpdateAccountControls();
            await LoadOfficialTextures();
            Say("正版档案已刷新");
        }

        Task OfficialAppearance(string part) {
            var account = microsoft;
            if (account == null) throw new IOException("请先登录微软账号。");
            switch (part) {
                case "name": return EditOfficialName(account);
                case "skin": return EditOfficialSkin(account);
                case "cape": return EditOfficialCape(account);
                default: throw new IOException("请选择要修改的正版档案资料。");
            }
        }

        Task EditOfficialName(object account) {
            var name = Input("", 16);
            var content = Stack(Note("当前档案名 · " + account.S("name")), Field("新 Java 档案名", name, "3–16 位字母、数字或下划线"),
                Note("Java 档案名每 30 天可更改一次。"));
            return SubmitOfficial("修改正版档案名", "name", content, "确认改名",
                () => SaveOfficialProfile("正版档案名已更新", "account:change-name", account.S("id"), name.Text.Trim()));
        }

        Task EditOfficialSkin(object account) {
            byte[] png = null;
            BitmapSource skin = null;
            const string skinHint = "请选择 64 × 64 或 64 × 32 的 PNG 皮肤";
            var filename = InstanceText(skinHint, 12);
            filename.VerticalAlignment = VerticalAlignment.Center;
            var fileError = Note("");
            fileError.Visibility = Visibility.Collapsed;
            UseResource(fileError, TextBlock.ForegroundProperty, "Danger");
            var model = Picker(new[] { Option("classic", "经典"), Option("slim", "纤细") }, account.S("variant"));
            var preview = new PlayerPreview { Height = 128 };
            void ShowPreview() { preview.Set(skin ?? officialSkin, officialCape, Chosen(model) == "slim"); }
            model.SelectionChanged += (_, __) => ShowPreview();
            ShowPreview();
            var choose = Button("选择 PNG", () => {
                var picker = new Microsoft.Win32.OpenFileDialog { Filter = "PNG 皮肤|*.png", Title = "选择正版皮肤" };
                if (picker.ShowDialog(ActiveDialog) != true) return Task.CompletedTask;
                try {
                    var candidate = LocalAppearance.ReadPng("skin", picker.FileName);
                    var decoded = DecodeImage(candidate, 0);
                    if (decoded == null) throw new IOException("无法读取这张 PNG 皮肤。");
                    png = candidate;
                    skin = decoded;
                    model.IsEnabled = skin.PixelHeight != 32;
                    if (!model.IsEnabled) model.SelectedIndex = 0;
                    filename.Text = Path.GetFileName(picker.FileName);
                    filename.ToolTip = filename.Text;
                    ShowPreview();
                    fileError.Visibility = Visibility.Collapsed;
                } catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) {
                    png = null;
                    skin = null;
                    filename.Text = skinHint;
                    filename.ToolTip = null;
                    model.IsEnabled = true;
                    ShowPreview();
                    fileError.Text = error is UnauthorizedAccessException ? "无法读取这张皮肤文件。" : error.Message;
                    fileError.Visibility = Visibility.Visible;
                }
                return Task.CompletedTask;
            });
            var content = Stack(WithActions(filename, choose), fileError, Field("手臂模型", model), preview);
            return SubmitOfficial("更换正版皮肤", "skin", content, "上传并使用", () => {
                if (png == null) throw new IOException("请先选择一张 PNG 皮肤。");
                return SaveOfficialProfile("正版皮肤已更新", "account:change-skin", account.S("id"), png, Chosen(model));
            });
        }

        async Task EditOfficialCape(object account) {
            var current = await RunJob("account:refresh");
            if (current == null) return;
            if (current.S("id") != account.S("id")) throw new IOException("当前账号已切换，请重新打开披风管理。");
            microsoft = current;
            UpdateAccountControls();
            _ = Guard(LoadOfficialTextures);
            await ShowOfficialCapes(current);
        }

        Task ShowOfficialCapes(object current) {
            var owned = current.A("capes");
            var choices = new[] { Option("", "不佩戴披风") }.Concat(owned.Select((cape, index) =>
                Option(cape.S("id"), cape.S("alias") == "" ? "披风 " + (index + 1) : cape.S("alias"))));
            var capes = Picker(choices, current.S("capeId"));
            var content = Stack(Field("账号披风", capes));
            if (owned.Length == 0) content.Children.Add(Note("此账号暂时没有可用的正版披风。"));
            return SubmitOfficial("管理正版披风", "cape", content, "应用披风",
                () => SaveOfficialProfile("正版披风已更新", "account:change-cape", current.S("id"), Chosen(capes)));
        }

        Task<bool?> SubmitOfficial(string title, string part, UIElement content, string label, Func<Task<bool>> submit) {
            var dialog = new TaskDialog(this, title, content);
            bool submitting = false;
            var website = dialog.Add("打开官网", () => Call("resources:open-link", part == "skin"
                ? "https://www.minecraft.net/msaprofile/mygames/editskin" : "https://www.minecraft.net/msaprofile/mygames"));
            var cancel = dialog.Add("取消", () => { dialog.Close(); return Task.CompletedTask; });
            dialog.Closing += (_, e) => e.Cancel = submitting;
            Button confirm = null;
            confirm = dialog.Add(label, async () => {
                submitting = true;
                confirm.Content = "正在提交…";
                website.IsEnabled = cancel.IsEnabled = content.IsEnabled = false;
                try {
                    if (!await submit()) return;
                    submitting = false;
                    dialog.Accept();
                } finally {
                    submitting = false;
                    confirm.Content = label;
                    website.IsEnabled = cancel.IsEnabled = content.IsEnabled = true;
                }
            }, true);
            return dialog.Open();
        }

        async Task<bool> SaveOfficialProfile(string message, string command, params object[] arguments) {
            var result = await RunJob(command, arguments);
            if (result == null) return false;
            microsoft = result.At("profile") ?? microsoft;
            UpdateAccountControls();
            UpdateAccountIdentity();
            _ = Guard(LoadOfficialTextures);
            Say(result.B("synced") ? message : "修改已提交，请刷新正版档案确认结果。");
            return true;
        }

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
            if (!ShowingGameCard || homeIdentity == null) return;
            bool official = settings.S("accountMode") == "microsoft";
            homeIdentity.Text = official ? "官方登录 · " + (microsoft?.S("name") ?? "未登录") : "离线登录 · " + settings.S("username");
            homeAvatar.Source = PlayerPreview.Avatar(official ? officialSkin : ImageFromData(offline.S("skin")));
            if (fetch && official && microsoft != null && loadedSkin == "") _ = Guard(LoadOfficialTextures);
        }
    }
}
