using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace EBuild.Native
{
    public partial class Launcher : Window, LauncherWindow
    {
        const int HomeSection = 0, InstancesSection = 1, DownloadsSection = 2, LanSection = 3, SettingsSection = 4, AccountsSection = 5;

        readonly string profile;
        readonly List<Button> nav = new List<Button>();
        readonly DispatcherTimer diagnosticsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        Backend backend;
        object settings;
        object[] instances = Array.Empty<object>();
        object[] games = Array.Empty<object>();
        object[] downloads = Array.Empty<object>();
        object jobProgress;
        string selected = "";
        string route = "";
        string completedInstance = "";
        string deviceCode = "";
        int section;
        int pageRevision;
        bool leaving;
        bool initializing = true;
        bool jobActive;
        int mutations;
        bool checkingExternal;
        Func<Task<bool>> leavePage;

        [STAThread]
        static void Main(string[] args) {
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
            System.Net.ServicePointManager.DefaultConnectionLimit = 32;
            try {
                var profile = LauncherProfile.Resolve();
                if (LauncherUpdate.HandleWorker(profile, args)) return;
                using (var mutex = new Mutex(false, LauncherProfile.MutexName(profile))) {
                    try { if (!mutex.WaitOne(0)) return; }
                    catch (AbandonedMutexException) { }
                    try {
                        var launcher = new Launcher(profile);
                        launcher.updateStartupArgs = args;
                        new Application().Run(launcher);
                    }
                    finally { mutex.ReleaseMutex(); }
                }
            } catch (Exception error) {
                MessageBox.Show(error.Message, "EBuild 启动器", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        Launcher(string profile) {
            this.profile = profile;
            InitializeComponent();
            var labels = new[] { "首页", "实例", "下载", "联机", "设置", "账号" };
            for (int index = 0; index < labels.Length; index++) {
                int destination = index;
                var button = Button(labels[index], () => Navigate(destination));
                button.Style = (Style)FindResource("NavigationButton");
                button.ClearValue(MarginProperty);
                button.IsEnabled = false;
                nav.Add(button);
                Navigation.Children.Add(button);
            }
            Navigation.SizeChanged += (_, __) => MoveNavigationMark(false);
            ApplyTheme("system");
            CancelJob.Click += async (_, __) => await Guard(() => Call("task:cancel"));
            ViewInstalled.Click += async (_, __) => await Guard(() => ViewInstance(completedInstance));
            Loaded += async (_, __) => await Guard(Initialize);
            Activated += async (_, __) => {
                if (!initializing && !leaving) await Guard(RefreshExternalPaths);
            };
            PreviewDragOver += DragFiles;
            DragLeave += (_, __) => DropHint.Visibility = Visibility.Collapsed;
            Drop += async (_, e) => await Guard(() => DropFiles(e));
            diagnosticsTimer.Tick += async (_, __) => await Guard(PollDiagnostics);
            SystemEvents.UserPreferenceChanged += SystemThemeChanged;
        }

        async Task Initialize() {
            backend = new Backend(this, profile, Notify);
            settings = await Call("settings:get");
            ApplyTheme(settings.S("theme"));
            await RefreshInstances();
            await RefreshDownloads();
            games = (await Call("game:list")).A();
            lanState = await Call("lan:get");
            string accountError = "";
            try {
                microsoft = await Call("account:summary");
                offline = await Call("account:appearance");
            } catch (Exception error) { accountError = "无法读取账号档案 · " + error.GetBaseException().Message; }
            initializing = false;
            foreach (var button in nav) button.IsEnabled = true;
            ShowHome();
            Say(accountError == "" ? "就绪" : accountError);
            diagnosticsTimer.Start();
            await InitializeUpdates();
            if (microsoft.B("needsRefresh")) await Guard(RefreshMicrosoft);
        }

        IntPtr System.Windows.Forms.IWin32Window.Handle => new WindowInteropHelper(this).Handle;
        void LauncherWindow.Minimize() { WindowState = WindowState.Minimized; }
        void MinimizeWindow(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
        void ToggleMaximizeWindow(object sender, RoutedEventArgs e) {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        }
        void CloseWindow(object sender, RoutedEventArgs e) => Close();

        void ApplyExpanderHeaderFocus(object sender, RoutedEventArgs e) {
            if (sender is Expander expander && expander.Template?.FindName("HeaderSite", expander) is ToggleButton header)
                header.FocusVisualStyle = (Style)expander.FindResource(SystemParameters.FocusVisualStyleKey);
        }

        void LauncherWindow.Post(Action action) {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(action);
        }

        Task<object> Call(string key, params object[] args) => backend.Call(key, args);
        object Active => instances.FirstOrDefault(item => item.S("id") == selected);
        object FindInstance(string id) => instances.FirstOrDefault(item => item.S("id") == id);

        async Task Guard(Func<Task> action) {
            try { await action(); }
            catch (OperationCanceledException) { Say("任务已取消"); }
            catch (Exception error) { Say(error.GetBaseException().Message); }
        }

        async Task<object> RunJob(string key, params object[] args) {
            if (jobActive || mutations != 0 || leaving) throw new IOException("请等待当前任务结束。");
            mutations++;
            try { return await Call(key, args); }
            finally { mutations--; }
        }

        async Task Navigate(int index) {
            if (!await CanLeavePage()) return;
            switch (index) {
                case HomeSection: ShowHome(); break;
                case InstancesSection: ShowInstances(); break;
                case DownloadsSection: await ShowDownloads(); break;
                case LanSection: ShowLan(); break;
                case SettingsSection: ShowSettings(); break;
                case AccountsSection: ShowAccounts(); break;
            }
        }

        Task<bool> CanLeavePage() => leavePage == null ? Task.FromResult(true) : leavePage();

        StackPanel ShowPage(string name, int index, string title, Func<Task> back = null, bool animate = true,
            UIElement headerActions = null, FrameworkElement titleContent = null) {
            var oldSection = section;
            route = name;
            progressBindings.Clear();
            UpdateFooterProgress();
            section = index;
            pageRevision++;
            leavePage = null;
            for (int i = 0; i < nav.Count; i++) {
                nav[i].Tag = i == index;
                AutomationProperties.SetItemStatus(nav[i], i == index ? "当前页面" : "");
            }
            if (oldSection != index || NavigationMark.Visibility != Visibility.Visible) MoveNavigationMark(animate);
            var panel = new StackPanel();
            var header = new DockPanel { LastChildFill = headerActions == null };
            var titleText = titleContent ?? Heading(title, 23);
            titleText.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(titleText, Dock.Left);
            header.Children.Add(titleText);
            if (headerActions != null) {
                DockPanel.SetDock(headerActions, Dock.Right);
                header.Children.Add(headerActions);
            }
            if (back != null) {
                var backButton = Button("← 返回", back);
                backButton.Margin = new Thickness(0, 0, 12, 9);
                DockPanel.SetDock(backButton, Dock.Left);
                header.Children.Insert(0, backButton);
            }
            panel.Children.Add(header);
            Page.Children.Clear();
            Page.Children.Add(panel);
            PageScroll.ScrollToTop();
            if (animate) AnimateEntrance(panel, Math.Sign(index - oldSection) * 22, oldSection == index ? 11 : 0);
            return panel;
        }

        void ApplyTheme(string theme) {
            bool dark = theme == "dark" || theme != "light" &&
                Equals(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1), 0);
            Resources["Canvas"] = Brush(dark ? "#191D23" : "#F2F3F5");
            Resources["Card"] = Brush(dark ? "#222831" : "#FCFCFD");
            Resources["Ink"] = Brush(dark ? "#E8ECF2" : "#3D3D3D");
            Resources["Muted"] = Brush(dark ? "#BFC7D3" : "#666666");
            Resources["Line"] = Brush(dark ? "#3B4654" : "#D9DEE5");
            Resources["Hover"] = Brush(dark ? "#2E3E54" : "#E8EDF4");
            Resources["Accent"] = Brush(dark ? "#47689C" : "#3E608F");
            Resources["AccentText"] = Brush(dark ? "#B0C8EA" : "#385A89");
            Resources["AccentHover"] = Brush(dark ? "#83A0CC" : "#6884AC");
            Resources["DropOverlay"] = Brush(dark ? "#E647689C" : "#E63E608F");
            Resources["Danger"] = Brush(dark ? "#F5ABA5" : "#A53D3D");
            Resources["BetaFill"] = Brush(dark ? "#3D3324" : "#F7EDDA");
            Resources["BetaText"] = Brush(dark ? "#EDC786" : "#805615");
            Resources["AlphaFill"] = Brush(dark ? "#3D3348" : "#EFE9F4");
            Resources["AlphaText"] = Brush(dark ? "#D6BEE8" : "#69517D");
        }

        void SystemThemeChanged(object sender, UserPreferenceChangedEventArgs args) {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => ApplyTheme(settings.S("theme"))));
        }

        void Say(string message) {
            Status.Text = message;
            Status.ToolTip = message;
        }

        void Notify(object update) {
            if (!Dispatcher.CheckAccess()) {
                ((LauncherWindow)this).Post(() => Notify(update));
                return;
            }
            if (leaving) return;
            string startedInstance = update.S("instanceStarted");
            if (startedInstance != "") {
                int index = Array.FindIndex(instances, item => item.S("id") == startedInstance);
                if (index >= 0) {
                    instances[index] = J.Merge(instances[index], J.Map("lastLaunch", update.S("lastLaunch")));
                    RenderInstanceLibrary();
                }
                return;
            }
            if (update.At("games") != null) {
                games = update.A("games");
                UpdateGameControls();
                UpdateLan();
                return;
            }
            if (update.At("lanState") != null) {
                lanState = update.At("lanState");
                UpdateLan();
                return;
            }
            if (update.B("downloadsChanged")) {
                Dispatcher.BeginInvoke(new Action(async () => await Guard(RefreshDownloads)));
                return;
            }
            if (update.S("launchJava") != "") {
                Dispatcher.BeginInvoke(new Action(async () => await Guard(() => AskForLaunchJava(update))));
                return;
            }
            if (update.S("launchError") != "") { Say(update.S("message")); return; }
            if (update.S("gameExit") != "") {
                if (!jobActive) Say("游戏已退出（代码 " + update.S("code") + "）");
                return;
            }
            string phase = update.S("phase");
            if (phase == "") return;
            jobProgress = update;
            jobActive = phase != "idle" && phase != "error";
            UpdateFooterProgress();
            CancelJob.Visibility = jobActive && phase != "starting" && phase != "deleting" ? Visibility.Visible : Visibility.Collapsed;
            Say(ProgressLabel(update));
            if (phase == "device") deviceCode = update.S("code");
            else if (!jobActive) deviceCode = "";
            UpdateDeviceCode();
            UpdateDownloadProgress();
            UpdateGameControls();
        }

        void UpdateFooterProgress() {
            bool download = jobProgress.S("kind") == "game-install";
            bool show = jobActive && (!download || progressBindings.Count == 0);
            Progress.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;
            int total = jobProgress.I("total");
            Progress.Style = download ? (Style)FindResource("DownloadProgressBar") : null;
            Progress.Tag = download && MotionEnabled ? "Animate" : null;
            Progress.IsIndeterminate = total <= 0;
            Progress.Maximum = Math.Max(1, total);
            Progress.Value = jobProgress.I("done");
        }

        static string ProgressLabel(object update) {
            return update.S("message") + (update.I("total") > 0 ? " · " + update.I("done") + " / " + update.I("total") : "");
        }

        async Task RefreshInstances() {
            settings = await Call("settings:get");
            instances = (await Call("instances:list")).A();
            selected = settings.S("selected");
            if (Active == null) selected = instances.FirstOrDefault().S("id");
        }

        async Task RefreshExternalPaths() {
            if (checkingExternal || !instances.Any(item => item.S("externalPath") != "")) return;
            checkingExternal = true;
            try {
                var updated = (await Call("instances:list")).A();
                bool changed = J.Dump(updated.Select(item => new[] { item.S("id"), item.S("externalMissing") }).ToArray()) !=
                    J.Dump(instances.Select(item => new[] { item.S("id"), item.S("externalMissing") }).ToArray());
                if (!changed) return;
                instances = updated;
                if (Active == null) selected = instances.FirstOrDefault().S("id");
                if (route == "instances") ShowInstances();
                if (route == "home") UpdateHomeCard();
                UpdateLan();
            } finally { checkingExternal = false; }
        }

        async Task ViewInstance(string id) {
            if (!await CanLeavePage()) return;
            await RefreshInstances();
            if (FindInstance(id) == null) { Say("实例已移除。"); return; }
            await SelectInstance(id);
            ViewInstalled.Visibility = Visibility.Collapsed;
            ShowInstances();
        }

        async Task SelectInstance(string id) {
            await Call("instances:select", id);
            bool changed = selected != id;
            selected = id;
            UpdateHomeCard(changed);
            UpdateLan();
        }

        protected override void OnClosing(CancelEventArgs e) {
            base.OnClosing(e);
            if (e.Cancel || backend == null) return;
            if (backend.Leaving) { e.Cancel = !backend.CanClose(); return; }
            if (!backend.HasGameWork && !backend.HasLanWork && !jobActive && mutations == 0 && leavePage == null) {
                e.Cancel = !backend.CanClose();
                return;
            }
            e.Cancel = true;
            if (leaving) return;
            Dispatcher.BeginInvoke(new Action(async () => await Guard(ExitLauncher)));
        }

        async Task ExitLauncher() {
            if (leaving) return;
            leaving = true;
            try {
                if (!await CanLeavePage()) return;
                if (backend.HasGameWork || backend.HasLanWork || jobActive || mutations != 0) {
                    var prompt = new TaskDialog(this, "退出 EBuild",
                        Stack(Note("运行中的游戏会继续运行。排队中的启动会取消，当前任务会暂停，联机会断开。")));
                    prompt.Add("最小化", () => {
                        prompt.Close();
                        WindowState = WindowState.Minimized;
                        return Task.CompletedTask;
                    });
                    if (await prompt.Result("保留游戏并退出") != true) return;
                }
                await Call("game:keep-and-exit");
            } finally { if (!backend.Leaving) leaving = false; }
        }

        protected override void OnClosed(EventArgs e) {
            updateLifetime.Cancel();
            diagnosticsTimer.Stop();
            SystemEvents.UserPreferenceChanged -= SystemThemeChanged;
            base.OnClosed(e);
        }

        static SolidColorBrush Brush(string color) {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }

        static void UseResource(FrameworkElement element, DependencyProperty property, string key) => element.SetResourceReference(property, key);

        Button Button(string label, Func<Task> action, bool primary = false) {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 6, 5), HorizontalAlignment = HorizontalAlignment.Stretch };
            if (primary) button.Style = (Style)FindResource("Primary");
            button.Click += async (_, __) => await Guard(action);
            return button;
        }

        static StackPanel Stack(params UIElement[] items) {
            var stack = new StackPanel();
            foreach (var item in items) stack.Children.Add(item);
            return stack;
        }

        static WrapPanel Actions(params UIElement[] items) {
            var actions = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
            foreach (var item in items) actions.Children.Add(item);
            return actions;
        }

        static Border Card(UIElement child) {
            var card = new Border { Child = child, Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 9),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9) };
            UseResource(card, Border.BackgroundProperty, "Card");
            UseResource(card, Border.BorderBrushProperty, "Line");
            return card;
        }

        static TextBlock Text(string value, double size = 13) => new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap };

        static TextBlock Heading(string value, double size = 16) {
            var text = Text(value, size);
            text.FontWeight = FontWeights.SemiBold;
            text.Margin = new Thickness(0, 0, 0, 9);
            return text;
        }

        static TextBlock Note(string value) {
            var text = Text(value, 12);
            UseResource(text, TextBlock.ForegroundProperty, "Muted");
            text.Margin = new Thickness(0, 2, 0, 6);
            return text;
        }

        static FrameworkElement Field(string caption, Control control, string hint = "", UIElement controlBody = null) {
            AutomationProperties.SetName(control, caption);
            var label = new Label { Content = caption, Target = control, Padding = new Thickness(0, 0, 0, 4) };
            UseResource(label, Control.ForegroundProperty, "Ink");
            var stack = Stack(label, controlBody ?? control);
            if (hint != "") stack.Children.Add(Note(hint));
            stack.Margin = new Thickness(0, 0, 0, 9);
            return stack;
        }

        static TextBox Input(string value = "", int max = 0, bool readOnly = false) =>
            new TextBox { Text = value, MaxLength = max, IsReadOnly = readOnly };

        static CheckBox Check(string label, bool value = false) {
            var check = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 5, 10, 8), VerticalAlignment = VerticalAlignment.Center };
            UseResource(check, Control.ForegroundProperty, "Ink");
            return check;
        }

        internal sealed class Choice
        {
            internal string Key;
            public string Label { get; set; }
        }

        static Choice Option(string key, string label) => new Choice { Key = key, Label = label };

        static ComboBox Picker(IEnumerable<Choice> items, string selected = "", bool editable = false) {
            var picker = new ComboBox { DisplayMemberPath = "Label", IsEditable = editable, IsTextSearchEnabled = true };
            SetOptions(picker, items, selected);
            return picker;
        }

        static string Chosen(ComboBox box) => (box.SelectedItem as Choice)?.Key ?? "";

        static void SetOptions(ComboBox box, IEnumerable<Choice> items, string preferred) {
            var choices = items.ToArray();
            box.ItemsSource = choices;
            box.SelectedItem = choices.FirstOrDefault(item => item.Key == preferred) ?? choices.FirstOrDefault();
        }

        static FrameworkElement Columns(UIElement left, UIElement right, double rightWeight = 1) {
            var columns = new Grid();
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(rightWeight, GridUnitType.Star) });
            Grid.SetColumn(right, 2);
            columns.Children.Add(left);
            columns.Children.Add(right);
            return columns;
        }

        static Grid WithActions(FrameworkElement content, params Button[] buttons) {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0) };
            foreach (var button in buttons) {
                button.Margin = new Thickness(0, 0, 5, 0);
                actions.Children.Add(button);
            }
            buttons[buttons.Length - 1].Margin = new Thickness(0);
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(actions, 1);
            row.Children.Add(content);
            row.Children.Add(actions);
            return row;
        }

        static TabItem Tab(string title, UIElement child) => new TabItem { Header = title, Content = child };
        static string Loader(string key) => LoaderOptions.FirstOrDefault(item => item.Key == key)?.Label ?? key;
        static readonly Choice[] LoaderOptions = { Option("vanilla", "原版"), Option("fabric", "Fabric"),
            Option("neoforge", "NeoForge"), Option("forge", "Forge"), Option("quilt", "Quilt"), Option("minecraft", "原版") };
        static string InstanceVersion(object item) => "Minecraft " + item.S("version") + " · " + Loader(item.S("loader"));
        static string InstanceLabel(object item) => item.S("name") + " · " + InstanceVersion(item);
        static Choice InstanceOption(object item) => Option(item.S("id"), InstanceLabel(item));

        static int Number(string text, int min, int max, string name) {
            if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < min || value > max)
                throw new IOException(name + "须为 " + min + "–" + max + "。");
            return value;
        }

        Window ActiveDialog => Application.Current.Windows.OfType<TaskDialog>().LastOrDefault(window => window.IsVisible && window.IsEnabled) ?? (Window)this;
        bool Confirm(string title, string body) => MessageBox.Show(ActiveDialog, body, title, MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

        sealed class TaskDialog : Window, System.Windows.Forms.IWin32Window
        {
            readonly Launcher host;
            readonly StackPanel body;
            readonly WrapPanel actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            readonly TextBlock error = Note("");
            readonly TaskCompletionSource<bool?> closed = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool? value;
            IntPtr System.Windows.Forms.IWin32Window.Handle => new WindowInteropHelper(this).Handle;

            internal TaskDialog(Launcher host, string title, UIElement content) {
                this.host = host;
                Owner = host.ActiveDialog;
                Title = title;
                FontFamily = host.FontFamily;
                FontSize = host.FontSize;
                ShowInTaskbar = false;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                ResizeMode = ResizeMode.NoResize;
                SizeToContent = SizeToContent.Height;
                Width = Math.Min(610, host.ActualWidth - 30);
                MaxHeight = Math.Max(290, host.ActualHeight - 42);
                Resources.MergedDictionaries.Add(host.Resources);
                UseResource(this, BackgroundProperty, "Canvas");
                UseResource(this, ForegroundProperty, "Ink");
                UseResource(error, TextBlock.ForegroundProperty, "Danger");
                body = Stack(content, error);
                var layout = new DockPanel { Margin = new Thickness(17) };
                DockPanel.SetDock(actions, Dock.Bottom);
                layout.Children.Add(actions);
                layout.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
                Content = layout;
                Closed += (_, __) => closed.TrySetResult(value);
            }

            internal Button Add(string label, Func<Task> action, bool primary = false) {
                var button = new Button { Content = label, Margin = new Thickness(6, 6, 0, 0) };
                if (primary) button.SetResourceReference(StyleProperty, "Primary");
                button.Click += async (_, __) => {
                    button.IsEnabled = false;
                    error.Text = "";
                    try { await action(); }
                    catch (Exception failure) { error.Text = failure.GetBaseException().Message; host.Say(error.Text); }
                    finally { button.IsEnabled = true; }
                };
                actions.Children.Add(button);
                return button;
            }

            internal void Accept() { value = true; Close(); }

            internal Task<bool?> Result(string submit, Func<Task<bool>> validate = null, TextBox confirmation = null, string exact = "",
                bool cancelByDefault = false) {
                var cancel = Add("取消", () => { Close(); return Task.CompletedTask; });
                if (cancelByDefault) {
                    cancel.IsDefault = true;
                    ContentRendered += (_, __) => cancel.Focus();
                    PreviewKeyDown += (_, e) => {
                        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
                    };
                }
                var button = Add(submit, async () => { if (validate == null || await validate()) Accept(); }, true);
                if (confirmation != null) {
                    button.IsEnabled = confirmation.Text == exact;
                    confirmation.TextChanged += (_, __) => button.IsEnabled = confirmation.Text == exact;
                }
                return Open();
            }

            internal Task<bool?> Open() {
                var owner = Owner;
                owner.IsEnabled = false;
                Closed += (_, __) => { owner.IsEnabled = true; if (owner.IsVisible) owner.Activate(); };
                try { Show(); }
                catch { owner.IsEnabled = true; throw; }
                return closed.Task;
            }
        }
    }
}
