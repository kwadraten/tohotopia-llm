using System.Text.Json;
using Tohotopia;
using TohotopiaShared;

static class CommunicationChecks
{
    internal static async Task Run()
    {
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception(name);
            Console.WriteLine("PASS " + name);
        }
        Check(ChatActions.CanRelayTeam(2, 2) && !ChatActions.CanRelayTeam(2, 3)
            && !ChatActions.CanRelayTeam(0, 0),
            "native team chat can reach the same-team human host even without another human teammate");
        var schedule = new AmbientSchedule();
        Check(!schedule.Due(7, "", 100) && schedule.Due(7, "message-1", 100), "only a message wakes AI outside its turn");
        schedule.Mark(7, "message-1", 100);
        Check(!schedule.Due(7, "message-1", 105) && !schedule.Due(7, "new-chat", 105)
            && schedule.Due(7, "new-chat", 111), "new ally message triggers bounded proactive inspection");
        schedule.Mark(7, "new-chat", 111);
        Check(!schedule.Due(7, "new-chat", 120) && !schedule.Due(7, "new-chat", 172) && schedule.Due(7, "newer-chat", 172),
            "no periodic wakeup without a new message");
        var snapshotTime = DateTimeOffset.UtcNow;
        var snapshot = new VisibleSnapshot("session", 42, 7, snapshotTime, JsonSerializer.Deserialize<JsonElement>("""{"selfId":7}"""));
        Check(snapshot.Matches("session", 42, 7, snapshotTime) && !snapshot.Matches("session", 42, 8, snapshotTime)
            && !snapshot.Matches("other", 42, 7, snapshotTime) && !snapshot.Matches("session", 42, 7, snapshotTime.AddSeconds(21)),
            "live visibility tool rejects another player, another match and stale snapshots");

        Files.Root = Path.Combine(Path.GetTempPath(), "tohotopia-communication-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Files.Root);
        RoomFiles.Write(Files.Root, "room.json", new RoomConfig { NativeLobby = true,
            AIs = [new() { Provider = "openai", Model = "fixture" }] });
        var observation = JsonSerializer.Deserialize<JsonElement>("""{"selfId":7,"grids":[{"id":3}],"chat":{"availableChannels":["team"],"recentMessages":[{"messageId":"m1","channel":"team","text":"需要支援"}]}}""");
        var request = new CommunicationRequest("comm-1", "session", 42, 7, 2, 1, observation, ["team"], "civilization rules");
        var client = new FakeClient { Response = """{"requestId":"comm-1","channel":"team","message":"  我会支援北侧。  ","memory":"与盟友协防"}""" };
        var director = new Director(new OAuth(), (_, _) => client);
        var reply = await director.Communicate(request, CancellationToken.None);
        Check(reply.Channel == "team" && reply.Message == "我会支援北侧。", "out-of-turn communication can request team coordination");
        Check(client.LastPrompt.Contains("需要支援") && client.LastPrompt.Contains("inspect_visible_state")
            && client.LastPrompt.Contains("文明") && client.LastPrompt.Contains("行动回合")
            && client.LastPrompt.Contains("不受本回合剩余行动额度限制"),
            "communication prompt includes ally message, visibility tool and no-turn restrictions");
        client.Response = """{"requestId":"public-ok","channel":"public","message":"愿意谈判吗？"}""";
        var publicReply = await director.Communicate(request with { RequestId = "public-ok", AvailableChannels = ["public"] }, CancellationToken.None);
        Check(publicReply.Channel == "public" && publicReply.Message == "愿意谈判吗？", "public diplomacy is accepted only when offered");
        client.Response = "```json\n{\"requestId\":\"repair-ok\",\"channel\":\"public\",\"message\":\"你好\",}\n```";
        int beforeRepair = client.Calls;
        var repaired = await director.Communicate(request with { RequestId = "repair-ok", AvailableChannels = ["public"] }, CancellationToken.None);
        Check(repaired.Message == "你好" && client.Calls == beforeRepair + 1, "JSON fences and trailing comma are repaired without regeneration");
        client.Responses.Enqueue("{ broken json");
        client.Responses.Enqueue("""{"requestId":"retry-ok","channel":"public","message":"重新回应"}""");
        int beforeRetry = client.Calls;
        var retried = await director.Communicate(request with { RequestId = "retry-ok", AvailableChannels = ["public"] }, CancellationToken.None);
        Check(retried.Message == "重新回应" && client.Calls == beforeRetry + 2, "unrepairable JSON triggers model regeneration");
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
        Check(true, "unavailable team, public channel and malformed communication are rejected");
    }
}
