using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("EBuild.Verification")]

sealed class Host : Form
{
    const string Entry = "https://ebuild-trial.example/index.html";
    static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
    static readonly Color Blue = Color.FromArgb(49,91,178);
    readonly WebView2 browser = new WebView2 { Dock = DockStyle.Fill };
    readonly Panel bar = new CaptionPanel { Dock = DockStyle.Top, Height = 36, BackColor = Blue };
    readonly string profile;
    CoreWebView2Environment webEnvironment;
    bool closingWebView, closeReady;
    Backend backend;
    Button minimize;
    sealed class CaptionPanel : Panel {
        protected override void WndProc(ref Message m) { if (m.Msg == 0x84) m.Result = new IntPtr(-1); else base.WndProc(ref m); }
    }
    sealed class CaptionLabel : Label {
        protected override void WndProc(ref Message m) { if (m.Msg == 0x84) m.Result = new IntPtr(-1); else base.WndProc(ref m); }
    }
    sealed class CaptionIcon : PictureBox {
        protected override void WndProc(ref Message m) { if (m.Msg == 0x84) m.Result = new IntPtr(-1); else base.WndProc(ref m); }
    }

    [STAThread]
    static int Main(string[] args) {
        if (Array.IndexOf(args, "--check-runtime") >= 0) {
            try { return String.IsNullOrWhiteSpace(CoreWebView2Environment.GetAvailableBrowserVersionString()) ? 1 : 0; }
            catch (WebView2RuntimeNotFoundException) { return 1; }
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // The inbox .NET Framework compiler has legacy TLS defaults.
        System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
        System.Net.ServicePointManager.DefaultConnectionLimit = 32;
        var profile = GetProfile();
        using (var mutex = new System.Threading.Mutex(false,ProfileMutexName(profile))) {
            if (!mutex.WaitOne(0)) return 0;
            try { Application.Run(new Host(profile)); }
            finally { mutex.ReleaseMutex(); }
        }
        return 0;
    }
    static string DefaultProfile() {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EBuild-WebView2-CSharp");
    }
    static string GetProfile() {
        var configured = Environment.GetEnvironmentVariable("EBUILD_CSHARP_PROFILE");
        if (!String.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
#if DEBUG
        return DefaultProfile() + "-Debug";
#else
        return DefaultProfile();
#endif
    }
    static string ProfileMutexName(string profile) {
        const string name = "Local\\EBuild-WebView2-CSharp-Trial-v1";
        var normalized = profile.TrimEnd('\\', '/');
        if (String.Equals(normalized, DefaultProfile(), StringComparison.OrdinalIgnoreCase)) return name;
        using (var sha = System.Security.Cryptography.SHA256.Create())
            return name + "-" + Network.Hex(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(normalized.ToUpperInvariant())));
    }
    Host(string profile) {
        Text = "EBuild 启动器 · C# 试用版";
        Icon = new Icon(Path.Combine(BaseDir,"ui","icon.ico"));
        ClientSize = new Size(938,575);
        MinimumSize = new Size(750,460);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        this.profile = profile;
        Controls.Add(browser);
        Controls.Add(bar);
        var label = new CaptionLabel { Text = "EBuild 启动器", ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill,
            Padding = new Padding(9,0,0,0), Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
        var logo = new CaptionIcon { Dock = DockStyle.Left, Width = 36, SizeMode = PictureBoxSizeMode.CenterImage, Image = new Bitmap(Icon.ToBitmap(),18,18) };
        bar.Controls.Add(label);
        bar.Controls.Add(logo);
        minimize = TitleButton("最小化", "─", () => WindowState = FormWindowState.Minimized);
        var maximize = TitleButton("最大化", "□", ToggleMax);
        var close = TitleButton("关闭", "×", Close);
        close.FlatAppearance.MouseOverBackColor = Color.FromArgb(196,43,28);
        foreach (var item in new Control[] { minimize, maximize, close }) bar.Controls.Add(item);
        Resize += (s,e) => {
            maximize.Text = WindowState == FormWindowState.Maximized ? "❐" : "□";
            maximize.AccessibleName = WindowState == FormWindowState.Maximized ? "还原" : "最大化";
            var inset = WindowState == FormWindowState.Maximized ? 8 : 0;
            Padding = new Padding(inset);
        };
        Shown += async (s,e) => {
            try { await Start(); }
            catch (Exception error) { if (!IsDisposed) { MessageBox.Show(this,error.Message,Text); Close(); } }
        };
    }
    void ToggleMax() { WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; }
    static Button TitleButton(string name,string symbol,Action action) {
        var b = new Button { Text = symbol, AccessibleName = name, FlatStyle = FlatStyle.Flat, Dock = DockStyle.Right, Width = 45,
            ForeColor = Color.White, BackColor = Blue, Font = new Font("Segoe UI", 12F) };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(72,111,190);
        b.Click += (s,e) => action();
        return b;
    }
    protected override void WndProc(ref Message m) {
        // Keep the native resizable window style and replace the default nonclient caption.
        if (m.Msg == 0x83 && m.WParam != IntPtr.Zero) { m.Result = IntPtr.Zero; return; }
        if (m.Msg == 0x84) {
            var raw = m.LParam.ToInt64();
            var p = PointToClient(new Point((short)(raw & 65535),(short)((raw >> 16) & 65535)));
            var x = p.X < 5 ? -1 : p.X >= ClientSize.Width - 5 ? 1 : 0;
            var y = p.Y < 5 ? -1 : p.Y >= ClientSize.Height - 5 ? 1 : 0;
            if (WindowState == FormWindowState.Normal && (x != 0 || y != 0)) {
                m.Result = new IntPtr(y == -1 ? (x == -1 ? 13 : x == 1 ? 14 : 12) : y == 1 ? (x == -1 ? 16 : x == 1 ? 17 : 15) : x == -1 ? 10 : 11); return;
            }
            if (minimize != null && bar.Bounds.Contains(p) && p.X < bar.Left + minimize.Left) {
                m.Result = new IntPtr(2); return;
            }
        }
        base.WndProc(ref m);
    }
    async Task Start() {
        backend = new Backend(this,profile,SendStatus);
        var options = new CoreWebView2EnvironmentOptions("--disk-cache-size=2097152 --media-cache-size=1048576");
        var env = await CoreWebView2Environment.CreateAsync(null,Path.Combine(profile,"WebView2"),options);
        await browser.EnsureCoreWebView2Async(env);
        webEnvironment = env;
        var web = browser.CoreWebView2;
        web.Settings.AreHostObjectsAllowed = false;
        web.Settings.AreDevToolsEnabled = false;
        web.Settings.AreDefaultScriptDialogsEnabled = false;
        web.Settings.AreDefaultContextMenusEnabled = false;
        web.Settings.IsPasswordAutosaveEnabled = false;
        web.Settings.IsGeneralAutofillEnabled = false;
        web.SetVirtualHostNameToFolderMapping("ebuild-trial.example",Path.Combine(BaseDir,"ui"),CoreWebView2HostResourceAccessKind.Deny);
        web.NavigationStarting += (s,e) => { e.Cancel = e.Uri != Entry; };
        web.FrameNavigationStarting += (s,e) => { e.Cancel = true; };
        web.NewWindowRequested += (s,e) => { e.Handled = true; };
        web.DownloadStarting += (s,e) => { e.Cancel = true; };
        web.PermissionRequested += (s,e) => { e.State = CoreWebView2PermissionState.Deny; };
        web.WebMessageReceived += Receive;
        var channels = J.Dump(J.Read(Path.Combine(BaseDir,"channels.json")).Obj());
        var bridge = File.ReadAllText(Path.Combine(BaseDir,"bridge.js")).Replace("/* CHANNELS */ {}",channels);
        await web.AddScriptToExecuteOnDocumentCreatedAsync(bridge);
        web.Navigate(Entry);
    }
    void SendStatus(object message) {
        if (IsDisposed || Disposing || closingWebView) return;
        if (InvokeRequired) {
            if (IsHandleCreated) try { BeginInvoke((MethodInvoker)(() => SendStatus(message))); } catch (InvalidOperationException) {}
            return;
        }
        if (browser.CoreWebView2 != null) browser.CoreWebView2.PostWebMessageAsJson(J.Dump(J.Map("status",message)));
    }
    async void Receive(object sender,CoreWebView2WebMessageReceivedEventArgs e) {
        if (e.Source != Entry || e.WebMessageAsJson.Length > 128000) return;
        object call;
        try { call = J.Parse(e.WebMessageAsJson); }
        catch (ArgumentException) { return; }
        int id = call.I("id");
        if (id <= 0) return;
        object response;
        try {
            var channel = call.S("channel");
            var value = channel == "import:drop" ? await PreviewDroppedImport(e) : await backend.Call(channel,call.A("args"));
            response = J.Map("id",id,"value",value);
        }
        catch (Exception error) { response = J.Map("id",id,"error",error.Message); }
        if (!IsDisposed && !Disposing && !closingWebView) browser.CoreWebView2.PostWebMessageAsJson(J.Dump(response));
    }
    Task<object> PreviewDroppedImport(CoreWebView2WebMessageReceivedEventArgs message) {
        var item = message.AdditionalObjects.SingleOrDefault();
        var file = item as FileInfo;
        var handle = item as CoreWebView2FileSystemHandle;
        var nativeFile = item as CoreWebView2File;
        var path = file != null ? file.FullName : handle != null ? handle.Path : nativeFile != null ? nativeFile.Path : "";
        if (path == "") throw new IOException("未能读取拖入路径，请从「导入」中选择文件夹或整合包。");
        return backend.PreviewDroppedImport(path);
    }
    static long WebCacheSize(string path) {
        long total = 0;
        foreach (var item in Directory.EnumerateFileSystemEntries(path)) {
            var attributes = File.GetAttributes(item);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("WebView2 数据目录包含链接。");
            total += (attributes & FileAttributes.Directory) == 0 ? new FileInfo(item).Length : WebCacheSize(item);
        }
        return total;
    }
    async void CloseWebView() {
        const long webCacheBudget = 48000000;
        try {
            var path = J.Inside(profile, "WebView2");
            // Only this launcher-owned WebView2 folder is disposable. Accounts and settings live alongside it.
            if (!String.Equals(Path.GetFullPath(webEnvironment.UserDataFolder), path, StringComparison.OrdinalIgnoreCase)) return;
            var size = await Task.Run(() => { J.NoLinks(path); return WebCacheSize(path); });
            if (size <= webCacheBudget) return;
            var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<CoreWebView2BrowserProcessExitedEventArgs> exited = (s,e) => released.TrySetResult(true);
            webEnvironment.BrowserProcessExited += exited;
            try {
                browser.Dispose();
                if (await Task.WhenAny(released.Task, Task.Delay(3000)) != released.Task) return;
                await Task.Run(() => { J.NoLinks(path); WebCacheSize(path); Directory.Delete(path, true); });
            } finally { webEnvironment.BrowserProcessExited -= exited; }
        } catch (Exception error) { System.Diagnostics.Debug.WriteLine(error.Message); }
        finally {
            closeReady = true;
            if (!IsDisposed) Close();
        }
    }
    protected override void OnFormClosing(FormClosingEventArgs e) {
        if (!closeReady && e.CloseReason == CloseReason.UserClosing && backend != null && (backend.HasGameWork || backend.HasLanWork) && !backend.Leaving) {
            e.Cancel = true;
            SendStatus(J.Map("confirmGameExit", true, "lan", backend.HasLanWork, "gamesRunning", backend.HasGameWork));
        } else if (backend != null && !backend.CanClose()) e.Cancel = true;
        else if (!closeReady && e.CloseReason == CloseReason.UserClosing && webEnvironment != null) {
            e.Cancel = true;
            if (!closingWebView) {
                closingWebView = true;
                Hide();
                BeginInvoke((MethodInvoker)CloseWebView);
            }
        }
        base.OnFormClosing(e);
    }
}
