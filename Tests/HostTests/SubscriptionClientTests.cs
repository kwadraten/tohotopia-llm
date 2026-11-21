using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Tohotopia;
using Xunit;

public class SubscriptionClientTests
{
    [Fact]
    public void Pkce_and_callback_parsing()
    {
        Assert.True(OAuth.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk") == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", "PKCE RFC 7636 challenge");
        Assert.True(OAuth.ParseCallback("http://localhost:1455/auth/callback?code=abc&state=xyz") == ("abc", "xyz"), "parse browser callback");
        Assert.True(OAuth.ParseCallback("abc#xyz") == ("abc", "xyz"), "parse manual authorization code");
    }

    [Fact]
    public void OpenAI_body_contract()
    {
        string body = SubscriptionClient.OpenAIBody(new AgentConfig(), new[] { new ChatMessage(ChatRole.System, "rules"), new ChatMessage(ChatRole.User, "state"), new ChatMessage(ChatRole.Assistant, "decision") }).ToJsonString();
        Assert.True(body.Contains("rules") && body.Contains("input_text") && body.Contains("output_text") && body.Contains("\"store\":false") && body.Contains("\"tools\":[]"), "direct subscription Responses request has instructions and no tools");
    }

    [Fact]
    public async Task Responses_sse_parsing()
    {
        string sse = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"ok\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n";
        Assert.True(await SubscriptionClient.ReadResponses(new MemoryStream(Encoding.UTF8.GetBytes(sse)), CancellationToken.None) == "ok", "parse complete subscription SSE");
        string itemSse = "data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"item-final\"}]}}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n";
        Assert.True(await SubscriptionClient.ReadResponses(new MemoryStream(Encoding.UTF8.GetBytes(itemSse)), CancellationToken.None) == "item-final", "completed item text survives an empty final output snapshot");
        await Assert.ThrowsAsync<InvalidOperationException>(() => SubscriptionClient.ReadResponses(new MemoryStream(Encoding.UTF8.GetBytes("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n")), CancellationToken.None));
    }

    [Fact]
    public async Task Direct_http_retries_401_once_after_refresh()
    {
        string prior = Files.Root;
        string root = Path.Combine(Path.GetTempPath(), "tohotopia-direct-" + Guid.NewGuid().ToString("N"));
        Files.Root = root;
        Directory.CreateDirectory(root);
        try
        {
            var transport = new FixtureTransport();
            var oauthFixture = new OAuth(new HttpClient(transport));
            var fixtureToken = new Token("fixture-old", "fixture-refresh", DateTimeOffset.UtcNow.AddHours(1), "fixture-account", "fixture-client", "chatgpt.tokens.use.direct");
            File.WriteAllBytes(CredentialPaths.File(root, "openai.token"), ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(fixtureToken, HostJson.Type<Token>(HostJson.Plain)), null, DataProtectionScope.CurrentUser));
            using var direct = new SubscriptionClient(new AgentConfig(), oauthFixture, new HttpClient(transport));
            var response = await direct.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "fixture-state") }, new ChatOptions { Instructions = "fixture-rules" });
            Assert.True(response.Text == "fixture-choice" && transport.ModelCalls == 2 && transport.RefreshCalls == 1, "direct HTTP retries 401 once after OAuth refresh");
            Assert.True(transport.LastBody.Contains("fixture-rules") && transport.LastBody.Contains("fixture-state"), "Agent Framework instructions reach direct HTTP transport");
        }
        finally { Files.Root = prior; Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Mismatched_oauth_state_rejected_before_exchange()
    {
        string prior = Files.Root;
        string root = Path.Combine(Path.GetTempPath(), "tohotopia-oauth-state-" + Guid.NewGuid().ToString("N"));
        Files.Root = root;
        Directory.CreateDirectory(root);
        try
        {
            var auth = new OAuth();
            var manual = await auth.Start("anthropic", CancellationToken.None);
            await Assert.ThrowsAsync<ArgumentException>(() => auth.Complete(manual.Id, "abc#wrong-state", CancellationToken.None));
        }
        finally { Files.Root = prior; Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Credential_restore_and_refresh_failure_lifecycle()
    {
        string prior = Files.Root;
        string root = Path.Combine(Path.GetTempPath(), "tohotopia-refresh-" + Guid.NewGuid().ToString("N"));
        Files.Root = root;
        Directory.CreateDirectory(root);
        try
        {
            var transport = new FixtureTransport();
            var fixtureToken = new Token("fixture-old", "fixture-refresh", DateTimeOffset.UtcNow.AddHours(1), "fixture-account", "fixture-client", "chatgpt.tokens.use.direct");
            File.WriteAllBytes(CredentialPaths.File(root, "openai.token"), ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(fixtureToken, HostJson.Type<Token>(HostJson.Plain)), null, DataProtectionScope.CurrentUser));
            // As in the original sequence, a direct request hits 401, refreshes
            // once and stores the rotated token before restore is checked.
            var oauthFixture = new OAuth(new HttpClient(transport));
            using (var direct = new SubscriptionClient(new AgentConfig(), oauthFixture, new HttpClient(transport)))
                await direct.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "fixture-state") });
            var restoredAuth = new OAuth(new HttpClient(transport));
            var restoredToken = await restoredAuth.Credentials("openai", CancellationToken.None);
            Assert.True(restoredToken.Access == "fixture-new" && restoredToken.Refresh == "fixture-rotated" && restoredToken.ClientId == "fixture-client" && restoredToken.Scope.Contains("chatgpt.tokens.use.direct"), "new OAuth instance restores encrypted token, rotated refresh and client registration");
            Assert.True(restoredAuth.Status("openai").Connected && restoredAuth.Status("openai").RefreshAvailable, "account management restores persisted subscription status");
            Assert.True(!Encoding.UTF8.GetString(File.ReadAllBytes(CredentialPaths.File(root, "openai.token"))).Contains("fixture-new"), "persistent credential file does not contain plaintext token");
            var tokenBytesBefore = File.ReadAllBytes(CredentialPaths.File(root, "openai.token"));
            transport.FailRefresh = true;
            await Assert.ThrowsAsync<InvalidOperationException>(() => restoredAuth.Credentials("openai", CancellationToken.None, true));
            transport.FailRefresh = false;
            Assert.True(File.ReadAllBytes(CredentialPaths.File(root, "openai.token")).SequenceEqual(tokenBytesBefore) && new OAuth().Status("openai").Status == "refresh_failed", "refresh failure preserves credentials and diagnostic state across restart");
            await restoredAuth.Credentials("openai", CancellationToken.None, true);
            Assert.True(new OAuth().Status("openai").Status == "connected", "successful retry clears persisted refresh failure");
        }
        finally { Files.Root = prior; Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Subscription_catalog_refreshes_and_filters_hidden_models()
    {
        string prior = Files.Root;
        string root = Path.Combine(Path.GetTempPath(), "tohotopia-live-catalog-" + Guid.NewGuid().ToString("N"));
        Files.Root = root;
        Directory.CreateDirectory(root);
        try
        {
            var transport = new FixtureTransport();
            var fixtureToken = new Token("fixture-old", "fixture-refresh", DateTimeOffset.UtcNow.AddHours(1), "fixture-account", "fixture-client", "chatgpt.tokens.use.direct");
            File.WriteAllBytes(CredentialPaths.File(root, "openai.token"), ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(fixtureToken, HostJson.Type<Token>(HostJson.Plain)), null, DataProtectionScope.CurrentUser));
            File.WriteAllText(Path.Combine(root, "models-dev.json"), "{}");
            var restoredAuth = new OAuth(new HttpClient(transport));
            var catalogTransport = new CatalogTransport();
            using var catalogHttp = new HttpClient(catalogTransport);
            int refreshBefore = transport.RefreshCalls;
            var liveModels = await new ModelCatalog(restoredAuth, catalogHttp).Models("openai", CancellationToken.None);
            Assert.True(catalogTransport.Calls == 2 && transport.RefreshCalls == refreshBefore + 1, "subscription model catalog refreshes and retries 401 once");
            Assert.True(liveModels.Count == 1 && liveModels[0].Id == "visible-model", "subscription catalog excludes hidden models and retains account model IDs");
        }
        finally { Files.Root = prior; Directory.Delete(root, true); }
    }
}
