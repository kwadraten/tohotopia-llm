using System.Text.Json;
using Tohotopia;
using TohotopiaShared;

static class ActionPlanChecks
{
    internal static async Task Run()
    {
        void Check(bool value, string message)
        {
            if (!value) throw new Exception(message);
            Console.WriteLine("PASS " + message);
        }
        Files.Root = Path.Combine(Path.GetTempPath(), "tohotopia-plan-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Files.Root);
        var observation = JsonSerializer.Deserialize<JsonElement>("""{"grids":[]}""");
        var request = new DecisionRequest("plan", 1, 330, 2, 0, observation,
            [new("a0", "UnitMove", 1, 0, 2, 0, 0, 0, "move"),
             new("a1", "Build", 1, 0, 2, 0, 0, 0, "build"),
             new("end_turn", "EndTurn", 0, 0, 0, 0, 0, 0, "end")], "rules", Feedback: "a0 因金币不足失败；剩余步骤 a1");
        var client = new FakeClient
        {
            Response = """{"requestId":"plan","actions":[{"candidateId":"a0"},{"candidateId":"a1"},{"candidateId":"end_turn"}],"reason":"batch"}"""
        };
        var director = new Director(new OAuth(), (_, _) => client);
        var chosen = await director.Decide(request, CancellationToken.None);
        Check(chosen.Steps.Select(s => s.CandidateId).SequenceEqual(["a0", "a1", "end_turn"]), "model can return an ordered action sequence");
        using (var wire = JsonDocument.Parse(JsonSerializer.Serialize(chosen, Files.Json)))
        {
            var gameSteps = wire.RootElement.GetProperty("actions").Deserialize<List<PlannedStep>>(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
            Check(gameSteps.Select(s => s.CandidateId).SequenceEqual(["a0", "a1", "end_turn"]), "game client receives the complete ordered sequence over JSON");
        }
        var plan = new ActionPlan();
        plan.Load(chosen.Steps);
        Check(plan.Current.CandidateId == "a0", "plan starts with its first candidate");
        plan.Advance();
        string report = plan.Block("资源不足");
        Check(report.Contains("a1") && report.Contains("资源不足") && report.Contains("end_turn") && !plan.Active,
            "blocked step clears the plan and reports remaining unexecuted steps");
        Check(client.LastPrompt.Contains("金币不足失败") && client.LastPrompt.Contains("剩余步骤 a1"), "obstruction feedback is included in the next model request");
        client.Response = """{"requestId":"replan","actions":[{"candidateId":"a1"}],"reason":"retry"}""";
        await director.Decide(request with { RequestId = "replan", Feedback = report }, CancellationToken.None);
        Check(client.LastPrompt.Contains(report) && client.LastPrompt.Contains("a1"), "blocked sequence continues with feedback and refreshed candidates");
        async Task Reject(string id, string actions)
        {
            client.Response = $$"""{"requestId":"{{id}}","actions":{{actions}},"reason":"invalid"}""";
            try { await director.Decide(request with { RequestId = id }, CancellationToken.None); }
            catch (InvalidOperationException) { return; }
            throw new Exception("Invalid action plan accepted: " + id);
        }
        await Reject("empty", "[]");
        await Reject("unknown", """[{"candidateId":"a0"},{"candidateId":"invented"}]""");
        await Reject("early-end", """[{"candidateId":"end_turn"},{"candidateId":"a0"}]""");
        await Reject("message-on-action", """[{"candidateId":"a0","message":"unwanted"}]""");
        await Reject("oversized", "[" + string.Join(",", Enumerable.Repeat("""{"candidateId":"a0"}""", ActionPlan.MaxSteps + 1)) + "]");
        Check(true, "empty, unknown, premature end-turn and oversized plans are rejected");
        client.Response = """{"requestId":"limited","actions":[{"candidateId":"a0"},{"candidateId":"a1"}],"reason":"too many"}""";
        try { await director.Decide(request with { RequestId = "limited", Sequence = 79 }, CancellationToken.None); throw new Exception("Plan exceeded remaining turn budget"); }
        catch (InvalidOperationException) { Check(true, "plan cannot exceed remaining action budget"); }
        client.Response = """{"requestId":"plan-chat","actions":[{"candidateId":"chat_public","message":"  一起防守。  "},{"candidateId":"a0"}],"reason":"coordinate"}""";
        var chat = await director.Decide(request with
        {
            RequestId = "plan-chat",
            Candidates = [.. request.Candidates, new("chat_public", TohotopiaShared.ChatActions.Kind("chat_public"), 0, 0, 0, 0, 0, 0, "public")]
        }, CancellationToken.None);
        Check(chat.Steps[0].Message == "一起防守。" && chat.Steps[1].Message == "", "chat message is attached to its step and trimmed within a plan");
    }
}
