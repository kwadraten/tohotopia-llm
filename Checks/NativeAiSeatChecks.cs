using System.Text.Json;
using TohotopiaMod;
using TohotopiaShared;

static class NativeAiSeatChecks
{
    internal static void Run()
    {
        void Check(bool value, string message)
        {
            if (!value) throw new Exception(message);
            Console.WriteLine("PASS " + message);
        }
        var slots = new[]
        {
            new NativeAiSeat(3, 0, 303, true, false),
            new NativeAiSeat(0, 76561198000000001, 100, false, false),
            new NativeAiSeat(2, 76561198000000002, 202, false, false),
            new NativeAiSeat(1, 0, 101, true, false),
            new NativeAiSeat(4, 0, 404, false, true)
        };
        var mapped = NativeAiSeats.Assign(slots, 2);
        Check(mapped.Count == 2 && mapped[101] == 1 && mapped[303] == 2
            && !mapped.ContainsKey(100) && !mapped.ContainsKey(202), "only native AI seats map to model order");
        Check(NativeAiSeats.Available(slots) == 2, "open and human-controlled seats are excluded");
        var lobby = NativeAiSeats.AssignSlots(slots, 2);
        Check(lobby.Count == 2 && lobby[1] == 1 && lobby[3] == 2 &&
            NativeAiSeats.AssignSlots(slots, 3).Count == 0,
            "lobby labels use the same ordered AI seats and require enough seats");
        bool missing = false, unmapped = false, duplicate = false;
        try { NativeAiSeats.Assign(slots, 3); } catch (InvalidOperationException) { missing = true; }
        try { NativeAiSeats.Assign(slots.WithPlayerId(1, 0), 2); } catch (InvalidOperationException) { unmapped = true; }
        try { NativeAiSeats.Assign(slots.WithPlayerId(3, 101), 2); } catch (InvalidOperationException) { duplicate = true; }
        Check(missing && unmapped && duplicate, "invalid native AI player mappings fail closed");
        var config = new RoomConfig
        {
            NativeLobby = true,
            AIs = [new() { Provider = "openai", Model = "one" }, new() { Provider = "openai", Model = "two" }]
        };
        config.Validate();
        var restored = JsonSerializer.Deserialize<RoomConfig>(JsonSerializer.Serialize(config, RoomFiles.Json), RoomFiles.Json)!;
        Check(restored.NativeLobby && restored.AIs.Count == 2, "native lobby configuration survives persistence");
        bool tooMany = false, spectator = false;
        try { (config with { AIs = Enumerable.Repeat(config.AIs[0], 5).ToList() }).Validate(); } catch (ArgumentException) { tooMany = true; }
        try { (config with { SpectatorMode = true }).Validate(); } catch (ArgumentException) { spectator = true; }
        Check(tooMany && spectator, "native room rejects more than four AI seats and spectator mode");
    }

    static NativeAiSeat[] WithPlayerId(this NativeAiSeat[] slots, int index, uint id) =>
        slots.Select(s => s.Index == index ? s with { PlayerId = id } : s).ToArray();
}
