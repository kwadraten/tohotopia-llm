using System.Text.Json;
using Microsoft.Extensions.AI;
using Tohotopia;
using TohotopiaShared;
using Xunit;

public class DirectorTests
{
    static readonly JsonElement Observation = JsonDocument.Parse("""{"selfId":330,"grids":[{"id":245,"x":2,"y":3,"terrain":"Land","resource":"Fruit","building":"None"}]}""").RootElement.Clone();
    static DecisionRequest Request => new("test-1", 1, 330, 2, 0, Observation,
        new() { new("a0", "UnitMove", 355, 0, 245, 0, 0, 0, "move"), new("end_turn", "EndTurn", 0, 0, 0, 0, 0, 0, "end") }, "帝国文明测试规则", SessionId: "test-match");

    [Fact]
    public void Native_turn_fallback_isolated_per_seat_and_cleared()
    {
        var fallback = new TohotopiaMod.ModelTurnFallback();
        fallback.Fail(330, 2);
        Assert.True(fallback.IsNative(330, 2) && !fallback.IsNative(330, 3) && !fallback.IsNative(331, 2), "failed AI turn falls back without disabling another seat");
        fallback.Fail(331, 2);
        Assert.True(fallback.IsNative(330, 2) && fallback.IsNative(331, 2) && !fallback.IsNative(330, 3), "multiple failures remain isolated and next turn retries models");
        fallback.Clear();
        Assert.True(!fallback.IsNative(330, 2) && !fallback.IsNative(331, 2), "new match or manual restart clears native fallback turns");
    }

    [Fact]
    public async Task Seat_failures_do_not_block_other_seats_or_next_turn()
    {
        var isolatedClient = new FakeClient { Response = "" };
        var isolatedDirector = new Director(new OAuth(), (_, _) => isolatedClient);
        await Assert.ThrowsAsync<InvalidOperationException>(() => isolatedDirector.Decide(Request with { RequestId = "isolation-fail" }, CancellationToken.None));
        isolatedClient.Response = """{"requestId":"isolation-second","candidateId":"a0","reason":"second seat"}""";
        var secondDecision = await isolatedDirector.Decide(Request with { PlayerId = 331, RequestId = "isolation-second" }, CancellationToken.None);
        Assert.True(secondDecision.CandidateId == "a0", "first seat model error does not block the second seat decision");
        isolatedClient.Response = """{"requestId":"isolation-retry","candidateId":"a0","reason":"retry next turn"}""";
        var retryDecision = await isolatedDirector.Decide(Request with { Turn = 3, RequestId = "isolation-retry" }, CancellationToken.None);
        Assert.True(retryDecision.CandidateId == "a0", "failed seat can make a valid decision on its next turn");
    }

    [Fact]
    public async Task Decide_injects_state_budgets_and_rejects_invalid_replies()
    {
        var fake = new FakeClient();
        var director = new Director(new OAuth(), (_, _) => fake);
        var d = await director.Decide(Request, CancellationToken.None);
        Assert.True(d.CandidateId == "a0", "Microsoft Agent Framework agent selects candidate");
        Assert.True(fake.LastPrompt.Contains("帝国文明测试规则") && fake.LastPrompt.Contains("TURN=2") && fake.LastPrompt.Contains("UnitMove"), "system civilization rules, state, candidates injected");
        Assert.True(fake.LastPrompt.Contains("已尝试 0/80 个动作") && fake.LastPrompt.Contains("本次最多规划 12 个动作") && fake.LastPrompt.Contains("总时限 120 秒"), "every model request includes time and remaining turn action budgets");
        var budgetClient = new FakeClient();
        var budgetDirector = new Director(new OAuth(), (_, _) => budgetClient);
        var budgetEnd = await budgetDirector.Decide(Request with { RequestId = "budget-exhausted", Sequence = 80 }, CancellationToken.None);
        Assert.True(budgetEnd.CandidateId == "end_turn" && budgetClient.Calls == 0, "exhausted turn decision budget ends turn without another model request");
        await director.Decide(Request, CancellationToken.None);
        Assert.True(fake.Calls == 1, "duplicate request does not invoke model twice");
        fake.Response = """{"requestId":"wrong","candidateId":"a0","reason":"stale"}""";
        await Assert.ThrowsAsync<InvalidOperationException>(() => director.Decide(Request with { RequestId = "test-2" }, CancellationToken.None));
        fake.Response = """{"requestId":"test-3","candidateId":"invented","reason":"bad"}""";
        await Assert.ThrowsAsync<InvalidOperationException>(() => director.Decide(Request with { RequestId = "test-3" }, CancellationToken.None));
        Assert.True(Director.MapTable(Observation).Contains("格245") && Director.MapTable(Observation).Contains("Land Fruit") && !Director.MapTable(Observation).Contains("None"), "map table renders explored cells without empty layers");
        Assert.Throws<ArgumentException>(() => Director.Validate(Request with { Candidates = new() { Request.Candidates[0] } }));
    }

    [Fact]
    public void Visibility_hides_unseen_game_state()
    {
        var explored = new HashSet<uint> { 245 };
        var visible = new HashSet<uint> { 355 };
        var known = new HashSet<uint> { 330 };
        Assert.False(TohotopiaMod.Visibility.Unit(331, 330, 246, false, explored), "unexplored enemy excluded");
        Assert.False(TohotopiaMod.Visibility.Unit(331, 330, 245, true, explored), "hidden enemy excluded on explored tile");
        Assert.True(TohotopiaMod.Visibility.Unit(330, 330, 246, true, explored), "own unit remains observable");
        Assert.False(TohotopiaMod.Visibility.Candidate("UnitAttack", 245, 999, 0, 0, explored, visible, known), "candidate cannot leak invisible target unit");
        Assert.False(TohotopiaMod.Visibility.Candidate("UnitAttackGround", 245, 0, 246, 0, explored, visible, known), "candidate cannot leak unexplored attack target");
        Assert.False(TohotopiaMod.Visibility.Candidate("PlayerAction", 0, 0, 0, 331, explored, visible, known), "candidate cannot target unmet player");
        Assert.True(TohotopiaMod.Visibility.Candidate("UnitMove", 246, 0, 0, 0, explored, visible, known), "exploration move allowed without exporting hidden terrain");
    }

    [Fact]
    public void Room_seat_limits()
    {
        new RoomConfig { AIs = Enumerable.Range(0, 5).Select(i => new AISeat { Model = "fixture", Civilization = (uint)i }).ToList() }.Validate();
        Assert.Throws<ArgumentException>(() => new RoomConfig { AIs = Enumerable.Range(0, 6).Select(i => new AISeat()).ToList() }.Validate());
        Assert.True(new AISeat { Model = "model-b" }.Name == "model-b", "AI player name follows model name");
    }

    [Fact]
    public async Task Ai_seat_provider_selection_and_ping()
    {
        string prior = Files.Root;
        string root = Path.Combine(Path.GetTempPath(), "tohotopia-seats-" + Guid.NewGuid().ToString("N"));
        Files.Root = root;
        Directory.CreateDirectory(root);
        try
        {
            RoomFiles.Write(root, "room.json", new RoomConfig { AIs = new() { new() { Provider = "openai", Model = "model-a" }, new() { Provider = "grok", Model = "model-b", Reasoning = "high" } } });
            AgentConfig? selectedConfig = null;
            var seatFake = new FakeClient { Response = """{"requestId":"seat-test","candidateId":"a0","reason":"seat"}""" };
            var seatDirector = new Director(new OAuth(), (c, _) => { selectedConfig = c; return seatFake; });
            await seatDirector.Decide(Request with { RequestId = "seat-test", AiSlot = 2 }, CancellationToken.None);
            Assert.True(selectedConfig?.Provider == "grok" && selectedConfig.Model == "model-b" && selectedConfig.Reasoning == "high", "each AI seat selects its own provider/model/reasoning");
            AgentConfig? pingConfig = null;
            var pingClient = new FakeClient { Response = "Pong!" };
            var pingDirector = new Director(new OAuth(), (c, _) => { pingConfig = c; return pingClient; });
            var ping = await pingDirector.Ping("xai", new ModelCatalog(new OAuth()), CancellationToken.None);
            Assert.True(ping.Success && ping.Reply == "Pong!" && ping.Model == "model-b" && pingConfig?.Provider == "grok" && pingConfig.Reasoning == "high",
                "connectivity ping reuses the seat provider/model/reasoning and reports the reply");
            pingClient.Response = "";
            pingClient.Fail = true;
            var failedPing = await pingDirector.Ping("xai", new ModelCatalog(new OAuth()), CancellationToken.None);
            Assert.True(!failedPing.Success && failedPing.Error.Contains("fixture ping failed") && failedPing.Provider == "grok",
                "connectivity ping reports model errors such as HTTP 429 to the caller");
        }
        finally { Files.Root = prior; Directory.Delete(root, true); }
    }

    [Fact]
    public void Catalog_parsing_and_driver_mapping()
    {
        using var catalog = JsonDocument.Parse("""{"models":{"fixture-reasoner":{"name":"Fixture Reasoner","reasoning":true,"reasoning_options":[{"type":"effort","values":["low","ultra"]}],"modalities":{"output":["text"]}},"fixture-image":{"modalities":{"output":["image","text"]}},"fixture-plain":{"reasoning":false,"modalities":{"output":["text"]}}}}""");
        var parsed = ModelCatalog.ParseModels(catalog.RootElement);
        Assert.True(parsed.Count == 2 && !parsed.Any(m => m.Id == "fixture-image"), "catalog excludes image generation models");
        Assert.True(parsed.Single(m => m.Id == "fixture-reasoner").Efforts.SequenceEqual(new[] { "low", "ultra" }), "reasoning options follow models.dev without hardcoded levels");
        Assert.True(parsed.Single(m => m.Id == "fixture-plain").Efforts.Length == 0, "catalog does not invent reasoning options");
        Assert.True(!SubscriptionClient.OpenAIBody(new AgentConfig { Reasoning = "default" }, new[] { new ChatMessage(ChatRole.User, "state") }).ToJsonString().Contains("\"reasoning\""), "default reasoning omits unsupported parameter");
        Assert.True(ModelCatalog.Driver("xai") == "grok" && ModelCatalog.Driver("kimi-code-plan-global") == "kimi" && ModelCatalog.Driver("kimi-code-plan-cn") == "kimi-code-plan-cn", "catalog provider IDs map only to implemented subscription adapters");
    }

    [Fact]
    public async Task Diagnostic_events_retain_correlation_without_tokens()
    {
        string prior = Files.Root;
        string root = Path.Combine(Path.GetTempPath(), "tohotopia-events-" + Guid.NewGuid().ToString("N"));
        Files.Root = root;
        Directory.CreateDirectory(root);
        try
        {
            RoomFiles.Write(root, "room.json", new RoomConfig { AIs = new() { new() { Provider = "openai", Model = "model-a" }, new() { Provider = "grok", Model = "model-b", Reasoning = "high" } } });
            var seatFake = new FakeClient { Response = """{"requestId":"seat-test","candidateId":"a0","reason":"seat"}""" };
            var seatDirector = new Director(new OAuth(), (_, _) => seatFake);
            await seatDirector.Decide(Request with { RequestId = "seat-test", AiSlot = 2 }, CancellationToken.None);
            var failClient = new FakeClient { Response = "" };
            var failDirector = new Director(new OAuth(), (_, _) => failClient);
            await Assert.ThrowsAsync<InvalidOperationException>(() => failDirector.Decide(Request with { RequestId = "event-fail" }, CancellationToken.None));
            AgentEvents.Write(root, new(DateTimeOffset.UtcNow, "game", "executed", "fixture action\nsecond line", AiSlot: 2, PlayerId: 330, Turn: 2, RequestId: "seat-test", CandidateId: "a0"));
            var events = Directory.GetFiles(Path.Combine(root, "logs"), "*.jsonl").SelectMany(File.ReadAllLines).Select(line => JsonSerializer.Deserialize<AgentEvent>(line, RoomFiles.Json)!).ToList();
            Assert.True(events.Any(e => e.Stage == "model_decision" && e.AiSlot == 2 && e.Model == "model-b") && events.Any(e => e.Stage == "executed" && e.RequestId == "seat-test" && e.CandidateId == "a0"), "decision and game execution logs retain agent and request correlation");
            Assert.True(events.Any(e => e.Stage == "model_failed") && events.Single(e => e.Source == "game").Message.Contains('\n'), "failed decisions and multiline messages persist as valid JSONL events");
            string logs = string.Join("", Directory.GetFiles(Path.Combine(root, "logs")).Select(File.ReadAllText));
            Assert.True(!logs.Contains("fixture-refresh") && !logs.Contains("fixture-new"), "diagnostic logs exclude authentication tokens");
        }
        finally { Files.Root = prior; Directory.Delete(root, true); }
    }
}
