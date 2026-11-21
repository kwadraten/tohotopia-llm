using System.Text.Json;
using Tohotopia;
using TohotopiaShared;
using Xunit;

public class CommunicationTests
{
    [Fact]
    public async Task Out_of_turn_communication_contract()
    {
        Assert.True(ChatActions.CanRelayTeam(2, 2) && !ChatActions.CanRelayTeam(2, 3)
            && !ChatActions.CanRelayTeam(0, 0),
            "native team chat can reach the same-team human host even without another human teammate");
        var schedule = new AmbientSchedule();
        Assert.True(!schedule.Due(7, "", 100) && schedule.Due(7, "message-1", 100), "only a message wakes AI outside its turn");
        schedule.Mark(7, "message-1", 100);
        Assert.True(!schedule.Due(7, "message-1", 105) && !schedule.Due(7, "new-chat", 105)
            && schedule.Due(7, "new-chat", 111), "new ally message triggers bounded proactive inspection");
        schedule.Mark(7, "new-chat", 111);
        Assert.True(!schedule.Due(7, "new-chat", 120) && !schedule.Due(7, "new-chat", 172) && schedule.Due(7, "newer-chat", 172),
            "no periodic wakeup without a new message");
        var snapshotTime = DateTimeOffset.UtcNow;
        var snapshot = new VisibleSnapshot("session", 42, 7, snapshotTime, JsonDocument.Parse("""{"selfId":7}""").RootElement.Clone());
        Assert.True(snapshot.Matches("session", 42, 7, snapshotTime) && !snapshot.Matches("session", 42, 8, snapshotTime)
            && !snapshot.Matches("other", 42, 7, snapshotTime) && !snapshot.Matches("session", 42, 7, snapshotTime.AddSeconds(21)),
            "live visibility tool rejects another player, another match and stale snapshots");

        string prior = Files.Root;
        string root = Path.Combine(Path.GetTempPath(), "tohotopia-communication-check-" + Guid.NewGuid().ToString("N"));
        Files.Root = root;
        Directory.CreateDirectory(root);
        try
        {
            RoomFiles.Write(root, "room.json", new RoomConfig { NativeLobby = true,
                AIs = [new() { Provider = "openai", Model = "fixture" }] });
            var observation = JsonDocument.Parse("""{"selfId":7,"grids":[{"id":3}],"chat":{"availableChannels":["team"],"recentMessages":[{"messageId":"m1","channel":"team","text":"需要支援"}]}}""").RootElement.Clone();
            var request = new CommunicationRequest("comm-1", "session", 42, 7, 2, 1, observation, ["team"], "civilization rules");
            var client = new FakeClient { Response = """{"requestId":"comm-1","channel":"team","message":"  我会支援北侧。  ","memory":"与盟友协防"}""" };
            var director = new Director(new OAuth(), (_, _) => client);
            var reply = await director.Communicate(request, CancellationToken.None);
            Assert.True(reply.Channel == "team" && reply.Message == "我会支援北侧。", "out-of-turn communication can request team coordination");
            Assert.True(client.LastPrompt.Contains("需要支援") && client.LastPrompt.Contains("inspect_visible_state")
                && client.LastPrompt.Contains("文明") && client.LastPrompt.Contains("行动回合")
                && client.LastPrompt.Contains("不受本回合剩余行动额度限制"),
                "communication prompt includes ally message, visibility tool and no-turn restrictions");
            var decision = new DecisionRequest("session-decision", 42, 7, 2, 0, observation,
                [new("a0", "UnitMove", 1, 0, 3, 0, 0, 0, "move"), new("end_turn", "EndTurn", 0, 0, 0, 0, 0, 0, "end")],
                "rules", AiSlot: 1, SessionId: "session");
            client.Response = """{"requestId":"session-decision","candidateId":"a0","reason":"move","memory":"本局专属记忆"}""";
            await director.Decide(decision, CancellationToken.None);
            Assert.True(client.LastPrompt.Contains("与盟友协防"), "communication and decisions share memory within a match");
            int callsBeforeNewMatch = client.Calls;
            await director.Decide(decision with { SessionId = "new-session" }, CancellationToken.None);
            Assert.True(client.Calls == callsBeforeNewMatch + 1 && !client.LastPrompt.Contains("本局专属记忆")
                && !client.LastPrompt.Contains("与盟友协防"), "reused map, player and request IDs cannot leak memory or cached decisions into a new match");
            client.Response = """{"requestId":"new-comm","channel":"","message":""}""";
            await director.Communicate(request with { RequestId = "new-comm", SessionId = "third-session" }, CancellationToken.None);
            Assert.True(!client.LastPrompt.Contains("本局专属记忆") && !client.LastPrompt.Contains("与盟友协防"),
                "new-match communication cannot read previous match memory");
            client.Response = """{"requestId":"public-ok","channel":"public","message":"愿意谈判吗？"}""";
            var publicReply = await director.Communicate(request with { RequestId = "public-ok", AvailableChannels = ["public"] }, CancellationToken.None);
            Assert.True(publicReply.Channel == "public" && publicReply.Message == "愿意谈判吗？", "public diplomacy is accepted only when offered");
            client.Response = "```json\n{\"requestId\":\"repair-ok\",\"channel\":\"public\",\"message\":\"你好\",}\n```";
            int beforeRepair = client.Calls;
            var repaired = await director.Communicate(request with { RequestId = "repair-ok", AvailableChannels = ["public"] }, CancellationToken.None);
            Assert.True(repaired.Message == "你好" && client.Calls == beforeRepair + 1, "JSON fences and trailing comma are repaired without regeneration");
            client.Responses.Enqueue("{ broken json");
            client.Responses.Enqueue("""{"requestId":"retry-ok","channel":"public","message":"重新回应"}""");
            int beforeRetry = client.Calls;
            var retried = await director.Communicate(request with { RequestId = "retry-ok", AvailableChannels = ["public"] }, CancellationToken.None);
            Assert.True(retried.Message == "重新回应" && client.Calls == beforeRetry + 2, "unrepairable JSON triggers model regeneration");
            async Task Reject(string id, string channel, string message, string[] allowed)
            {
                client.Response = JsonSerializer.Serialize(new AmbientReply(id, channel, message), Files.Json);
                try { await director.Communicate(request with { RequestId = id, AvailableChannels = allowed }, CancellationToken.None); }
                catch (ArgumentException) { return; }
                catch (InvalidOperationException) { return; }
                throw new Exception("Invalid communication accepted: " + id);
            }
            await Reject("public", "public", "hello", ["team"]);
            await Reject("not-allied", "team", "hello", []);
            await Reject("empty", "team", " ", ["team"]);
            await Reject("message-without-channel", "", "hello", ["team"]);
        }
        finally { Files.Root = prior; Directory.Delete(root, true); }
    }
}
