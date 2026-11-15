using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
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
        return account == null ? null : J.Map("name", account.S("name"), "skin", account.S("skin"),
            "cape", account.S("cape"), "variant", account.S("variant"), "needsRefresh", !account.Obj().ContainsKey("skin"));
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
        ct.ThrowIfCancellationRequested();
        Save(account);
        return account;
    }
    internal async Task<object> RefreshProfile(Network net, CancellationToken ct) {
        var account = Load();
        if (account == null) throw new IOException("请先登录微软账号。");
        if (Convert.ToInt64(account.At("expires")) <= DateTime.UtcNow.AddMinutes(1).Ticks)
            return Summary(await Renew(net, account, ct));
        var response = await net.Request(MinecraftService + "minecraft/profile", ct, token: account.S("token"));
        if (response.Item1 == 401) return Summary(await Renew(net, account, ct));
        var updated = J.Merge(account, ReadProfile(response));
        ct.ThrowIfCancellationRequested();
        Save(updated);
        return Summary(updated);
    }
    static string TextureUrl(object item) {
        Uri url;
        if (!Uri.TryCreate(item.S("url"), UriKind.Absolute, out url) || url.Host != "textures.minecraft.net" ||
            (url.Scheme != "http" && url.Scheme != "https") || !url.IsDefaultPort || url.UserInfo.Length != 0 ||
            !System.Text.RegularExpressions.Regex.IsMatch(url.AbsolutePath, @"^/texture/[a-fA-F0-9]{32,64}$")) return "";
        return "https://textures.minecraft.net" + url.AbsolutePath;
    }
    static object ReadProfile(Tuple<int,object> player) {
        if (player.Item1 != 200 || player.Item2.S("name") == "" || player.Item2.S("id") == "")
            throw new IOException("未找到 Minecraft Java 版玩家档案。");
        var skin = player.Item2.A("skins").FirstOrDefault(x => x.S("state") == "ACTIVE");
        var cape = player.Item2.A("capes").FirstOrDefault(x => x.S("state") == "ACTIVE");
        return J.Map("id", player.Item2.S("id"), "name", player.Item2.S("name"), "skin", TextureUrl(skin),
            "cape", TextureUrl(cape), "variant", skin.S("variant") == "SLIM" ? "slim" : "classic");
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
