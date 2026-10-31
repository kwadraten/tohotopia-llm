using System;
using TohotopiaShared;
using N = TohotopiaMod.Native;

namespace TohotopiaMod;

internal sealed class RoomBridge
{
    readonly string root;
    readonly Battle battle;
    readonly NativeAiBridge native;
    readonly ModelNameDisplay names = new();

    public RoomBridge(string root, Battle battle)
    {
        this.root = root;
        this.battle = battle;
        native = new NativeAiBridge(root, battle);
    }

    public int Seat(uint playerId) => native.Seat(playerId);
    internal uint[] ControlledPlayers() => native.ControlledPlayers();
    internal string[] ChatChannels(uint playerId) => native.ChatChannels(playerId);
    internal ChatObservation[] ChatHistory(uint playerId) => native.ChatHistory(playerId);
    internal bool IsOwnMessage(uint playerId, ulong memberId) => false;
    internal bool SendChat(uint playerId, string channel, string text, out string result) => native.SendChat(playerId, channel, text, out result);
    internal ChatParticipant[] ChatParticipants() => native.ChatParticipants();
    internal string DeliverDecision(uint playerId, string payload) => payload;
    public bool ModelsEnabled => native.ModelsEnabled;
    internal void ExecuteModelAction(Action action) => action();
    public void ModelStopped(string message) => native.ModelStopped(message);

    public void Poll()
    {
        var controller = N.Static("TH1_UI.Core.ViewControllerManager", "_outsideMultiplayController");
        var view = N.Get<IntPtr>(controller, "_script");
        battle.SettingsPoll(view != IntPtr.Zero && N.Get<bool>(view, "_show"));
        native.Poll(RoomFiles.Read<RoomConfig>(root, "room.json"));
        names.Poll(native);
    }

    public void Dispose()
    {
        native.Dispose();
        names.Poll(native);
    }
}
