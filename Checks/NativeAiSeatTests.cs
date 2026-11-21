using System.Text.Json;
using TohotopiaMod;
using TohotopiaShared;
using Xunit;

public class NativeAiSeatTests
{
    [Fact]
    public void Seat_mapping_and_validation()
    {
        var slots = new[]
        {
            new NativeAiSeat(3, 0, 303, true, false),
            new NativeAiSeat(0, 76561198000000001, 100, false, false),
            new NativeAiSeat(2, 76561198000000002, 202, false, false),
            new NativeAiSeat(1, 0, 101, true, false),
            new NativeAiSeat(4, 0, 404, false, true)
        };
        var mapped = NativeAiSeats.Assign(slots, 2);
        Assert.True(mapped.Count == 2 && mapped[101] == 1 && mapped[303] == 2
            && !mapped.ContainsKey(100) && !mapped.ContainsKey(202), "only native AI seats map to model order");
        Assert.True(NativeAiSeats.Available(slots) == 2, "open and human-controlled seats are excluded");
        var lobby = NativeAiSeats.AssignSlots(slots, 2);
        Assert.True(lobby.Count == 2 && lobby[1] == 1 && lobby[3] == 2 &&
            NativeAiSeats.AssignSlots(slots, 3).Count == 0,
            "lobby labels use the same ordered AI seats and require enough seats");
        Assert.Throws<InvalidOperationException>(() => NativeAiSeats.Assign(slots, 3));
        Assert.Throws<InvalidOperationException>(() => NativeAiSeats.Assign(slots.WithPlayerId(1, 0), 2));
        Assert.Throws<InvalidOperationException>(() => NativeAiSeats.Assign(slots.WithPlayerId(3, 101), 2));
        var config = new RoomConfig
        {
            NativeLobby = true,
            AIs = [new() { Provider = "openai", Model = "one" }, new() { Provider = "openai", Model = "two" }]
        };
        config.Validate();
        var restored = JsonSerializer.Deserialize<RoomConfig>(JsonSerializer.Serialize(config, RoomFiles.Json), RoomFiles.Json)!;
        Assert.True(restored.NativeLobby && restored.AIs.Count == 2, "native lobby configuration survives persistence");
        Assert.Throws<ArgumentException>(() => (config with { AIs = Enumerable.Repeat(config.AIs[0], 5).ToList() }).Validate());
        Assert.Throws<ArgumentException>(() => (config with { SpectatorMode = true }).Validate());
    }
}

static class NativeAiSeatExtensions
{
    internal static NativeAiSeat[] WithPlayerId(this NativeAiSeat[] slots, int index, uint id) =>
        slots.Select(s => s.Index == index ? s with { PlayerId = id } : s).ToArray();
}
