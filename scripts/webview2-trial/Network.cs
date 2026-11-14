using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

sealed class Network : IDisposable
{
    readonly HttpClient http;
    internal Network(object settings) {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate, UseCookies = false,
            UseProxy = settings.S("proxyMode") != "direct" };
        if (settings.S("proxyMode") == "manual") handler.Proxy = new WebProxy(settings.S("proxy"));
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EBuildLauncher/0.1.1 (https://github.com/eee22323232/EBuild)");
    }
    public void Dispose() { http.Dispose(); }
    internal static Uri Https(string value) {
        Uri uri;
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0)
            throw new InvalidOperationException("下载地址必须是 HTTPS。");
        return uri;
    }
    internal async Task<Tuple<int,object>> Request(string url, CancellationToken ct, HttpContent data = null, string token = "") {
        using (var message = new HttpRequestMessage(data == null ? HttpMethod.Get : HttpMethod.Post, Https(url))) {
            message.Content = data;
            if (token.Length != 0) message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using (var response = await http.SendAsync(message, ct).ConfigureAwait(false)) {
                var status = (int)response.StatusCode;
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                object parsed;
                try { parsed = J.Parse(body); } catch (ArgumentException) { parsed = J.Map(); }
                return Tuple.Create(status, parsed);
            }
        }
    }
    internal async Task<string> Text(string url, CancellationToken ct) {
        using (var result = await http.GetAsync(Https(url), ct).ConfigureAwait(false)) {
            if (!result.IsSuccessStatusCode) throw new IOException("请求失败（HTTP " + (int)result.StatusCode + "）：" + result.RequestMessage.RequestUri.Host);
            return await result.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
    }
    internal async Task<object> Json(string url, CancellationToken ct) { return J.Parse(await Text(url, ct).ConfigureAwait(false)); }
    internal static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
    internal static bool Check(string path, object spec) {
        if (!File.Exists(path)) return false;
        var size = spec.At("size");
        if (size != null && new FileInfo(path).Length != Convert.ToInt64(size)) return false;
        foreach (var key in new[] { "sha512", "sha256", "sha1" }) {
            var expected = spec.S(key);
            if (expected.Length == 0) continue;
            using (HashAlgorithm hash = key == "sha512" ? (HashAlgorithm)SHA512.Create() : key == "sha256" ? (HashAlgorithm)SHA256.Create() : SHA1.Create())
            using (var file = File.OpenRead(path)) if (!String.Equals(Hex(hash.ComputeHash(file)), expected, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return new FileInfo(path).Length > 0;
    }
    internal async Task FileTo(object spec, string path, CancellationToken ct, string mirror = "") {
        ct.ThrowIfCancellationRequested();
        J.NoLinks(path);
        if (Check(path, spec)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".part";
        var target = spec.S("url");
        var urls = mirror != "" && mirror != target ? new[] { mirror, target, target } : new[] { target, target };
        try {
            for (int n = 0; n < urls.Length; n++) {
                try {
                    using (var response = await http.GetAsync(Https(urls[n]), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false)) {
                        response.EnsureSuccessStatusCode();
                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true)) {
                            var buffer = new byte[65536];
                            long bytes = 0, maxBytes = spec.At("size") == null ? 2L * 1024 * 1024 * 1024 : Convert.ToInt64(spec.At("size"));
                            int count;
                            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                                while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) > 0) {
                                    timeout.CancelAfter(TimeSpan.FromSeconds(60));
                                    bytes += count;
                                    if (bytes > maxBytes) throw new IOException("下载文件大于预期。");
                                    await output.WriteAsync(buffer, 0, count, timeout.Token).ConfigureAwait(false);
                                }
                            }
                        }
                    }
                    if (!Check(temp, spec)) throw new IOException("文件校验失败：" + Path.GetFileName(path));
                    ct.ThrowIfCancellationRequested();
                    if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
                    return;
                } catch (Exception error) {
                    ct.ThrowIfCancellationRequested();
                    if (n + 1 == urls.Length || (!(error is IOException) && !(error is HttpRequestException) && !(error is OperationCanceledException))) throw;
                }
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        } finally { File.Delete(temp); }
    }
    internal static void Unzip(string file, string dest, CancellationToken ct, Func<string,string> map = null) {
        long total = 0;
        using (var zip = ZipFile.OpenRead(file)) foreach (var entry in zip.Entries) {
            ct.ThrowIfCancellationRequested();
            var name = map == null ? entry.FullName : map(entry.FullName);
            if (String.IsNullOrEmpty(name) || String.IsNullOrEmpty(entry.Name)) continue;
            total += entry.Length;
            if (total > 4L * 1024 * 1024 * 1024) throw new IOException("压缩包解压体积超过限制。");
            var output = J.Inside(dest, name);
            J.NoLinks(output);
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            entry.ExtractToFile(output, true);
        }
    }
    internal static async Task Pool<T>(IEnumerable<T> input, int count, Func<T,Task> handle, CancellationToken ct) {
        var queue = new System.Collections.Concurrent.ConcurrentQueue<T>(input);
        var workers = Enumerable.Range(0, Math.Max(1, Math.Min(32,count))).Select(async i => {
            T item;
            while (queue.TryDequeue(out item)) { ct.ThrowIfCancellationRequested(); await handle(item).ConfigureAwait(false); }
        });
        await Task.WhenAll(workers).ConfigureAwait(false);
    }
}
