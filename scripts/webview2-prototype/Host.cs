// Disposable WebView2 host for EBuild's size and setup experiment.
using System;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

sealed class Host : Form
{
    const string Entry = "https://ebuild-prototype.example/index.html";
    static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
    readonly WebView2 browser = new WebView2 { Dock = DockStyle.Fill };
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    readonly string profile;
    Process backend;

    [STAThread]
    static int Main(string[] args)
    {
        if (Array.IndexOf(args, "--check-runtime") >= 0) {
            try { return String.IsNullOrWhiteSpace(CoreWebView2Environment.GetAvailableBrowserVersionString()) ? 1 : 0; }
            catch (WebView2RuntimeNotFoundException) { return 1; }
        }
        return Run();
    }

    static int Run()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new Host());
        return 0;
    }

    Host()
    {
        Text = "EBuild · WebView2 实验版";
        Icon = new Icon(Path.Combine(BaseDir, "src", "ui", "icon.ico"));
        ClientSize = new Size(938, 575);
        MinimumSize = new Size(750, 460);
        StartPosition = FormStartPosition.CenterScreen;
        profile = Environment.GetEnvironmentVariable("EBUILD_PROTOTYPE_PROFILE") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EBuild-WebView2-Prototype");
        Controls.Add(browser);
        Shown += async delegate {
            try { await Start(); }
            catch (Exception error) {
                if (IsDisposed) return;
                MessageBox.Show(this, error.Message, Text);
                Close();
            }
        };
    }

    async Task Start()
    {
        Directory.CreateDirectory(profile);
        StartBackend();
        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(profile, "WebView2"));
        await browser.EnsureCoreWebView2Async(env);
        var web = browser.CoreWebView2;
        web.Settings.AreHostObjectsAllowed = false;
        web.Settings.AreDevToolsEnabled = false;
        web.Settings.AreDefaultScriptDialogsEnabled = false;
        web.Settings.AreDefaultContextMenusEnabled = false;
        web.SetVirtualHostNameToFolderMapping("ebuild-prototype.example",
            Path.Combine(BaseDir, "src", "ui"), CoreWebView2HostResourceAccessKind.Deny);
        web.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e) {
            e.Cancel = e.Uri != Entry;
        };
        web.FrameNavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e) { e.Cancel = true; };
        web.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e) { e.Handled = true; };
        web.WebMessageReceived += Receive;
        await web.AddScriptToExecuteOnDocumentCreatedAsync(File.ReadAllText(Path.Combine(BaseDir, "bridge.js")));
        web.Navigate(Entry);
    }

    void StartBackend()
    {
        var start = new ProcessStartInfo(Path.Combine(BaseDir, "node.exe"), "backend.cjs") {
            WorkingDirectory = BaseDir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8,
        };
        start.EnvironmentVariables["EBUILD_PROTOTYPE_PROFILE"] = profile;
        start.EnvironmentVariables.Remove("NODE_OPTIONS");
        start.EnvironmentVariables.Remove("NODE_PATH");
        backend = new Process { StartInfo = start, EnableRaisingEvents = true };
        backend.Exited += delegate {
            if (!IsDisposed && IsHandleCreated) BeginInvoke((MethodInvoker)delegate {
                if (!IsDisposed && browser.CoreWebView2 != null)
                    browser.CoreWebView2.PostWebMessageAsJson("{\"backendExited\":true}");
            });
        };
        backend.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) {
            if (e.Data != null && !IsDisposed && IsHandleCreated) BeginInvoke((MethodInvoker)delegate {
                if (!IsDisposed && browser.CoreWebView2 != null)
                    browser.CoreWebView2.PostWebMessageAsJson(e.Data);
            });
        };
        backend.Start();
        backend.BeginOutputReadLine();
    }

    sealed class Request {
        public int id { get; set; }
        public string channel { get; set; }
        public object[] args { get; set; }
    }

    void Receive(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (e.Source != Entry) return;
        Request request;
        try { request = json.Deserialize<Request>(e.WebMessageAsJson); }
        catch (Exception) { return; }
        if (request == null || request.id <= 0 || request.args == null) return;
        if (request.channel == "data:choose") {
            using (var dialog = new FolderBrowserDialog { Description = "选择游戏数据的位置（将创建独立实验子目录）",
                ShowNewFolderButton = true }) {
                var selected = dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
                browser.CoreWebView2.PostWebMessageAsJson(json.Serialize(new { id = request.id, value = selected }));
            }
            return;
        }
        if (backend.HasExited) {
            browser.CoreWebView2.PostWebMessageAsJson(json.Serialize(new { id = request.id, error = "原型的 Node 后端已退出。" }));
            return;
        }
        var bytes = new UTF8Encoding(false).GetBytes(e.WebMessageAsJson + "\n");
        try {
            backend.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
            backend.StandardInput.BaseStream.Flush();
        } catch (IOException) {
            browser.CoreWebView2.PostWebMessageAsJson(json.Serialize(new { id = request.id, error = "原型的 Node 后端已退出。" }));
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        if (backend == null) return;
        try { if (!backend.HasExited) backend.Kill(); }
        catch (InvalidOperationException) {}
        backend.Dispose();
    }
}
