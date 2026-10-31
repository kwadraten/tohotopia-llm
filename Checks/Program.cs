using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Tohotopia;
if (args.Length == 2 && args[0] == "--rules-snapshot") { RulesSnapshotChecks.Run(args[1]); return; }
if (args.Contains("--native-seats")) { NativeAiSeatChecks.Run(); return; }
if (args.Contains("--action-plan")) { await ActionPlanChecks.Run(); return; }
if (args.Contains("--communication")) { await CommunicationChecks.Run(); return; }
if (args.Contains("--provider-connections")) { await ProviderConnectionChecks.Run(); return; }
await ProviderConnectionChecks.Run();
Files.Root = Path.Combine(Path.GetTempPath(), "tohotopia-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Files.Root);
RoomFileChecks.Run(Files.Root);
OutputTokenChecks.Run(Files.Root);
Files.Save(new AgentConfig());
await ChatChecks.Run();
SpatialTableChecks.Run();
RulePromptChecks.Run();
SubscriptionReplayChecks.Run();
var observation = JsonSerializer.Deserialize<JsonElement>("""{"selfId":330,"grids":[{"id":245,"x":2,"y":3,"terrain":"Land","resource":"Fruit","building":"None"}]}""");
var r = new DecisionRequest("test-1", 1, 330, 2, 0, observation, new() { new("a0", "UnitMove", 355, 0, 245, 0, 0, 0, "move"), new("end_turn", "EndTurn", 0, 0, 0, 0, 0, 0, "end") }, "帝国文明测试规则");
void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
var fallback = new TohotopiaMod.ModelTurnFallback();
fallback.Fail(330, 2);
Check(fallback.IsNative(330, 2) && !fallback.IsNative(331, 2), "failed AI turn falls back without disabling another seat");
fallback.Fail(331, 2);
Check(fallback.IsNative(330, 2) && fallback.IsNative(331, 2) && !fallback.IsNative(330, 3), "multiple failures remain isolated and next turn retries models");
fallback.Clear();
Check(!fallback.IsNative(330, 2) && !fallback.IsNative(331, 2), "new match or manual restart clears native fallback turns");
var isolatedClient = new FakeClient { Response = "" };
var isolatedDirector = new Director(new OAuth(), (_, _) => isolatedClient);
bool firstFailed = false;
try { await isolatedDirector.Decide(r with { RequestId = "isolation-fail" }, CancellationToken.None); }
catch (InvalidOperationException) { firstFailed = true; }
isolatedClient.Response = """{"requestId":"isolation-second","candidateId":"a0","reason":"second seat"}""";
var secondDecision = await isolatedDirector.Decide(r with { PlayerId = 331, RequestId = "isolation-second" }, CancellationToken.None);
Check(firstFailed && secondDecision.CandidateId == "a0", "first seat model error does not block the second seat decision");
isolatedClient.Response = """{"requestId":"isolation-retry","candidateId":"a0","reason":"retry next turn"}""";
var retryDecision = await isolatedDirector.Decide(r with { Turn = 3, RequestId = "isolation-retry" }, CancellationToken.None);
Check(retryDecision.CandidateId == "a0", "failed seat can make a valid decision on its next turn");
var fake = new FakeClient();
var director = new Director(new OAuth(), (_, _) => fake);
var d = await director.Decide(r, CancellationToken.None);
Check(d.CandidateId == "a0", "Microsoft Agent Framework agent selects candidate");
Check(fake.LastPrompt.Contains("帝国文明测试规则") && fake.LastPrompt.Contains("TURN=2") && fake.LastPrompt.Contains("UnitMove"), "system civilization rules, state, candidates injected");
Check(fake.LastPrompt.Contains("已尝试 0/80 个动作") && fake.LastPrompt.Contains("本次最多规划 12 个动作") && fake.LastPrompt.Contains("总时限 120 秒"), "every model request includes time and remaining turn action budgets");
var budgetClient = new FakeClient();
var budgetDirector = new Director(new OAuth(), (_, _) => budgetClient);
var budgetEnd = await budgetDirector.Decide(r with { RequestId = "budget-exhausted", Sequence = 80 }, CancellationToken.None);
Check(budgetEnd.CandidateId == "end_turn" && budgetClient.Calls == 0, "exhausted turn decision budget ends turn without another model request");
var again = await director.Decide(r, CancellationToken.None);
Check(fake.Calls == 1, "duplicate request does not invoke model twice");
fake.Response = """{"requestId":"wrong","candidateId":"a0","reason":"stale"}""";
try { await director.Decide(r with { RequestId = "test-2" }, CancellationToken.None); throw new Exception("Stale reply accepted"); } catch (InvalidOperationException) { Console.WriteLine("PASS reject stale decision"); }
fake.Response = """{"requestId":"test-3","candidateId":"invented","reason":"bad"}""";
try { await director.Decide(r with { RequestId = "test-3" }, CancellationToken.None); throw new Exception("Invented action accepted"); } catch (InvalidOperationException) { Console.WriteLine("PASS reject invented candidate"); }
Check(Director.MapTable(observation).Contains("格245") && Director.MapTable(observation).Contains("Land Fruit") && !Director.MapTable(observation).Contains("None"), "map table renders explored cells without empty layers");
try { Director.Validate(r with { Candidates = new() { r.Candidates[0] } }); throw new Exception("Missing end accepted"); } catch (ArgumentException) { Console.WriteLine("PASS require end-turn option"); }
Console.WriteLine("All checks passed.");
var explored = new HashSet<uint> { 245 };
var visible = new HashSet<uint> { 355 };
var known = new HashSet<uint> { 330 };
Check(!TohotopiaMod.Visibility.Unit(331, 330, 246, false, explored), "unexplored enemy excluded");
Check(!TohotopiaMod.Visibility.Unit(331, 330, 245, true, explored), "hidden enemy excluded on explored tile");
Check(TohotopiaMod.Visibility.Unit(330, 330, 246, true, explored), "own unit remains observable");
Check(!TohotopiaMod.Visibility.Candidate("UnitAttack", 245, 999, 0, 0, explored, visible, known), "candidate cannot leak invisible target unit");
Check(!TohotopiaMod.Visibility.Candidate("UnitAttackGround", 245, 0, 246, 0, explored, visible, known), "candidate cannot leak unexplored attack target");
Check(!TohotopiaMod.Visibility.Candidate("PlayerAction", 0, 0, 0, 331, explored, visible, known), "candidate cannot target unmet player");
Check(TohotopiaMod.Visibility.Candidate("UnitMove", 246, 0, 0, 0, explored, visible, known), "exploration move allowed without exporting hidden terrain");
Check(OAuth.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk") == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", "PKCE RFC 7636 challenge");
Check(OAuth.ParseCallback("http://localhost:1455/auth/callback?code=abc&state=xyz") == ("abc", "xyz"), "parse browser callback");
Check(OAuth.ParseCallback("abc#xyz") == ("abc", "xyz"), "parse manual authorization code");
string body = JsonSerializer.Serialize(SubscriptionClient.OpenAIBody(new AgentConfig(), new[] { new ChatMessage(ChatRole.System, "rules"), new ChatMessage(ChatRole.User, "state"), new ChatMessage(ChatRole.Assistant, "decision") }));
Check(body.Contains("rules") && body.Contains("input_text") && body.Contains("output_text") && body.Contains("\"store\":false") && body.Contains("\"tools\":[]"), "direct subscription Responses request has instructions and no tools");
string sse = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"ok\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n";
Check(await SubscriptionClient.ReadResponses(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sse)), CancellationToken.None) == "ok", "parse complete subscription SSE");
string itemSse = "data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"item-final\"}]}}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n";
Check(await SubscriptionClient.ReadResponses(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(itemSse)), CancellationToken.None) == "item-final", "completed item text survives an empty final output snapshot");
try { await SubscriptionClient.ReadResponses(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n")), CancellationToken.None); throw new Exception("Truncated response accepted"); } catch (InvalidOperationException) { Console.WriteLine("PASS reject truncated subscription SSE"); }
var transport = new FixtureTransport();
var oauthFixture = new OAuth(new HttpClient(transport));
var fixtureToken = new Token("fixture-old", "fixture-refresh", DateTimeOffset.UtcNow.AddHours(1), "fixture-account", "fixture-client", "chatgpt.tokens.use.direct");
File.WriteAllBytes(Path.Combine(Files.Root, "openai.token"), System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fixtureToken)), null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
using (var direct = new SubscriptionClient(new AgentConfig(), oauthFixture, new HttpClient(transport))) { var response = await direct.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "fixture-state") }, new ChatOptions { Instructions = "fixture-rules" }); Check(response.Text == "fixture-choice" && transport.ModelCalls == 2 && transport.RefreshCalls == 1, "direct HTTP retries 401 once after OAuth refresh"); Check(transport.LastBody.Contains("fixture-rules") && transport.LastBody.Contains("fixture-state"), "Agent Framework instructions reach direct HTTP transport"); }
var auth = new OAuth();
var manual = await auth.Start("anthropic", CancellationToken.None);
try { await auth.Complete(manual.Id, "abc#wrong-state", CancellationToken.None); throw new Exception("Wrong OAuth state accepted"); } catch (ArgumentException) { Console.WriteLine("PASS reject mismatched OAuth state before HTTP exchange"); }
new TohotopiaShared.RoomConfig { AIs = Enumerable.Range(0, 5).Select(i => new TohotopiaShared.AISeat { Model = "fixture", Civilization = (uint)i }).ToList() }.Validate();
Console.WriteLine("PASS room accepts five AI seats plus human");
try { new TohotopiaShared.RoomConfig { AIs = Enumerable.Range(0, 6).Select(i => new TohotopiaShared.AISeat()).ToList() }.Validate(); throw new Exception("Six AI seats accepted"); } catch (ArgumentException) { Console.WriteLine("PASS reject more than five AI seats"); }
TohotopiaShared.RoomFiles.Write(Files.Root, "room.json", new TohotopiaShared.RoomConfig { AIs = new() { new() { Provider = "openai", Model = "model-a" }, new() { Provider = "grok", Model = "model-b", Reasoning = "high" } } });
AgentConfig? selectedConfig = null;
var seatFake = new FakeClient { Response = """{"requestId":"seat-test","candidateId":"a0","reason":"seat"}""" };
var seatDirector = new Director(new OAuth(), (c, _) => { selectedConfig = c; return seatFake; });
await seatDirector.Decide(r with { RequestId = "seat-test", AiSlot = 2 }, CancellationToken.None);
Check(selectedConfig?.Provider == "grok" && selectedConfig.Model == "model-b" && selectedConfig.Reasoning == "high", "each AI seat selects its own provider/model/reasoning");
Check(new TohotopiaShared.AISeat { Model = "model-b" }.Name == "model-b", "AI player name follows model name");
using (var catalog = JsonDocument.Parse("""{"models":{"fixture-reasoner":{"name":"Fixture Reasoner","reasoning":true,"reasoning_options":[{"type":"effort","values":["low","ultra"]}],"modalities":{"output":["text"]}},"fixture-image":{"modalities":{"output":["image","text"]}},"fixture-plain":{"reasoning":false,"modalities":{"output":["text"]}}}}"""))
{
    var parsed = ModelCatalog.ParseModels(catalog.RootElement);
    Check(parsed.Count == 2 && !parsed.Any(m => m.Id == "fixture-image"), "catalog excludes image generation models");
    Check(parsed.Single(m => m.Id == "fixture-reasoner").Efforts.SequenceEqual(new[] { "low", "ultra" }), "reasoning options follow models.dev without hardcoded levels");
    Check(parsed.Single(m => m.Id == "fixture-plain").Efforts.Length == 0, "catalog does not invent reasoning options");
}
Check(!JsonSerializer.Serialize(SubscriptionClient.OpenAIBody(new AgentConfig { Reasoning = "default" }, new[] { new ChatMessage(ChatRole.User, "state") })).Contains("\"reasoning\""), "default reasoning omits unsupported parameter");
Check(ModelCatalog.Driver("xai") == "grok" && ModelCatalog.Driver("kimi-code-plan-global") == "kimi" && ModelCatalog.Driver("kimi-code-plan-cn") == "kimi-code-plan-cn", "catalog provider IDs map only to implemented subscription adapters");
var restoredAuth = new OAuth(new HttpClient(transport));
var restoredToken = await restoredAuth.Credentials("openai", CancellationToken.None);
Check(restoredToken.Access == "fixture-new" && restoredToken.Refresh == "fixture-rotated" && restoredToken.ClientId == "fixture-client" && restoredToken.Scope.Contains("chatgpt.tokens.use.direct"), "new OAuth instance restores encrypted token, rotated refresh and client registration");
Check(restoredAuth.Status("openai").Connected && restoredAuth.Status("openai").RefreshAvailable, "account management restores persisted subscription status");
Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(Files.Root, "openai.token"))).Contains("fixture-new"), "persistent credential file does not contain plaintext token");
var tokenBytesBefore = File.ReadAllBytes(Path.Combine(Files.Root, "openai.token"));
transport.FailRefresh = true;
try { await restoredAuth.Credentials("openai", CancellationToken.None, true); throw new Exception("Refresh failure accepted"); } catch (InvalidOperationException) { Console.WriteLine("PASS refresh failure is reported"); }
transport.FailRefresh = false;
Check(File.ReadAllBytes(Path.Combine(Files.Root, "openai.token")).SequenceEqual(tokenBytesBefore) && new OAuth().Status("openai").Status == "refresh_failed", "refresh failure preserves credentials and diagnostic state across restart");
await restoredAuth.Credentials("openai", CancellationToken.None, true);
Check(new OAuth().Status("openai").Status == "connected", "successful retry clears persisted refresh failure");
File.WriteAllText(Path.Combine(Files.Root, "models-dev.json"), "{}");
var catalogTransport = new CatalogTransport();
using (var catalogHttp = new HttpClient(catalogTransport))
{
    int refreshBefore = transport.RefreshCalls;
    var liveModels = await new ModelCatalog(restoredAuth, catalogHttp).Models("openai", CancellationToken.None);
    Check(catalogTransport.Calls == 2 && transport.RefreshCalls == refreshBefore + 1, "subscription model catalog refreshes and retries 401 once");
    Check(liveModels.Count == 1 && liveModels[0].Id == "visible-model", "subscription catalog excludes hidden models and retains account model IDs");
}
TohotopiaShared.AgentEvents.Write(Files.Root, new(DateTimeOffset.UtcNow, "game", "executed", "fixture action\nsecond line", AiSlot: 2, PlayerId: 330, Turn: 2, RequestId: "seat-test", CandidateId: "a0"));
var events = Directory.GetFiles(Path.Combine(Files.Root, "logs"), "*.jsonl").SelectMany(File.ReadAllLines).Select(line => JsonSerializer.Deserialize<TohotopiaShared.AgentEvent>(line, TohotopiaShared.RoomFiles.Json)!).ToList();
Check(events.Any(e => e.Stage == "model_decision" && e.AiSlot == 2 && e.Model == "model-b") && events.Any(e => e.Stage == "executed" && e.RequestId == "seat-test" && e.CandidateId == "a0"), "decision and game execution logs retain agent and request correlation");
Check(events.Any(e => e.Stage == "model_failed") && events.Single(e => e.Source == "game").Message.Contains('\n'), "failed decisions and multiline messages persist as valid JSONL events");
Check(!string.Join("", Directory.GetFiles(Path.Combine(Files.Root, "logs")).Select(File.ReadAllText)).Contains("fixture-refresh") && !string.Join("", Directory.GetFiles(Path.Combine(Files.Root, "logs")).Select(File.ReadAllText)).Contains("fixture-new"), "diagnostic logs exclude authentication tokens");
sealed class FixtureTransport : HttpMessageHandler
{
    public int ModelCalls, RefreshCalls; public string LastBody = ""; public bool FailRefresh;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri!.AbsolutePath == "/api/accounts/oauth/token")
        {
            RefreshCalls++;
            if (FailRefresh)
                return new(System.Net.HttpStatusCode.BadRequest);
            return new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"access_token\":\"fixture-new\",\"refresh_token\":\"fixture-rotated\",\"expires_in\":3600}")
            };
        }
        if (request.RequestUri.AbsoluteUri != "https://api.openai.com/v1/responses")
            throw new Exception("Unexpected endpoint");
        ModelCalls++;
        LastBody = await request.Content!.ReadAsStringAsync(ct);
        if (ModelCalls == 1)
            return new(System.Net.HttpStatusCode.Unauthorized);
        if (request.Headers.Authorization?.Parameter != "fixture-new")
            throw new Exception("Refresh credential not applied");
        if (request.Headers.Contains("originator") || request.Headers.Contains("version") || request.Headers.Contains("ChatGPT-Account-Id"))
            throw new Exception("Codex headers must not accompany direct subscription tokens");
        return new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"type\":\"response.completed\",\"response\":{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"fixture-choice\"}]}]}}\n\n")
        };
    }
}
sealed class CatalogTransport : HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri!.AbsoluteUri != "https://api.openai.com/v1/models" || request.Headers.Contains("originator") || request.Headers.Contains("ChatGPT-Account-Id"))
            throw new Exception("Model catalog must use the direct subscription route");
        Calls++;
        return Task.FromResult(new HttpResponseMessage(Calls == 1 ? System.Net.HttpStatusCode.Unauthorized : System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"models":[{"slug":"visible-model","visibility":"list"},{"slug":"hidden-model","visibility":"hidden"}]}""")
        });
    }
}
sealed class FakeClient : IChatClient
{
    public Queue<string> Responses = new();
    public int Calls; public string LastPrompt = "", Response = """{"requestId":"test-1","candidateId":"a0","reason":"expand"}""";
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
    {
        Calls++;
        LastPrompt = (o?.Instructions ?? "") + string.Join("\n", m.Select(x => x.Text));
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Responses.Count > 0 ? Responses.Dequeue() : Response)));
    }
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield return new(ChatRole.Assistant, Response);
    }
    public object? GetService(Type t, object? key = null) => null; public void Dispose()
    {
    }
}
