using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

sealed class Accounts
{
    const string Client = "6970569a-c58e-4b83-9e46-c44e0b3d3ab4";
    const string OAuth = "https://login.microsoftonline.com/consumers/oauth2/v2.0/";
    const string Scope = "XboxLive.signin offline_access";
    const string MinecraftService = "https://api.minecraftservices.com/";
    readonly string file;
    internal Accounts(string profile) { file = Path.Combine(profile, "microsoft-account.dat"); }
    object Load() {
        if (!File.Exists(file)) return null;
        return J.Parse(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser)));
    }
    void Save(object account) {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(J.Dump(account)), null, DataProtectionScope.CurrentUser);
        var temp = file + ".tmp";
        try {
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(file)) File.Replace(temp, file, null); else File.Move(temp, file);
        } finally { File.Delete(temp); }
    }
    static object Summary(object account) {
        return account == null ? null : J.Map("id", account.S("id"), "name", account.S("name"), "skin", account.S("skin"),
            "cape", account.S("cape"), "capeId", account.S("capeId"), "capes", account.A("capes"),
            "variant", account.S("variant"), "needsRefresh", !account.Obj().ContainsKey("skin"));
    }
    internal object Summary() { return Summary(Load()); }
    internal void Logout() { File.Delete(file); }
    static Task<Tuple<int,object>> Form(Network net, string route, CancellationToken ct, params object[] fields) {
        return net.Request(OAuth + route, ct, new FormUrlEncodedContent(J.Map(fields).Select(x =>
            new System.Collections.Generic.KeyValuePair<string,string>(x.Key, x.Value.S()))));
    }
    static Task<Tuple<int,object>> Post(Network net, string url, object data, CancellationToken ct) {
        return net.Request(url, ct, new StringContent(J.Dump(data), Encoding.UTF8, "application/json"));
    }
    internal async Task<object> Login(Network net, Action<object> notify, CancellationToken ct) {
        var start = await Form(net, "devicecode", ct, "client_id", Client, "scope", Scope);
        if (start.Item1 != 200 || start.Item2.S("user_code") == "") throw new IOException("微软未接受 EBuild 的设备登录请求。");
        notify(J.Map("phase", "device", "message", "在浏览器输入设备代码", "code", start.Item2.S("user_code")));
        var end = DateTime.UtcNow.AddSeconds(start.Item2.I("expires_in", 900));
        var interval = Math.Max(1, start.Item2.I("interval", 5));
        while (DateTime.UtcNow < end) {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            var token = await Form(net, "token", ct, "client_id", Client, "grant_type", "urn:ietf:params:oauth:grant-type:device_code", "device_code", start.Item2.S("device_code"));
            if (token.Item1 == 200) {
                var account = await Exchange(net, token.Item2, ct);
                ct.ThrowIfCancellationRequested();
                Save(account);
                return Summary(account);
            }
            if (token.Item2.S("error") == "slow_down") interval += 5;
            else if (token.Item2.S("error") != "authorization_pending") throw new IOException("微软登录未完成，请重新登录。");
        }
        throw new IOException("微软登录代码已过期。");
    }
    internal async Task<object> ForLaunch(Network net, CancellationToken ct) {
        var saved = Load();
        if (saved == null) throw new IOException("请先登录微软账号，或选择离线档案。");
        if (Convert.ToInt64(saved.At("expires")) > DateTime.UtcNow.AddMinutes(1).Ticks) return saved;
        return await Renew(net, saved, ct);
    }
    async Task<object> Renew(Network net, object saved, CancellationToken ct) {
        var token = await Form(net, "token", ct, "client_id", Client, "grant_type", "refresh_token", "refresh_token", saved.S("refresh"), "scope", Scope);
        if (token.Item1 != 200) throw new IOException("微软登录已过期，请重新登录。");
        var account = await Exchange(net, token.Item2, ct);
        RequireSameProfile(saved, account);
        ct.ThrowIfCancellationRequested();
        Save(account);
        return account;
    }
    internal async Task<object> RefreshProfile(Network net, CancellationToken ct) {
        return Summary(await FetchProfile(net, ct));
    }

    async Task<object> FetchProfile(Network net, CancellationToken ct, string expectedId = null) {
        var account = Load();
        if (account == null) throw new IOException("请先登录微软账号。");
        if (expectedId != null && (expectedId == "" || account.S("id") != expectedId))
            throw new IOException("当前账号已切换，请重新打开修改窗口。");
        if (Convert.ToInt64(account.At("expires")) <= DateTime.UtcNow.AddMinutes(1).Ticks)
            return await Renew(net, account, ct);
        var response = await net.Request(MinecraftService + "minecraft/profile", ct, token: account.S("token"));
        if (response.Item1 == 401) return await Renew(net, account, ct);
        ct.ThrowIfCancellationRequested();
        return SaveProfile(account, response);
    }

    internal async Task<object> ChangeName(Network net, string expectedId, string name, CancellationToken ct) {
        if (!Regex.IsMatch(name, @"\A[A-Za-z0-9_]{3,16}\z"))
            throw new IOException("Java 档案名须为 3–16 位字母、数字或下划线。");
        var account = await FetchProfile(net, ct, expectedId);
        if (name == account.S("name")) throw new IOException("当前已使用这个档案名。");
        var route = MinecraftService + "minecraft/profile/name/" + Uri.EscapeDataString(name);
        var limit = await net.Request(MinecraftService + "minecraft/profile/namechange", ct, token: account.S("token"));
        RequireServiceSuccess(limit);
        if (Equals(limit.Item2.At("nameChangeAllowed"), false))
            throw new IOException("Minecraft 目前不允许此账号改名。Java 档案名每 30 天可更改一次。");
        var available = await net.Request(route + "/available", ct, token: account.S("token"));
        RequireServiceSuccess(available);
        switch (available.Item2.S("status")) {
            case "AVAILABLE": break;
            case "DUPLICATE":
                if (string.Equals(name, account.S("name"), StringComparison.OrdinalIgnoreCase)) break;
                throw new IOException("这个档案名已被使用。");
            case "NOT_ALLOWED": throw new IOException("Minecraft 不允许使用这个档案名。");
            default: throw new IOException("暂时无法确认这个档案名是否可用。");
        }
        return await ApplyProfileChange(net, account, route, HttpMethod.Put, null, ct);
    }

    internal async Task<object> ChangeSkin(Network net, string expectedId, byte[] png, string variant, CancellationToken ct) {
        if (png == null || png.Length == 0 || png.Length > 1024 * 1024) throw new IOException("请选择有效的 PNG 皮肤。");
        if (variant != "classic" && variant != "slim") throw new IOException("请选择经典或纤细模型。");
        var account = await FetchProfile(net, ct, expectedId);
        using (var form = new MultipartFormDataContent()) {
            form.Add(new StringContent(variant), "variant");
            var file = new ByteArrayContent(png);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            form.Add(file, "file", "skin.png");
            return await ApplyProfileChange(net, account, MinecraftService + "minecraft/profile/skins", HttpMethod.Post, form, ct);
        }
    }

    internal async Task<object> ChangeCape(Network net, string expectedId, string capeId, CancellationToken ct) {
        var account = await FetchProfile(net, ct, expectedId);
        if (capeId != "" && !account.A("capes").Any(cape => cape.S("id") == capeId))
            throw new IOException("当前账号没有这件披风，请重新打开披风管理。");
        if (capeId == account.S("capeId")) return J.Map("profile", Summary(account), "synced", true);
        string route = MinecraftService + "minecraft/profile/capes/active";
        if (capeId == "") return await ApplyProfileChange(net, account, route, HttpMethod.Delete, null, ct);
        using (var data = new StringContent(J.Dump(J.Map("capeId", capeId)), Encoding.UTF8, "application/json"))
            return await ApplyProfileChange(net, account, route, HttpMethod.Put, data, ct);
    }

    async Task<object> ApplyProfileChange(Network net, object account, string url, HttpMethod method, HttpContent data, CancellationToken ct) {
        Tuple<int,object> response;
        try {
            response = await net.Request(url, ct, data, account.S("token"), method);
        } catch (Exception error) {
            if (!(error is HttpRequestException) && (!(error is OperationCanceledException) || ct.IsCancellationRequested)) throw;
            throw new IOException("未收到 Minecraft Services 的确认，请先刷新档案再重试。", error);
        }
        RequireServiceSuccess(response);
        try {
            if (response.Item2.At("skins") == null || response.Item2.At("capes") == null)
                response = await net.Request(MinecraftService + "minecraft/profile", ct, token: account.S("token"));
            return J.Map("profile", Summary(SaveProfile(account, response)), "synced", true);
        } catch (Exception error) {
            if (!(error is IOException || error is HttpRequestException || error is UnauthorizedAccessException ||
                error is OperationCanceledException)) throw;
            return J.Map("profile", Summary(account), "synced", false);
        }
    }

    static void RequireServiceSuccess(Tuple<int,object> response) {
        int status = response.Item1;
        if (status >= 200 && status < 300) return;
        if (status == 401) throw new IOException("Minecraft 登录已过期，请刷新正版档案后重试。");
        if (status == 403) throw new IOException("Minecraft Services 拒绝了这项操作（HTTP 403）。也可以到 Minecraft 官网管理。");
        if (status == 409) throw new IOException("账号档案发生冲突，请刷新档案后再试。");
        if (status == 429) throw new IOException("Minecraft Services 请求过于频繁，请稍后重试。");
        throw new IOException("Minecraft Services 未接受这项操作（HTTP " + status + "）。");
    }

    static void RequireSameProfile(object original, object updated) {
        if (original.S("id") != updated.S("id")) throw new IOException("当前账号已切换，请重新打开修改窗口。");
    }

    object SaveProfile(object account, Tuple<int,object> response) {
        var profile = ReadProfile(response);
        RequireSameProfile(account, profile);
        var updated = J.Merge(account, profile);
        Save(updated);
        return updated;
    }
    static string TextureUrl(object item) {
        Uri url;
        if (!Uri.TryCreate(item.S("url"), UriKind.Absolute, out url) || url.Host != "textures.minecraft.net" ||
            (url.Scheme != "http" && url.Scheme != "https") || !url.IsDefaultPort || url.UserInfo.Length != 0 ||
            !Regex.IsMatch(url.AbsolutePath, @"^/texture/[a-fA-F0-9]{32,64}$")) return "";
        return "https://textures.minecraft.net" + url.AbsolutePath;
    }
    static object ReadProfile(Tuple<int,object> player) {
        if (player.Item1 != 200 || player.Item2.S("name") == "" || player.Item2.S("id") == "")
            throw new IOException("未找到 Minecraft Java 版玩家档案。");
        var skin = player.Item2.A("skins").FirstOrDefault(x => x.S("state") == "ACTIVE");
        var capes = player.Item2.A("capes").Where(item => {
            Guid capeId;
            return Guid.TryParse(item.S("id"), out capeId);
        })
            .Select(item => J.Map("id", item.S("id"), "alias", item.S("alias"), "url", TextureUrl(item),
                "active", item.S("state") == "ACTIVE")).ToArray();
        var cape = capes.FirstOrDefault(item => item.B("active"));
        return J.Map("id", player.Item2.S("id"), "name", player.Item2.S("name"), "skin", TextureUrl(skin),
            "cape", cape.S("url"), "capeId", cape.S("id"), "capes", capes,
            "variant", skin.S("variant") == "SLIM" ? "slim" : "classic");
    }
    static async Task<object> Exchange(Network net, object microsoft, CancellationToken ct) {
        var user = await Post(net, "https://user.auth.xboxlive.com/user/authenticate",
            J.Map("Properties", J.Map("AuthMethod", "RPS", "SiteName", "user.auth.xboxlive.com", "RpsTicket", "d=" + microsoft.S("access_token")),
                "RelyingParty", "http://auth.xboxlive.com", "TokenType", "JWT"), ct);
        if (user.Item1 != 200) throw new IOException("Xbox Live 登录失败，请确认已创建 Xbox 档案。");
        var xbox = await Post(net, "https://xsts.auth.xboxlive.com/xsts/authorize",
            J.Map("Properties", J.Map("SandboxId", "RETAIL", "UserTokens", new[] { user.Item2.S("Token") }),
                "RelyingParty", "rp://api.minecraftservices.com/", "TokenType", "JWT"), ct);
        var claim = xbox.Item2.At("DisplayClaims").A("xui").FirstOrDefault();
        if (xbox.Item1 != 200 || claim.S("uhs") == "") throw new IOException("Xbox 授权失败（" + xbox.Item2.S("XErr") + "）。");
        var minecraft = await Post(net, MinecraftService + "authentication/login_with_xbox",
            J.Map("identityToken", "XBL3.0 x=" + claim.S("uhs") + ";" + xbox.Item2.S("Token")), ct);
        if (minecraft.Item1 != 200) throw new IOException(minecraft.Item1 == 403 ? "Minecraft Services 拒绝了 EBuild 应用的授权。" : "Minecraft Services 登录失败。");
        var access = minecraft.Item2.S("access_token");
        if (access.Length == 0) throw new IOException("Minecraft Services 未返回登录凭据。");
        var owns = await net.Request(MinecraftService + "entitlements/mcstore", ct, token: access);
        if (owns.Item1 != 200 || !owns.Item2.A("items").Any()) throw new IOException("未能验证该账号的 Minecraft Java 版所有权。");
        var player = await net.Request(MinecraftService + "minecraft/profile", ct, token: access);
        return J.Merge(ReadProfile(player), J.Map("token", access, "clientId", Client, "xuid", claim.S("xid"),
            "refresh", microsoft.S("refresh_token"), "expires", DateTime.UtcNow.AddSeconds(minecraft.Item2.I("expires_in", 86400)).Ticks));
    }
}
