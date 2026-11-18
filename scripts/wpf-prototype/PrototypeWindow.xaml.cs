using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

public partial class PrototypeWindow : Window, LauncherWindow
{
    sealed class InstanceEntry
    {
        internal object Spec;
        public string Name { get { return Spec.S("name"); } }
        public string Details {
            get {
                var loader = Spec.S("loader");
                var label = loader == "vanilla" ? "原版" :
                    loader == "neoforge" ? "NeoForge" : Char.ToUpperInvariant(loader[0]) + loader.Substring(1);
                return "Minecraft " + Spec.S("version") + " · " + label;
            }
        }
    }

    readonly string profile;
    readonly string sourceRoot;
    Backend backend;
    object[] games = new object[0];
    bool closing;

    [STAThread]
    static void Main() {
        System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
        System.Net.ServicePointManager.DefaultConnectionLimit = 32;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var original = Path.Combine(local, "EBuild-WebView2-CSharp");
        var options = J.Read(Path.Combine(original, "settings.json"));
        var sourceRoot = Path.GetFullPath(options.S("dataRoot") == "" ? Path.Combine(original, "游戏数据") : options.S("dataRoot"));
        var testProfile = Environment.GetEnvironmentVariable("EBUILD_WPF_TEST_PROFILE");
        var profile = Path.GetFullPath(String.IsNullOrWhiteSpace(testProfile) ? Path.Combine(local, "EBuild-WPF-Prototype") : testProfile);
        if (ExternalInstances.ContainsPath(sourceRoot, profile) || ExternalInstances.ContainsPath(profile, sourceRoot) ||
            ExternalInstances.ContainsPath(original, profile) || ExternalInstances.ContainsPath(profile, original)) {
            MessageBox.Show("测试目录须独立于现有 EBuild 数据。", "EBuild WPF");
            return;
        }
        using (var sha = System.Security.Cryptography.SHA256.Create())
        using (var mutex = new Mutex(false, "Local\\EBuild-WPF-" +
            Network.Hex(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(profile.TrimEnd('\\').ToUpperInvariant()))))) {
            try { if (!mutex.WaitOne(0)) return; }
            catch (AbandonedMutexException) { }
            try { new Application().Run(new PrototypeWindow(profile, sourceRoot, options.S("selected"))); }
            finally { mutex.ReleaseMutex(); }
        }
    }

    readonly string lastSelected;

    PrototypeWindow(string profile, string sourceRoot, string lastSelected) {
        this.profile = profile;
        this.sourceRoot = sourceRoot;
        this.lastSelected = lastSelected;
        InitializeComponent();
    }

    IntPtr System.Windows.Forms.IWin32Window.Handle { get { return new WindowInteropHelper(this).Handle; } }
    void LauncherWindow.Minimize() { WindowState = WindowState.Minimized; }
    void LauncherWindow.Post(Action action) {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(action);
    }

    async void WindowLoaded(object sender, RoutedEventArgs e) {
        try {
            // This configuration belongs only to the prototype. The source instance data is read-only.
            var testSettings = J.Inside(profile, "settings.json");
            J.NoLinks(testSettings);
            var settings = J.Read(testSettings);
            if (settings.S("dataRoot") != "" || settings.S("accountMode") == "microsoft")
                throw new IOException("测试目录包含不兼容的配置，请使用空的测试目录。");
            backend = new Backend(this, profile, UpdateStatus);
            var entries = await Task.Run(() => ReadSourceInstances(sourceRoot));
            InstanceList.ItemsSource = entries;
            InstanceList.SelectedItem = entries.FirstOrDefault(item => item.Spec.S("id") == lastSelected) ?? entries.FirstOrDefault();
            var options = await backend.Call("settings:get", new object[0]);
            MemoryBox.Text = options.S("memory");
            AfterLaunch.SelectedIndex = options.S("afterLaunch") == "minimize" ? 1 : 0;
            UpdateStatus(J.Map("games", await backend.Call("game:list", new object[0])));
            StatusText.Text = entries.Length == 0 ? "原版 EBuild 暂无已安装实例。" : "就绪 · 测试副本只沿用游戏和加载器版本";
            UpdateCard();
        } catch (Exception error) { StatusText.Text = error.Message; }
    }

    static InstanceEntry[] ReadSourceInstances(string root) {
        var directory = J.Inside(root, "instances");
        if (!Directory.Exists(directory)) return new InstanceEntry[0];
        J.NoLinks(directory);
        return Directory.EnumerateDirectories(directory)
            .Where(path => !Path.GetFileName(path).StartsWith(".") && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            .Select(path => new { Id = Path.GetFileName(path), Manifest = Path.Combine(path, ".ebuild.json") })
            .Where(record => File.Exists(record.Manifest) && (File.GetAttributes(record.Manifest) & FileAttributes.ReparsePoint) == 0)
            .Select(record => J.Merge(J.Read(record.Manifest), J.Map("id", record.Id)))
            .Where(spec => spec.S("name") != "" && spec.S("version") != "" && Minecraft.SupportsLoader(spec.S("loader")))
            .Select(spec => new InstanceEntry { Spec = spec })
            .OrderBy(item => item.Name).ToArray();
    }

    InstanceEntry Selected { get { return InstanceList.SelectedItem as InstanceEntry; } }

    void InstanceChanged(object sender, SelectionChangedEventArgs e) { UpdateCard(); }

    void UpdateCard() {
        var item = Selected;
        InstanceTitle.Text = item == null ? "还没有游戏实例" : item.Name;
        InstanceVersion.Text = item == null ? "" : item.Details;
        var state = item == null ? "" : games.FirstOrDefault(game => game.S("id") == item.Spec.S("id")).S("state");
        LaunchButton.IsEnabled = item != null && backend != null && state == "" && !closing;
        LaunchButton.Content = state == "running" ? "游戏运行中" : state == "stopping" ? "正在停止…" :
            state == "queued" ? "等待启动…" : state == "preparing" ? "正在准备…" : "启动测试副本";
        StopButton.Visibility = state == "running" ? Visibility.Visible : Visibility.Collapsed;
    }

    void SeedTestInstance(InstanceEntry instance) {
        var spec = instance.Spec;
        var id = J.Id(spec.S("id"));
        var version = J.Id(spec.S("version"));
        var directory = J.Inside(backend.Root, "instances/" + id);
        var manifest = Path.Combine(directory, ".ebuild.json");
        J.NoLinks(manifest);
        if (!File.Exists(manifest)) {
            J.Save(manifest, J.Map("name", spec.S("name"), "version", version, "loader", spec.S("loader"),
                "loaderVersion", spec.S("loaderVersion")));
        }
        var relativeVersion = "game/versions/" + version + "/" + version + ".json";
        var source = J.Inside(sourceRoot, relativeVersion);
        var target = J.Inside(backend.Root, relativeVersion);
        if (File.Exists(source) && !File.Exists(target)) {
            J.NoLinks(source);
            J.NoLinks(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(source, target);
        }
    }

    async void LaunchClicked(object sender, RoutedEventArgs e) {
        var item = Selected;
        if (item == null || closing) return;
        try {
            var version = J.Id(item.Spec.S("version"));
            var client = J.Inside(backend.Root, "game/versions/" + version + "/" + version + ".jar");
            if (!File.Exists(client) &&
                MessageBox.Show(this, "这个版本需要在测试目录中下载游戏文件。是否继续？", Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            SeedTestInstance(item);
            await backend.Call("game:start", new object[] { item.Spec.S("id") });
        } catch (Exception error) { StatusText.Text = error.Message; }
    }

    async void StopClicked(object sender, RoutedEventArgs e) {
        var item = Selected;
        if (item == null) return;
        try {
            var result = await backend.Call("game:stop", new object[] { item.Spec.S("id") });
            var identity = result.S("forceIdentity");
            if (identity != "" && MessageBox.Show(this, "游戏未响应关闭请求。是否强制结束测试游戏？",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                await backend.Call("game:stop", new object[] { item.Spec.S("id"), identity });
        } catch (Exception error) { StatusText.Text = error.Message; }
    }

    void UpdateStatus(object update) {
        if (!Dispatcher.CheckAccess()) {
            ((LauncherWindow)this).Post(() => UpdateStatus(update));
            return;
        }
        if (update.At("games") != null) {
            games = update.A("games");
            UpdateCard();
        }
        if (closing) return;
        if (update.S("message") != "") StatusText.Text = update.S("message");
        if (update.S("gameExit") != "") StatusText.Text = "测试游戏已退出（代码 " + update.S("code") + "）";
        if (update.S("launchJava") != "") {
            StatusText.Text = "需要 Java " + update.S("major");
            Dispatcher.BeginInvoke(new Action(() => ConfirmJava(update.S("launchJava"), update.I("major"))));
        }
    }

    async void ConfirmJava(string id, int major) {
        if (closing) return;
        if (MessageBox.Show(this, "这个实例需要 Java " + major + "。是否下载到测试目录？", Title,
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { await backend.Call("game:start", new object[] { id, major }); }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    async void SaveClicked(object sender, RoutedEventArgs e) {
        int memory;
        if (!Int32.TryParse(MemoryBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out memory) ||
            memory < 1024 || memory > 65536) {
            StatusText.Text = "请输入 1024–65536 MB 的内存。";
            return;
        }
        try {
            await backend.Call("settings:save", new object[] { J.Map("memory", memory,
                "afterLaunch", AfterLaunch.SelectedIndex == 1 ? "minimize" : "keep") });
            StatusText.Text = "测试设置已保存";
        } catch (Exception error) { StatusText.Text = error.Message; }
    }

    void HomeClicked(object sender, RoutedEventArgs e) { SwitchPage(HomePage, SettingsPage, -1); }
    void SettingsClicked(object sender, RoutedEventArgs e) { SwitchPage(SettingsPage, HomePage, 1); }

    static void SwitchPage(FrameworkElement next, FrameworkElement old, int direction) {
        if (next.Visibility == Visibility.Visible) return;
        old.Visibility = Visibility.Collapsed;
        next.Visibility = Visibility.Visible;
        if (!SystemParameters.ClientAreaAnimation) return;
        var transform = new TranslateTransform();
        next.RenderTransform = transform;
        var time = TimeSpan.FromMilliseconds(180);
        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(18 * direction, 0, time) {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        next.BeginAnimation(OpacityProperty, new DoubleAnimation(0.6, 1, time));
    }

    protected override void OnClosing(CancelEventArgs e) {
        base.OnClosing(e);
        if (e.Cancel || backend == null) return;
        if (backend.Leaving || !backend.HasGameWork) {
            e.Cancel = !backend.CanClose();
            return;
        }
        e.Cancel = true;
        if (!closing && MessageBox.Show(this, "退出测试版？正在运行的测试游戏会保留。", Title,
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) {
            closing = true;
            CloseWithGame();
        }
    }

    async void CloseWithGame() {
        try { await backend.Call("game:keep-and-exit", new object[0]); }
        catch (Exception error) { closing = false; StatusText.Text = error.Message; }
    }
}
