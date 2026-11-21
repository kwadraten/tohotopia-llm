using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tohotopia;
using Xunit;

static class CredentialPaths
{
    internal static string File(string root, string name)
    {
        string directory = Path.Combine(root, "credentials");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, name);
    }
}

public class CredentialTests
{
    [Fact]
    public async Task Credential_lifecycle_isolation_and_atomicity()
    {
        string prior = Files.Root;
        string root = Path.Combine(Path.GetTempPath(), "tohotopia-credentials-" + Guid.NewGuid().ToString("N"));
        Files.Root = root;
        Directory.CreateDirectory(root);
        try
        {
            var auth = new OAuth();
            File.WriteAllText(Path.Combine(root, "deepseek.token"), "legacy-invalid-credential");
            Assert.True(auth.Credential("deepseek") == new CredentialInfo("deepseek", "", "", "", false)
                && !auth.Status("deepseek").Configured, "legacy root credentials are ignored instead of migrated");
            Assert.True(auth.Credential("deepseek") == new CredentialInfo("deepseek", "", "", "", false), "an absent credential returns empty metadata and no secret");
            auth.ConfigureApiKey("deepseek", new("fixture-deepseek-key"));
            auth.UpdateApiKey("deepseek", new("", "https://fixture.example/v1", "workspace"));
            Assert.True((await auth.Credentials("deepseek", default)).Access == "fixture-deepseek-key"
                && auth.Credential("deepseek") == new CredentialInfo("deepseek", "api_key", "https://fixture.example/v1", "workspace", true),
                "editing an API key with a blank secret retains the encrypted key and updates endpoint and workspace");
            string json = JsonSerializer.Serialize(auth.Credential("deepseek"), HostJson.Type<CredentialInfo>());
            using (var doc = JsonDocument.Parse(json))
                Assert.True(doc.RootElement.EnumerateObject().Count() == 5 && !json.Contains("fixture-deepseek-key")
                    && doc.RootElement.GetProperty("hasSecret").GetBoolean(), "credential metadata uses exactly five source-generated public fields and never exposes the key");
            var saved = File.ReadAllBytes(CredentialPaths.File(root, "deepseek.token"));
            foreach (var invalid in new[] { new ApiKeyRequest(""), new("new-secret", "http://remote.example/v1"), new("new-secret", "https://user:secret@example.test/v1"), new("new-secret", "https://api.example/v1?key=secret"), new("new-secret", WorkspaceId: "bad\nworkspace") })
            {
                await Reject(() => Task.FromResult(auth.ConfigureApiKey("deepseek", invalid)));
                Assert.True(saved.SequenceEqual(File.ReadAllBytes(CredentialPaths.File(root, "deepseek.token"))), "invalid API-key replacement leaves the prior encrypted credential byte-for-byte intact");
            }
            await Reject(() => auth.Start("deepseek", default));
            Assert.True(saved.SequenceEqual(File.ReadAllBytes(CredentialPaths.File(root, "deepseek.token")))
                && OAuth.AuthMethods("deepseek").All(m => m.Type == "api")
                && auth.Status("deepseek").AuthType == "api_key", "unsupported DeepSeek subscription login is rejected without changing its API-key account");
            auth.UpdateApiKey("deepseek", new("fixture-replaced"));
            Assert.True((await new OAuth().Credentials("deepseek", default)).Access == "fixture-replaced", "a nonempty API-key edit replaces the secret and survives OAuth instance restart");
            foreach (string invalid in new[] { "../deepseek", "..\\deepseek", "/deepseek", "C:deepseek", "deepseek/other", "%2e%2e", "deepseek..token", "" })
            {
                await Reject(() => Task.FromResult(auth.Credential(invalid)));
                await Reject(() => Task.FromResult(auth.ConfigureApiKey(invalid, new("fixture"))));
                await Reject(() => Task.FromResult(auth.Status(invalid)));
                await Reject(() => auth.Credentials(invalid, default));
                await Reject(() => auth.Start(invalid, default));
                await Reject(() => { auth.Delete(invalid); return Task.CompletedTask; });
            }
            Assert.True(File.Exists(CredentialPaths.File(root, "deepseek.token")), "all credential operations reject provider traversal and filename injection before touching storage");
            await Reject(() => Task.FromResult(auth.UpdateApiKey("google", new(""))));
            auth.ConfigureApiKey("xai", new("fixture-xai"));
            auth.ConfigureApiKey("anthropic", new("fixture-other"));
            File.WriteAllText(Path.Combine(root, "settings.json"), "fixture-settings");
            File.WriteAllText(Path.Combine(root, "room.json"), "fixture-room-seats");
            var other = File.ReadAllBytes(CredentialPaths.File(root, "anthropic.token"));
            auth.Delete("xai"); auth.Delete("grok");
            Assert.True(!auth.Credential("grok").HasSecret && !auth.Status("xai").Configured
                && !File.Exists(CredentialPaths.File(root, "grok-auth-status.json"))
                && other.SequenceEqual(File.ReadAllBytes(CredentialPaths.File(root, "anthropic.token")))
                && File.ReadAllText(Path.Combine(root, "settings.json")) == "fixture-settings"
                && File.ReadAllText(Path.Combine(root, "room.json")) == "fixture-room-seats",
                "idempotent alias deletion removes only the canonical provider account and preserves other providers, seats and global settings");
            using var transport = new ExchangeHandler();
            var oauth = new OAuth(new HttpClient(transport));
            var old = File.ReadAllBytes(CredentialPaths.File(root, "anthropic.token"));
            var login = await oauth.Start("anthropic", default);
            Assert.True(oauth.Credential("anthropic").AuthType == "api_key" && old.SequenceEqual(File.ReadAllBytes(CredentialPaths.File(root, "anthropic.token"))),
                "starting subscription reauthorization keeps the existing API-key account usable");
            transport.Fail = true;
            await Reject(() => oauth.Complete(login.Id, Callback(login), default));
            Assert.True(old.SequenceEqual(File.ReadAllBytes(CredentialPaths.File(root, "anthropic.token"))) && oauth.State(login.Id).Status == "failed",
                "failed subscription reauthorization reports failure without replacing the existing credential");
            transport.Fail = false;
            login = await oauth.Start("anthropic", default);
            await oauth.Complete(login.Id, Callback(login), default);
            Assert.True(oauth.Credential("anthropic").AuthType == "subscription" && oauth.Status("anthropic").AuthType == "subscription"
                && (await oauth.Credentials("anthropic", default)).Access == "fixture-subscription",
                "completed subscription authorization atomically replaces the API key and changes the reported authentication type");
            var subscription = File.ReadAllBytes(CredentialPaths.File(root, "anthropic.token"));
            await Reject(() => Task.FromResult(oauth.UpdateApiKey("anthropic", new(""))));
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                await Reject(() => oauth.Start("anthropic", canceled.Token));
            }
            Assert.True(subscription.SequenceEqual(File.ReadAllBytes(CredentialPaths.File(root, "anthropic.token"))),
                "blank API-key edits and canceled authorization cannot reinterpret or erase subscription credentials");
            oauth.UpdateApiKey("anthropic", new("fixture-new-api"));
            Assert.True((await oauth.Credentials("anthropic", default)) is { AuthType: "api_key", Refresh: "", Access: "fixture-new-api" },
                "switching from subscription to an explicit API key clears refresh-token state");
            await RefreshRace(root, false);
            await RefreshRace(root, true);
            await LoginRace(root, false);
            await LoginRace(root, true);
            await DeviceRace();
            await DeleteRetry();
            var logs = string.Join("", Directory.GetFiles(Path.Combine(root, "logs")).Select(File.ReadAllText));
            Assert.True(!logs.Contains("fixture-deepseek-key") && !logs.Contains("fixture-subscription") && !logs.Contains("fixture-refresh"),
                "credential lifecycle diagnostics never log keys, access tokens or refresh tokens");
        }
        finally { Files.Root = prior; Directory.Delete(root, true); }
    }
    static async Task RefreshRace(string root, bool replace)
    {
        Seed(root, "grok", new("fixture-old", "fixture-refresh", DateTimeOffset.UtcNow.AddMinutes(-1)));
        using var handler = new BlockedHandler();
        var oauth = new OAuth(new HttpClient(handler));
        var work = oauth.Credentials("xai", default, true);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (replace) oauth.ConfigureApiKey("grok", new("fixture-winner"));
        else oauth.Delete("xai");
        handler.Release.SetResult();
        await Reject(() => work);
        Assert.True(replace ? (await oauth.Credentials("grok", default)).Access == "fixture-winner"
            : !oauth.Credential("grok").HasSecret && !File.Exists(CredentialPaths.File(root, "grok-auth-status.json")),
            replace ? "a late refresh cannot overwrite a newly configured API key" : "a late refresh cannot resurrect deleted credentials or refresh-failure status");
    }
    static async Task LoginRace(string root, bool replace)
    {
        using var handler = new BlockedHandler();
        var oauth = new OAuth(new HttpClient(handler));
        var login = await oauth.Start("anthropic", default);
        var work = oauth.Complete(login.Id, Callback(login), default);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (replace) oauth.ConfigureApiKey("anthropic", new("fixture-login-winner"));
        else oauth.Delete("anthropic");
        handler.Release.SetResult();
        await Reject(() => work);
        Assert.True(replace ? (await oauth.Credentials("anthropic", default)).Access == "fixture-login-winner"
            : !oauth.Credential("anthropic").HasSecret && !File.Exists(CredentialPaths.File(root, "anthropic-auth-status.json")),
            replace ? "a late login exchange cannot overwrite an API-key replacement" : "a late login exchange cannot resurrect a deleted account or its status");
        Assert.Throws<KeyNotFoundException>(() => oauth.State(login.Id));
    }
    static async Task DeviceRace()
    {
        using var handler = new DeviceHandler();
        var oauth = new OAuth(new HttpClient(handler));
        var login = await oauth.Start("kimi-code-plan-global", default);
        await handler.PollEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        oauth.Delete("kimi");
        await handler.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(!oauth.Credential("kimi").HasSecret, "deleting a device-login alias cancels its in-flight token polling without creating credentials");
        Assert.Throws<KeyNotFoundException>(() => oauth.State(login.Id));
    }
    static async Task DeleteRetry()
    {
        var auth = new OAuth();
        string path = CredentialPaths.File(Files.Root, "grok.token");
        auth.ConfigureApiKey("grok", new("fixture-preserved-delete"));
        string statusPath = CredentialPaths.File(Files.Root, "grok-auth-status.json");
        using (var locked = new FileStream(statusPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bool failed = false;
            try { auth.Delete("grok"); }
            catch (IOException) { failed = true; }
            Assert.True(failed && File.Exists(path) && auth.Credential("grok").HasSecret,
                "a locked companion file reports failure without deleting the primary credential first");
        }
        auth.Delete("grok");
        auth.ConfigureApiKey("grok", new("fixture-readonly-delete"));
        File.SetAttributes(path, FileAttributes.ReadOnly);
        auth.Delete("grok");
        Assert.True(!File.Exists(path), "credential removal clears a read-only attribute before deleting the token");
        auth.ConfigureApiKey("grok", new("fixture-locked-delete"));
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var removal = Task.Run(() => { started.SetResult(); auth.Delete("grok"); });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(150);
            Assert.True(!removal.IsCompleted, "credential removal waits briefly for another process to release its file lock");
            locked.Dispose();
            await removal.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(!File.Exists(path) && !auth.Credential("grok").HasSecret, "credential removal finishes after a transient file lock is released");
    }
    static string Callback(LoginState login) => "fixture-code#" + OAuth.ParseCallback(login.Url).State;
    static void Seed(string root, string provider, Token token) => File.WriteAllBytes(CredentialPaths.File(root, provider + ".token"),
        ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(token, HostJson.Type<Token>(HostJson.Plain)), null, DataProtectionScope.CurrentUser));
    static async Task Reject(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or OperationCanceledException) { return; }
        throw new Exception("Invalid or superseded credential operation was accepted");
    }
    static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    sealed class ExchangeHandler : HttpMessageHandler
    {
        internal bool Fail;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(Fail ? Reply("{}", HttpStatusCode.BadRequest) : Reply("""{"access_token":"fixture-subscription","refresh_token":"fixture-refresh","expires_in":3600}"""));
    }
    sealed class BlockedHandler : HttpMessageHandler
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Entered.SetResult();
            await Release.Task;
            return Reply("""{"access_token":"fixture-late-token","refresh_token":"fixture-late-refresh","expires_in":3600}""");
        }
    }
    sealed class DeviceHandler : HttpMessageHandler
    {
        internal readonly TaskCompletionSource PollEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("device_authorization"))
                return Reply("""{"verification_uri":"https://fixture.example/authorize","device_code":"fixture-device","user_code":"fixture-code","interval":1,"expires_in":60}""");
            PollEntered.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { Canceled.SetResult(); throw; }
            throw new Exception("Device polling should have been canceled");
        }
    }
}
