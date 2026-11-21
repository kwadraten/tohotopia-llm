using System.Text.Json;
using Microsoft.Extensions.AI;
using Tohotopia;
using TohotopiaShared;
using Xunit;

public class ChatTests
{
    static DecisionRequest Request() => new("chat-public", 55, 330, 1, 0,
        JsonDocument.Parse("""{"grids":[],"chat":{"recentMessages":[]}}""").RootElement.Clone(),
        new() { new("a0", "UnitMove", 0, 0, 0, 0, 0, 0, "move"), new(ChatActions.PublicId, ChatActions.Kind(ChatActions.PublicId), 0, 0, 0, 0, 0, 0, "public"), new("end_turn", "EndTurn", 0, 0, 0, 0, 0, 0, "end") }, "chat rules", SessionId: "chat-match");

    [Fact]
    public async Task Chat_decisions_and_inbox()
    {
        var request = Request();
        var client = new ChatFixture();
        var director = new Director(new OAuth(), (_, _) => client);
        client.Reply = new("chat-public", ChatActions.PublicId, "communicate", Message: "  一起防守北方。  ");
        var chosen = await director.Decide(request, CancellationToken.None);
        Assert.True(chosen.Message == "一起防守北方。", "chat decision carries its own trimmed message, distinct from reason");
        async Task Rejected(string id, string candidate, string message)
        {
            client.Reply = new(id, candidate, "fixture", Message: message);
            try { await director.Decide(request with { RequestId = id }, CancellationToken.None); }
            catch (ArgumentException) { return; }
            catch (InvalidOperationException) { return; }
            throw new Exception("Invalid chat decision accepted: " + id);
        }
        await Rejected("empty-chat", ChatActions.PublicId, " ");
        await Rejected("long-chat", ChatActions.PublicId, new string('中', 700));
        await Rejected("unavailable-team", ChatActions.TeamId, "secret");
        await Rejected("message-on-move", "end_turn", "hello");
        var teamRequest = request with { RequestId = "team-chat", Candidates = new(request.Candidates) { new(ChatActions.TeamId, ChatActions.Kind(ChatActions.TeamId), 0, 0, 0, 0, 0, 0, "team") } };
        client.Reply = new("team-chat", ChatActions.TeamId, "coordinate", Message: "守住首都。");
        Assert.True((await director.Decide(teamRequest, CancellationToken.None)).CandidateId == ChatActions.TeamId, "offered team message candidate can be selected");
        var onlyChat = request with { RequestId = "only-chat", Candidates = request.Candidates.Where(c => c.Id != "a0").ToList() };
        Assert.True((await director.Decide(onlyChat, CancellationToken.None)).CandidateId == "end_turn", "only chat candidates cause end turn");
        ChatActions.ValidateMessage(new string('a', 2048));
    }

    [Fact]
    public void Relay_labels_and_byte_limits()
    {
        string forwarded = ChatActions.RelayMessage(2, "provider/model", "  hello  ");
        Assert.True(forwarded == "[代provider/model 转发] hello" &&
            ChatActions.IsRelayedMessage(forwarded) &&
            ChatActions.IsRelayedMessage("[房主代 AI 席位 2 转发] hello") &&
            !ChatActions.IsRelayedMessage("[代我问候] hello"),
            "forwarded chat identifies the model and remains recognizable as a host relay");
        Assert.True(ChatActions.RelayMessage(1, "", "hello") == "[代AI 席位 1 转发] hello" &&
            !ChatActions.ModelLabel("<b>\nname</b>").Contains('<') &&
            ChatActions.ModelLabel(new string('x', 100)).Length == 49,
            "model labels are bounded, sanitized and support missing names");
        Assert.Throws<ArgumentException>(() => ChatActions.RelayMessage(1, "model", new string('a', 2048)));
    }

    [Fact]
    public void Inbox_visibility_and_history()
    {
        var inbox = new ChatInbox();
        inbox.Add(new("1", 2, "ally", "public", 0, "public"));
        inbox.Add(new("2", 3, "ally", "team", 1, "team one"));
        inbox.Add(new("3", 4, "enemy", "team", 2, "team two"));
        inbox.Add(new("2", 3, "ally", "team", 1, "duplicate"));
        Assert.True(inbox.Visible(1).Select(m => m.Text).SequenceEqual(new[] { "public", "team one" }), "team inbox excludes enemy-team messages and duplicate deliveries");
        Assert.True(inbox.Visible(0).Length == 1 && inbox.Visible(2).Last().Text == "team two", "team changes immediately hide old-team history and no-team players see public only");
        for (int i = 0; i < 120; i++) inbox.Add(new("p" + i, 2, "ally", "public", 0, "message" + i));
        Assert.True(inbox.Visible(1).Length == 20 && inbox.Visible(1).Last().Text == "message119", "chat observation retains the latest 20 delivered messages");
        inbox.Clear();
        Assert.True(inbox.Visible(1).Length == 0, "leaving a Virtual room clears chat history");
    }

    [Fact]
    public void Room_team_defaults_and_validation()
    {
        var room = JsonSerializer.Deserialize<RoomConfig>("""{"aIs":[{"model":"fixture"}]}""", RoomFiles.Json)!;
        Assert.True(room.HumanTeamId == 0 && room.AIs[0].TeamId == 0, "existing room configs keep no-team defaults");
        Assert.Throws<ArgumentException>(() => (room with { HumanTeamId = -1 }).Validate());
    }

    sealed class ChatFixture : IChatClient
    {
        internal Decision Reply = new("", "", "");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(Reply, Files.Json))));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
