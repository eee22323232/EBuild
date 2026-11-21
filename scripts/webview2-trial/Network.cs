using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

sealed class Network : IDisposable
{
    readonly HttpClient http;
    readonly string transfers;
    readonly int concurrency;
    readonly TransferScheduler scheduler;
    static readonly SemaphoreSlim[] fileLocks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    internal Network(object settings, string transferRoot = null, HttpMessageHandler transport = null) {
        transfers = transferRoot;
        concurrency = Math.Max(1, Math.Min(32, settings.I("concurrency", 12)));
        scheduler = new TransferScheduler(concurrency);
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate, UseCookies = false,
            UseProxy = settings.S("proxyMode") != "direct", MaxConnectionsPerServer = concurrency };
        if (settings.S("proxyMode") == "manual") handler.Proxy = new WebProxy(settings.S("proxy"));
        http = new HttpClient(transport ?? handler) { Timeout = TimeSpan.FromSeconds(60) };
        if (transport != null) handler.Dispose();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EBuildLauncher/0.1.2 (https://github.com/eee22323232/EBuild)");
    }
    public void Dispose() { http.Dispose(); }
    internal static Uri Https(string value) {
        Uri uri;
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0)
            throw new InvalidOperationException("下载地址必须是 HTTPS。");
        return uri;
    }
    internal async Task<Tuple<int,object>> Request(string url, CancellationToken ct, HttpContent data = null,
        string token = "", HttpMethod method = null) {
        using (var message = new HttpRequestMessage(method ?? (data == null ? HttpMethod.Get : HttpMethod.Post), Https(url))) {
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
    internal async Task<string> Text(string url, CancellationToken ct, int timeoutSeconds = 60) {
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
            deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            using (var result = await http.GetAsync(Https(url), deadline.Token).ConfigureAwait(false)) {
                if (!result.IsSuccessStatusCode) throw new IOException("请求失败（HTTP " + (int)result.StatusCode + "）：" + result.RequestMessage.RequestUri.Host);
                return await result.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }
    }
    internal async Task<object> Json(string url, CancellationToken ct, int timeoutSeconds = 60) {
        return J.Parse(await Text(url, ct, timeoutSeconds).ConfigureAwait(false));
    }
    internal object TransferStatus() { return scheduler.Snapshot(); }
    internal void SourceChanged(string from, string to, string reason) {
        scheduler.Switched(scheduler.For(Https(from)), scheduler.For(Https(to)), reason);
    }
    internal static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
    internal static bool Check(string path, object spec) { return Check(new FileInfo(path), spec); }

    // .mrpack fileSize is a progress hint. Only a SHA-512-pinned download may use it this way.
    static bool SizeIsHint(object spec) { return spec.B("sizeHint") && Regex.IsMatch(spec.S("sha512"), "^[a-fA-F0-9]{128}$"); }

    static bool Check(FileInfo file, object spec) {
        if (!file.Exists || file.Length == 0) return false;
        var size = spec.At("size");
        if (size != null && file.Length != Convert.ToInt64(size) && !SizeIsHint(spec)) return false;
        foreach (var key in new[] { "sha512", "sha256", "sha1" }) {
            var expected = spec.S(key);
            if (expected.Length == 0) continue;
            using (HashAlgorithm hash = key == "sha512" ? (HashAlgorithm)SHA512.Create() : key == "sha256" ? (HashAlgorithm)SHA256.Create() : SHA1.Create())
            using (var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
                if (!String.Equals(Hex(hash.ComputeHash(input)), expected, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
    internal async Task FileTo(object spec, string path, CancellationToken ct, string mirror = "", LaunchChecks checks = null,
            long? maxDownloadBytes = null, IEnumerable<Uri> alternates = null) {
        var identity = String.Join(":", new[] { spec.S("sha512"), spec.S("sha256"), spec.S("sha1"), spec.S("size") });
        var resumable = new[] { "sha512", "sha256", "sha1" }.Any(algorithm => System.Text.RegularExpressions.Regex.IsMatch(spec.S(algorithm),
            "^[a-fA-F0-9]{" + (algorithm == "sha512" ? 128 : algorithm == "sha256" ? 64 : 40) + "}$"));
        string key;
        using (var hash = SHA256.Create()) key = Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(resumable ? identity : Path.GetFullPath(path).ToUpperInvariant())));
        var gate = fileLocks[Convert.ToInt32(key.Substring(0, 2), 16) % fileLocks.Length];
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { await FileToCore(spec, path, ct, mirror, checks, resumable, key, maxDownloadBytes, alternates).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    async Task FileToCore(object spec, string path, CancellationToken ct, string mirror, LaunchChecks checks, bool resumable, string key,
            long? maxDownloadBytes, IEnumerable<Uri> alternates) {
        ct.ThrowIfCancellationRequested();
        var file = new FileInfo(path);
        var stamp = checks == null ? null : LaunchChecks.Stamp(file);
        if (checks != null && checks.Matches(path, spec, stamp)) return;
        if (stamp != null) checks.CheckDirectory(path);
        else J.NoLinks(path);
        if (Check(file, spec)) {
            if (checks != null) checks.Remember(path, spec, stamp);
            return;
        }
        if (spec.S("url") == "") throw new IOException("加载器生成的文件缺失或损坏：" + file.Name);
        // Re-check the complete path before replacing a file after a cache miss.
        if (stamp != null) J.NoLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var temp = resumable && transfers != null ? J.Inside(transfers, key + ".part") : path + "." + Guid.NewGuid().ToString("N") + ".part";
        var keep = resumable && transfers != null;
        J.NoLinks(temp);
        Directory.CreateDirectory(Path.GetDirectoryName(temp));
        var target = spec.S("url");
        var urls = (mirror == "" ? new[] { Https(target) } : new[] { Https(mirror), Https(target) })
            .Concat(alternates ?? Enumerable.Empty<Uri>()).Distinct().ToArray();
        var sizeIsHint = SizeIsHint(spec);
        long? declared = spec.At("size") == null ? (long?)null : Convert.ToInt64(spec.At("size"));
        long? exact = sizeIsHint ? null : declared;
        long maxBytes = declared ?? 2L * 1024 * 1024 * 1024;
        if (sizeIsHint && declared.HasValue) maxBytes += Math.Min(1024 * 1024, long.MaxValue - maxBytes);
        if (maxDownloadBytes.HasValue) maxBytes = Math.Min(maxBytes, maxDownloadBytes.Value);
        try {
            if (keep && Check(temp, spec)) {
                await CommitDownload(temp, path, ct).ConfigureAwait(false);
                if (checks != null) checks.Remember(path, spec, LaunchChecks.Stamp(path));
                return;
            }
            TransferScheduler.Source previous = null;
            string reason = "";
            for (int n = 0; n <= urls.Length; n++) {
                var url = urls[Math.Min(n, urls.Length - 1)];
                var source = scheduler.For(url);
                bool hasAlternate = n < urls.Length - 1;
                var unavailable = await scheduler.Enter(source, hasAlternate, ct).ConfigureAwait(false);
                if (unavailable.Length != 0) {
                    previous = source;
                    reason = unavailable;
                    continue;
                }
                if (previous != null) scheduler.Switched(previous, source, reason);
                try {
                    await Transfer(url, source, temp, keep, exact, maxBytes, sizeIsHint, hasAlternate, ct).ConfigureAwait(false);
                    if (!Check(temp, spec)) { File.Delete(temp); throw new IOException("文件校验失败：" + Path.GetFileName(path)); }
                    await CommitDownload(temp, path, ct).ConfigureAwait(false);
                    if (checks != null) checks.Remember(path, spec, LaunchChecks.Stamp(path));
                    return;
                } catch (Exception error) {
                    ct.ThrowIfCancellationRequested();
                    if (n == urls.Length || (!(error is IOException) && !(error is HttpRequestException))) throw;
                    previous = source;
                    reason = error is HttpRequestException ? "下载连接中断" : error.Message;
                }
                if (!hasAlternate) await Task.Delay(200, ct).ConfigureAwait(false);
            }
        } finally { if (!keep) File.Delete(temp); }
    }

    async Task Transfer(Uri url, TransferScheduler.Source source, string temp, bool keep, long? exact, long maxBytes,
        bool sizeIsHint, bool hasAlternate, CancellationToken ct) {
        var elapsed = Stopwatch.StartNew();
        long received = 0;
        int status = 0, retryMs = 0;
        string issue = "";
        bool blockSource = false, fast = false;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
            try {
                J.NoLinks(temp);
                long offset = keep && File.Exists(temp) ? new FileInfo(temp).Length : 0;
                if (offset >= maxBytes) { File.Delete(temp); offset = 0; }
                var deadline = TimeSpan.FromSeconds(hasAlternate ? 8 : 60);
                using (var request = new HttpRequestMessage(HttpMethod.Get, url)) {
                    request.Headers.AcceptEncoding.ParseAdd("identity");
                    if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                    timeout.CancelAfter(deadline);
                    using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false)) {
                        status = (int)response.StatusCode;
                        if (status == 416) { File.Delete(temp); throw new IOException("续传范围失效，正在重新下载"); }
                        if (!response.IsSuccessStatusCode) {
                            var retryAfter = response.Headers.RetryAfter;
                            if (status == 429 || status == 503) {
                                var delay = retryAfter == null ? TimeSpan.FromSeconds(2) :
                                    retryAfter.Delta ?? (retryAfter.Date.GetValueOrDefault() - DateTimeOffset.UtcNow);
                                retryMs = (int)Math.Max(200, Math.Min(30000, delay.TotalMilliseconds));
                            }
                            blockSource = status == 429 || status == 502 || status == 503 || status == 504;
                            var message = status == 429 ? "服务器限流（HTTP 429）" : "下载失败（HTTP " + status + "）";
                            if (blockSource || status >= 500) issue = message;
                            throw new IOException(message);
                        }
                        var append = response.StatusCode == HttpStatusCode.PartialContent;
                        var range = response.Content.Headers.ContentRange;
                        if (append && (range == null || !range.HasRange || range.Unit != "bytes" || range.From != offset ||
                            range.To < range.From || range.To >= maxBytes ||
                            (exact.HasValue && range.Length != exact) ||
                            (range.HasLength && (range.Length <= range.To || range.Length > maxBytes)) ||
                            (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength != range.To - range.From + 1) ||
                            response.Content.Headers.ContentEncoding.Any())) {
                            File.Delete(temp);
                            throw new IOException("服务器返回的续传数据无效");
                        }
                        var responseLength = sizeIsHint ? (append ? range.Length : response.Content.Headers.ContentLength) : exact;
                        if (responseLength > maxBytes) throw new IOException("下载文件大于预期");
                        long bytes = append ? offset : 0;
                        timeout.CancelAfter(deadline);
                        using (timeout.Token.Register(response.Dispose))
                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = new FileStream(temp, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 65536, true)) {
                            var buffer = new byte[65536];
                            int count;
                            while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) > 0) {
                                bytes += count;
                                if (bytes > maxBytes) throw new IOException("下载文件大于预期");
                                // Only idle network reads should trigger a source change.
                                timeout.CancelAfter(Timeout.Infinite);
                                received += count;
                                scheduler.AddBytes(count);
                                await output.WriteAsync(buffer, 0, count, ct).ConfigureAwait(false);
                                timeout.CancelAfter(deadline);
                            }
                            timeout.Token.ThrowIfCancellationRequested();
                            timeout.CancelAfter(Timeout.Infinite);
                        }
                        if (bytes < responseLength) {
                            issue = "下载连接提前结束";
                            throw new IOException(issue + "，已保留进度");
                        }
                        fast = elapsed.ElapsedMilliseconds <= 2000 || received * 1000 / elapsed.ElapsedMilliseconds >= 128 * 1024;
                    }
                }
            } catch (Exception error) {
                ct.ThrowIfCancellationRequested();
                if (timeout.IsCancellationRequested || error is OperationCanceledException) {
                    issue = "连接超时，未收到下载数据";
                    blockSource = true;
                    throw new IOException(issue, error);
                }
                if (error is HttpRequestException) issue = "下载连接中断";
                throw;
            } finally { scheduler.Leave(source, fast, issue, retryMs, blockSource); }
        }
    }

    static async Task CommitDownload(string source, string target, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        J.NoLinks(target);
        if (String.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase)) {
            if (File.Exists(target)) File.Replace(source, target, null); else File.Move(source, target);
            return;
        }
        // On another volume, stage beside the destination before publishing.
        var stage = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        J.NoLinks(target); J.NoLinks(stage);
        try {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
            using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await input.CopyToAsync(output, 65536, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            J.NoLinks(target);
            if (File.Exists(target)) File.Replace(stage, target, null); else File.Move(stage, target);
            File.Delete(source);
        } finally { if (File.Exists(stage)) File.Delete(stage); }
    }
    internal static void Unzip(string file, string dest, CancellationToken ct, Func<string,string> map = null, Action extracted = null) {
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
            if (extracted != null) extracted();
        }
    }
    internal async Task Pool<T>(IEnumerable<T> input, Func<T,Task> handle, CancellationToken ct) {
        var queue = new System.Collections.Concurrent.ConcurrentQueue<T>(input);
        var workers = Enumerable.Range(0, Math.Min(concurrency, queue.Count)).Select(async i => {
            T item;
            while (queue.TryDequeue(out item)) { ct.ThrowIfCancellationRequested(); await handle(item).ConfigureAwait(false); }
        });
        await Task.WhenAll(workers).ConfigureAwait(false);
    }
}
