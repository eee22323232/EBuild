using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

static class OfficialAppearance
{
    const string Id = "ab38c0ae2dcd43cbade71b49bd7a92e2";
    const string Cape = "25cd2ad0-0aec-4337-94a8-8ef45186f068";
    static readonly string Texture = "https://textures.minecraft.net/texture/" + new string('a', 64);

    sealed class Service : HttpMessageHandler {
        readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> calls = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();

        internal void Expect(HttpMethod method, string path, object body, int status = 200, Action<HttpRequestMessage> check = null) {
            calls.Enqueue(request => {
                Require(request.Method == method && request.RequestUri == new Uri("https://api.minecraftservices.com/minecraft/profile" + path),
                    "Appearance changes must use the expected Minecraft Services endpoint.");
                Require(request.Headers.Authorization.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "test-token",
                    "Appearance changes must use the Minecraft access token.");
                check?.Invoke(request);
                return new HttpResponseMessage((HttpStatusCode)status) {
                    Content = new StringContent(body == null ? "" : J.Dump(body), Encoding.UTF8, "application/json")
                };
            });
        }

        internal void Drained() { Require(calls.Count == 0, "A required Minecraft Services request was skipped."); }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            Require(calls.Count != 0, "Unexpected Minecraft Services request.");
            return Task.FromResult(calls.Dequeue()(request));
        }
    }

    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    static object Profile(string name, bool wearing = false, string variant = "CLASSIC") => J.Map(
        "id", Id, "name", name, "skins", new[] { J.Map("state", "ACTIVE", "url", Texture, "variant", variant) },
        "capes", new[] { J.Map("id", Cape, "state", wearing ? "ACTIVE" : "INACTIVE", "url", Texture, "alias", "Test Cape") });

    static async Task Refused(Func<Task<object>> action, string message) {
        try { await action(); }
        catch (IOException error) when (error.Message.Contains(message)) { return; }
        throw new InvalidOperationException("An invalid appearance change was not refused.");
    }

    internal static async Task Run(string root) {
        Directory.CreateDirectory(root);
        var saved = J.Map("id", Id, "name", "OldName", "token", "test-token", "expires", DateTime.UtcNow.AddHours(2).Ticks);
        File.WriteAllBytes(Path.Combine(root, "microsoft-account.dat"),
            ProtectedData.Protect(Encoding.UTF8.GetBytes(J.Dump(saved)), null, DataProtectionScope.CurrentUser));
        var skinFile = Path.Combine(root, "skin.png");
        using (var image = new Bitmap(64, 64)) image.Save(skinFile, ImageFormat.Png);
        byte[] skin = LocalAppearance.ReadPng("skin", skinFile);
        var accounts = new Accounts(root);
        var service = new Service();
        using (var net = new Network(J.Map(), transport: service)) {
            var ct = CancellationToken.None;
            await Refused(() => accounts.ChangeName(net, "changed-account", "NewName", ct), "已切换");
            service.Expect(HttpMethod.Get, "", Profile("OldName"));
            service.Expect(HttpMethod.Get, "/namechange", J.Map("nameChangeAllowed", false));
            await Refused(() => accounts.ChangeName(net, Id, "NewName", ct), "30 天");
            service.Expect(HttpMethod.Get, "", Profile("OldName"));
            service.Expect(HttpMethod.Get, "/namechange", J.Map("nameChangeAllowed", true));
            service.Expect(HttpMethod.Get, "/name/NewName/available", J.Map("status", "DUPLICATE"));
            await Refused(() => accounts.ChangeName(net, Id, "NewName", ct), "已被使用");
            service.Expect(HttpMethod.Get, "", Profile("OldName"));
            service.Expect(HttpMethod.Get, "/namechange", J.Map("nameChangeAllowed", true));
            service.Expect(HttpMethod.Get, "/name/NewName/available", J.Map("status", "AVAILABLE"));
            service.Expect(HttpMethod.Put, "/name/NewName", Profile("NewName"));
            var renamed = await accounts.ChangeName(net, Id, "NewName", ct);
            Require(renamed.B("synced") && accounts.Summary().S("name") == "NewName", "Save the confirmed Java name.");

            service.Expect(HttpMethod.Get, "", Profile("NewName"));
            service.Expect(HttpMethod.Post, "/skins", Profile("NewName", variant: "SLIM"), check: request => {
                var fields = ((MultipartFormDataContent)request.Content).ToDictionary(part => part.Headers.ContentDisposition.Name.Trim('"'));
                Require(fields["variant"].ReadAsStringAsync().GetAwaiter().GetResult() == "slim" &&
                    fields["file"].Headers.ContentType.MediaType == "image/png" &&
                    fields["file"].ReadAsByteArrayAsync().GetAwaiter().GetResult().SequenceEqual(skin), "Upload the selected PNG and model variant.");
            });
            Require((await accounts.ChangeSkin(net, Id, skin, "slim", ct)).At("profile").S("variant") == "slim", "Refresh the selected skin model.");

            service.Expect(HttpMethod.Get, "", Profile("NewName", variant: "SLIM"));
            await Refused(() => accounts.ChangeCape(net, Id, "unowned", ct), "没有这件披风");
            service.Expect(HttpMethod.Get, "", Profile("NewName", variant: "SLIM"));
            service.Expect(HttpMethod.Put, "/capes/active", Profile("NewName", true, "SLIM"), check: request => {
                Require(J.Parse(request.Content.ReadAsStringAsync().GetAwaiter().GetResult()).S("capeId") == Cape, "Send the owned cape ID.");
            });
            var equipped = await accounts.ChangeCape(net, Id, Cape, ct);
            Require(equipped.B("synced") && equipped.At("profile").S("capeId") == Cape, "Refresh the active cape after changing it.");
            service.Expect(HttpMethod.Get, "", Profile("NewName", true, "SLIM"));
            service.Expect(HttpMethod.Delete, "/capes/active", null, 204);
            service.Expect(HttpMethod.Get, "", Profile("NewName", variant: "SLIM"));
            var hidden = await accounts.ChangeCape(net, Id, "", ct);
            Require(hidden.B("synced") && hidden.At("profile").S("capeId") == "", "Refresh the profile when hiding a cape returns no content.");
            service.Expect(HttpMethod.Get, "", Profile("NewName", true, "SLIM"));
            service.Expect(HttpMethod.Delete, "/capes/active", null, 204);
            service.Expect(HttpMethod.Get, "", null, 503);
            Require(!(await accounts.ChangeCape(net, Id, "", ct)).B("synced"), "Distinguish a confirmed change from a failed refresh.");
            service.Drained();
        }
        Console.WriteLine("PASS: official profile edits enforce account identity, name availability, PNG upload and cape ownership.");
    }
}
