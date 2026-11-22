using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

static class UpdateProbe
{
    internal static int Run(string[] args) {
        try {
            if (args.Length == 6 && args[0] == "--stage-update")
                Stage(args[1], args[2], args[3], args[4], args[5]).GetAwaiter().GetResult();
            else if (args.Length == 2 && args[0] == "--confirm-update")
                Task.Run(() => Confirm(Int32.Parse(args[1]))).GetAwaiter().GetResult();
            else throw new ArgumentException("Invalid update verification arguments.");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static async Task Stage(string launcher, string profile, string archive, string tag, string expected) {
        var assembly = Assembly.LoadFrom(launcher);
        var networkType = assembly.GetType("Network", true);
        var updateType = assembly.GetType("EBuild.Native.LauncherUpdate", true);
        const BindingFlags methods = BindingFlags.Static | BindingFlags.NonPublic;
        var bytes = File.ReadAllBytes(archive);
        string hash;
        using (var sha = SHA256.Create()) hash = expected == "checksum" ? new String('0', 64) :
            BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        using (var transport = new ReleaseFeed(tag, hash, bytes))
        using (var network = (IDisposable)networkType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
            .Invoke(new object[] { new Dictionary<string,object>(), null, transport })) {
            var check = updateType.GetMethod("Check", methods);
            var release = (Dictionary<string,object>)await ((Task<object>)check.Invoke(null, new object[] { network, "", CancellationToken.None }));
            if ((string)release["tag"] != tag || (string)release["sha256"] != hash)
                throw new InvalidOperationException("Release entry did not contain the expected version and hash.");
            var muted = await ((Task<object>)check.Invoke(null, new object[] { network, tag, CancellationToken.None }));
            if (muted != null) throw new InvalidOperationException("An automatic check should suppress a previously failed release.");

            var pending = Path.Combine(profile, "launcher-updates", "pending.json");
            try {
                var id = await ((Task<string>)updateType.GetMethod("Stage", methods)
                    .Invoke(null, new[] { profile, (object)network, release, CancellationToken.None }));
                if (expected != "accept" || !File.Exists(Path.Combine(profile, "launcher-updates", id, "new", "EBuild-WPF.exe")))
                    throw new InvalidOperationException("Unexpected staging result.");
                Console.WriteLine("PASS: checked and staged " + tag + ".");
            } catch (IOException error) {
                var message = expected == "checksum" ? "文件校验失败" : expected == "version" ? "版本与发布版本不匹配" : "";
                if (message == "" || !error.Message.Contains(message) || File.Exists(pending)) throw;
                Console.WriteLine("PASS: rejected " + expected + " before creating a pending update.");
            }
        }
    }

    static void Confirm(int processId) {
        using (var process = Process.GetProcessById(processId)) {
            var apply = new AndCondition(
                new PropertyCondition(AutomationElement.NameProperty, "重启并安装"),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            var dialogName = new PropertyCondition(AutomationElement.NameProperty, "安装 EBuild 更新");
            AutomationElement Window() {
                process.Refresh();
                return process.MainWindowHandle == IntPtr.Zero ? null : AutomationElement.FromHandle(process.MainWindowHandle);
            }
            Invoke(WaitForControl(() => Window()?.FindFirst(TreeScope.Descendants, apply), "apply button"));
            Invoke(WaitForControl(() => Window()?.FindFirst(TreeScope.Descendants, dialogName)?.FindFirst(TreeScope.Descendants, apply),
                "confirmation button"));
            Console.WriteLine("PASS: confirmed the update through the running WPF launcher.");
        }
    }

    static void Invoke(AutomationElement item) => ((InvokePattern)item.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    static AutomationElement WaitForControl(Func<AutomationElement> find, string label) {
        var timer = Stopwatch.StartNew();
        do {
            try { var item = find(); if (item != null && item.Current.IsEnabled) return item; }
            catch (ElementNotAvailableException) { }
            Thread.Sleep(100);
        } while (timer.Elapsed < TimeSpan.FromSeconds(15));
        throw new TimeoutException("Update UI did not show the " + label + ".");
    }

    sealed class ReleaseFeed : HttpMessageHandler
    {
        const string Repository = "https://github.com/eee22323232/EBuild";
        readonly string tag, hash, asset;
        readonly byte[] package;

        internal ReleaseFeed(string tag, string hash, byte[] package) {
            this.tag = tag;
            this.hash = hash;
            this.package = package;
            asset = "EBuild-" + tag + "-WPF-Windows-x64.zip";
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            string url = request.RequestUri.AbsoluteUri;
            string assets = Repository + "/releases/download/" + tag + "/";
            HttpContent content;
            if (url == Repository + "/releases.atom")
                content = new StringContent("<feed xmlns='http://www.w3.org/2005/Atom'><entry><link href='" + Repository +
                    "/releases/tag/" + tag + "'/><content>Update verification</content></entry></feed>");
            else if (url == assets + "WPF-SHA256SUMS.txt") content = new StringContent(hash + "  " + asset + "\n");
            else if (url == assets + asset) content = new ByteArrayContent(package);
            else throw new InvalidOperationException("Unexpected fixture request: " + url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content });
        }
    }
}
