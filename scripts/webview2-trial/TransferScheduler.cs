using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

sealed class TransferScheduler
{
    internal sealed class Source
    {
        internal readonly string Host;
        internal int Active, Limit, FastResponses, Failures;
        internal long RetryAt, PenalizedUntil, ReduceAfter;
        internal string Issue = "";

        internal Source(string host, int limit) { Host = host; Limit = limit; }
    }

    readonly object gate = new object();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly Dictionary<string, Source> sources = new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase);
    readonly int ceiling;
    TaskCompletionSource<bool> changed = NewSignal();
    int active;
    long started, networkTime, bytes, previousBytes, sampledAt, speed;
    string lastSwitch = "";

    internal TransferScheduler(int ceiling) { this.ceiling = ceiling; }

    static TaskCompletionSource<bool> NewSignal() {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    void Signal() {
        var previous = changed;
        changed = NewSignal();
        previous.TrySetResult(true);
    }

    internal Source For(Uri uri) {
        lock (gate) {
            Source source;
            if (!sources.TryGetValue(uri.Host, out source)) {
                source = new Source(uri.Host, Math.Min(8, ceiling));
                sources.Add(uri.Host, source);
            }
            return source;
        }
    }

    internal async Task<string> Enter(Source source, bool hasAlternate, CancellationToken ct) {
        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
            var canceled = Task.Delay(Timeout.Infinite, waiting.Token);
            try {
                while (true) {
                    ct.ThrowIfCancellationRequested();
                    Task signal;
                    long delay;
                    lock (gate) {
                        var now = clock.ElapsedMilliseconds;
                        if (hasAlternate && Math.Max(source.PenalizedUntil, source.RetryAt) > now) return source.Issue;
                        delay = source.RetryAt - now;
                        if (delay <= 0 && source.Active < source.Limit && active < ceiling) {
                            if (active++ == 0) started = now;
                            source.Active++;
                            return "";
                        }
                        signal = changed.Task;
                    }
                    if (delay > 0) {
                        using (var retry = CancellationTokenSource.CreateLinkedTokenSource(waiting.Token)) {
                            await Task.WhenAny(signal, Task.Delay((int)delay, retry.Token)).ConfigureAwait(false);
                            retry.Cancel();
                        }
                    } else await Task.WhenAny(signal, canceled).ConfigureAwait(false);
                }
            } finally { waiting.Cancel(); }
        }
    }

    internal void Leave(Source source, bool fast, string issue, int retryMs, bool blockSource) {
        lock (gate) {
            var now = clock.ElapsedMilliseconds;
            if (issue.Length != 0) {
                source.FastResponses = 0;
                source.Failures++;
                if (source.ReduceAfter <= now) {
                    source.Limit = Math.Max(1, (source.Limit + 1) / 2);
                    source.ReduceAfter = now + 2000;
                }
                source.Issue = issue;
                source.PenalizedUntil = Math.Max(source.PenalizedUntil, now + (blockSource || source.Failures >= 2 ? 15000 : 0));
                if (retryMs > 0) source.RetryAt = Math.Max(source.RetryAt, now + retryMs);
            } else if (fast && source.PenalizedUntil <= now) {
                source.Failures = 0;
                if (source.Limit < ceiling && ++source.FastResponses >= source.Limit) {
                    source.Limit = Math.Min(ceiling, source.Limit + 2);
                    source.FastResponses = 0;
                }
            }
            source.Active--;
            if (--active == 0) networkTime += now - started;
            Signal();
        }
    }

    internal void AddBytes(int count) { Interlocked.Add(ref bytes, count); }

    internal void Switched(Source from, Source to, string reason) {
        if (from == to) return;
        lock (gate) lastSwitch = Label(from.Host) + " → " + Label(to.Host) + " · " + reason;
    }

    static string Label(string host) {
        if (host == "bmclapi2.bangbang93.com") return "BMCLAPI";
        if (host == "libraries.minecraft.net" || host == "resources.download.minecraft.net" ||
            host.EndsWith(".mojang.com", StringComparison.OrdinalIgnoreCase)) return "Mojang 官方源";
        if (host == "cdn.modrinth.com") return "Modrinth";
        if (host.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase)) return "CurseForge CDN";
        return host;
    }

    internal object Snapshot() {
        lock (gate) {
            var now = clock.ElapsedMilliseconds;
            var total = Interlocked.Read(ref bytes);
            var elapsed = now - sampledAt;
            if (elapsed >= 500) {
                speed = (total - previousBytes) * 1000 / elapsed;
                previousBytes = total;
                sampledAt = now;
            }
            var current = String.Join("、", sources.Values.Where(source => source.Active > 0)
                .OrderByDescending(source => source.Active).ThenBy(source => source.Host).Select(source => Label(source.Host)).Distinct());
            return J.Map("source", current, "bytesPerSecond", speed, "bytes", total,
                "networkMs", networkTime + (active == 0 ? 0 : now - started), "switch", lastSwitch);
        }
    }
}
