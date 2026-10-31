using System.Text.Json;
using TohotopiaMod;
using TohotopiaShared;

static class SpectatorChecks
{
    internal static void Run()
    {
        void Check(bool value, string message)
        {
            if (!value) throw new Exception(message);
            Console.WriteLine("PASS " + message);
        }
        var room = new RoomConfig
        {
            HumanCivilization = 7, HumanTeamId = 6,
            AIs = [new() { Model = "same-model", Civilization = 0, TeamId = 1 }, new() { Model = "same-model", Civilization = 1, TeamId = 2 }]
        };
        room.Validate();
        Check(room.Players().Count == 3 && room.Players()[0] is { ModelSeat: 0, Civilization: 7, TeamId: 6 }, "normal room still assigns a human civilization before model slots");
        var spectator = room with { SpectatorMode = true, UseModels = true };
        spectator.Validate();
        Check(spectator.Players().Count == 2 && spectator.Players().All(p => p.ModelSeat > 0) && spectator.Players()[0] is { Index: 0, ModelSeat: 1, Civilization: 0, TeamId: 1 }, "spectator room creates only model civilizations, including the first slot");
        Check(spectator.Players().Select(p => p.ModelSeat).SequenceEqual(new[] { 1, 2 }) && spectator.Players()[1].TeamId == 2, "spectator mode preserves per-model identity, civilization and team configuration");
        var restored = JsonSerializer.Deserialize<RoomConfig>(JsonSerializer.Serialize(spectator, RoomFiles.Json), RoomFiles.Json)!;
        Check(restored.SpectatorMode && restored.UseModels && restored.Players().Count == 2, "spectator participation survives room-file persistence");
        Check(!JsonSerializer.Deserialize<RoomConfig>("{}", RoomFiles.Json)!.SpectatorMode, "old room files retain normal human participation");
        bool oneRejected = false, nativeRejected = false;
        try { (spectator with { AIs = [room.AIs[0]] }).Validate(); } catch (ArgumentException) { oneRejected = true; }
        try { (spectator with { UseModels = false }).Validate(); } catch (ArgumentException) { nativeRejected = true; }
        Check(oneRejected && nativeRejected, "spectator matches require at least two model-controlled players");

        var transport = new VirtualP2P();
        transport.Join(spectator.AIs.Select(a => a.Model), spectatorMode: true);
        Check(transport.Members.Count == 3 && transport.ParticipantCount == 2 && !transport.IsParticipant(VirtualP2P.HostId), "observer host remains a transport endpoint without occupying a game seat");
        Check(transport.IsParticipant(VirtualP2P.MemberId(1)) && transport.IsParticipant(VirtualP2P.MemberId(2))
            && transport.ModelSeat(VirtualP2P.MemberId(1)) == 1 && transport.ModelSeat(VirtualP2P.MemberId(2)) == 2, "duplicate model names preserve separate player mappings independent of native slot order");
        var received = new List<VirtualP2P.Packet>();
        transport.Received += received.Add;
        transport.Broadcast(VirtualP2P.HostId, [1]);
        transport.Send(VirtualP2P.MemberId(2), VirtualP2P.HostId, [2]);
        transport.Pump();
        Check(received.Count == 3 && received.Count(p => transport.IsAI(p.Recipient)) == 2 && received.Last().Sender == VirtualP2P.MemberId(2), "observer authority still broadcasts model state and receives each model decision");
        transport.Leave();
        transport.Join(["replacement"]);
        Check(!transport.SpectatorMode && transport.ParticipantCount == 2 && transport.IsParticipant(VirtualP2P.HostId), "leaving spectator mode restores normal host participation on rejoin");

        var access = new SpectatorAccess { Enabled = true };
        Check(!access.CanExecute && !access.Presentation && !access.ChatServer, "spectator mode denies human gameplay actions by default");
        using (access.Present())
        {
            Check(access.Presentation && !access.CanExecute, "read-only native inspection does not grant simulation permission");
            using (access.Simulation())
            {
                using (access.Simulation()) Check(access.CanExecute, "nested authoritative model operations remain executable");
                Check(access.CanExecute, "nested scope disposal does not interrupt its parent simulation");
            }
            Check(!access.CanExecute, "model execution permission is removed before returning to observer input");
        }
        try { using var scope = access.Simulation(); throw new InvalidOperationException(); } catch (InvalidOperationException) { }
        Check(!access.CanExecute && !access.Presentation, "exceptional action exit restores observer read-only permissions");
        using (access.ServeChat()) Check(access.ChatServer && !access.CanExecute, "chat authority can serve model messages without obtaining a playable host slot");
        Check(!access.ChatServer, "chat-only membership cannot leak into map participant enumeration");
        using (access.Settle())
        {
            Check(access.EvaluatingSettlement && !access.CanExecute && !access.ChatServer, "all-model settlement counts competitors without granting observer control");
            using (access.Settle()) Check(access.EvaluatingSettlement, "nested settlement retains model competitors");
            Check(access.EvaluatingSettlement, "inner settlement exit keeps the outer roster active");
        }
        try { using var scope = access.Settle(); throw new InvalidOperationException(); } catch (InvalidOperationException) { }
        Check(!access.EvaluatingSettlement, "settlement restores AI ownership even on failure");
        access.Enabled = false;
        Check(access.CanExecute, "normal rooms retain the original player input behavior");
    }
}
