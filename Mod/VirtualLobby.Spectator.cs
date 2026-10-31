using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using N = TohotopiaMod.Native;

namespace TohotopiaMod;

internal sealed unsafe partial class VirtualLobby
{
    readonly SpectatorAccess spectatorAccess = new();
    IntPtr configuredMapConfig;
    IntPtr spectatorMap;
    bool startingSpectatorMatch;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate bool MapArgumentCall(IntPtr self, IntPtr map);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate bool GridSightCall(IntPtr map, uint grid);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void GridClickCall(IntPtr self, IntPtr map, IntPtr grid, bool flag);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate bool InteractionCall(IntPtr self, IntPtr action, IntPtr param, bool flag);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate bool ActionCall(IntPtr self, IntPtr action, IntPtr param);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void TurnCall(IntPtr self, IntPtr map, uint player);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SettlementCall(IntPtr self, IntPtr map, IntPtr settlement);

    internal void ConfigureMap(IntPtr config) { configuredMapConfig = config; spectatorMap = IntPtr.Zero; }
    bool IsSpectatorMap(IntPtr map) => Transport.Joined && spectatorAccess.Enabled && map != IntPtr.Zero
        && map == spectatorMap;
    bool IsSpectatorView(IntPtr map) => IsSpectatorMap(map) && map == N.Static("TH1_Logic.Core.Main", "MapData");

    internal void ExecuteModelAction(Action action)
    {
        using var scope = spectatorAccess.Simulation();
        action();
    }

    void InstallSpectatorHooks()
    {
        // Normal settlement ends a match when every human has lost. In an
        // all-model match its human roster is empty, so it ends immediately.
        // Count model civilizations only for this check; retain AI ownership
        // everywhere else so the authoritative AI loop keeps running.
        Bind<SettlementCall>("TH1_Logic.MatchConfig.NormalMatchSettlement", "fdj", 2, original => (self, map, settlement) =>
        {
            if (!IsSpectatorMap(map)) { original(self, map, settlement); return; }
            using var scope = spectatorAccess.Settle();
            original(self, map, settlement);
        }, trace: false);
        // The host remains the transport authority, but has no player mapping.
        Bind<MapArgumentCall>("RuntimeData.NetData", "gbf", 1, original => (self, map) =>
        {
            // MapData clones MapConfig during construction. Identify the new
            // authoritative map by its model member roster, not config pointers.
            if (Transport.Joined && spectatorAccess.Enabled && (IsSpectatorMap(map) || startingSpectatorMatch))
            {
                if (!MapSpectatorPlayers(self, map)) return false;
                if (spectatorMap != map)
                {
                    spectatorMap = map;
                    handles.Add(N.il2cpp_gchandle_new(map, false));
                }
                return true;
            }
            return original(self, map);
        });
        Bind<PlayerCall>("RuntimeData.MapData", "fzx", 1, original => (map, player) =>
            IsSpectatorMap(map) ? false : original(map, player), trace: false);
        Bind<BoolCall>("RuntimeData.MapData", "fzy", 0, original => map => IsSpectatorMap(map) || original(map), trace: false);

        // Full-map rendering bypasses only presentation gates, never Sight.gdu
        // or the unit-hiding rules used by the model observation/candidates.
        Bind<GridSightCall>("TH1_Core.Managers.PresentedSightGate", "npv", 2,
            original => (map, grid) => IsSpectatorView(map) || original(map, grid), trace: false);
        Bind<BoolCall>("TH1_Core.Managers.PresentedSightGate", "uax", 1,
            original => map => IsSpectatorView(map) || original(map), trace: false);

        // Reuse native read-only clicking without globally registering Replica:
        // doing so would disable the authoritative AI/turn loop.
        Bind<BoolCall>("TH1_Logic.Net.LiveSpectatorGame.SpectatorExecution", "trh", 1,
            original => map => (spectatorAccess.Presentation && IsSpectatorView(map)) || original(map), trace: false);
        Bind<VoidCall>("Logic.InputLogic", "gwl", 0, original => self =>
        {
            if (!IsSpectatorView(N.Static("TH1_Logic.Core.Main", "MapData"))) { original(self); return; }
            using var scope = spectatorAccess.Present();
            original(self);
        }, trace: false);
        Bind<GridClickCall>("Logic.MapInteraction", "gzj", 3, original => (self, map, grid, flag) =>
        {
            if (!IsSpectatorView(map)) { original(self, map, grid, flag); return; }
            using var scope = spectatorAccess.Present();
            original(self, map, grid, flag);
        }, trace: false);
        Bind<InteractionCall>("Logic.MapInteraction", "spk", 3, original => (self, action, param, flag) =>
            IsSpectatorView(N.Get<IntPtr>(self, "_mapData")) && !spectatorAccess.CanExecute ? false : original(self, action, param, flag));

        // Native AI and model actions are trusted simulation callers. UI buttons
        // and shortcuts cannot issue actions merely because a player is viewed.
        Bind<VoidCall>("Logic.GameLogic", "gtu", 0, original => self =>
        {
            if (!IsSpectatorView(N.Static("TH1_Logic.Core.Main", "MapData"))) { original(self); return; }
            using var scope = spectatorAccess.Simulation();
            original(self);
        }, trace: false);
        Bind<BoolCall>("TH1_Logic.Core.Main", "MainMemberStartMatch", 0, original => self =>
        {
            using var scope = spectatorAccess.Simulation();
            startingSpectatorMatch = true;
            try
            {
                bool result = original(self);
                if (!result) spectatorMap = IntPtr.Zero;
                return result;
            }
            finally { startingSpectatorMatch = false; }
        });
        Bind<MapArgumentCall>("Logic.Action.ActionLogicBase", "CompleteExecute", 1, original => (self, param) =>
            IsSpectatorMap(N.Get<IntPtr>(param, "MapData")) && !spectatorAccess.CanExecute ? false : original(self, param), trace: false);
        Bind<ActionCall>("TH1_Logic.Steam.GameNetSender", "eqf", 2, original => (self, action, param) =>
            IsSpectatorMap(N.Get<IntPtr>(param, "MapData")) && !spectatorAccess.CanExecute ? false : original(self, action, param), trace: false);
        Bind<TurnCall>("Logic.PlayerLogic", "gzx", 2, original => (self, map, player) =>
        {
            if (!IsSpectatorMap(map) || spectatorAccess.CanExecute) original(self, map, player);
        }, trace: false);

        // Chat requires a host server identity, but its roster is still built
        // solely from model slots. This temporary scope never creates a host civ.
        Bind<VoidCall>("TH1_Logic.Chat.ChatManager", "spe", 0, original => self =>
        {
            using var scope = spectatorAccess.ServeChat();
            original(self);
        }, trace: false);
        Battle.Log.LogInfo("Spectator hooks enabled: host has no civilization; model simulation and player sight remain authoritative");
    }

    bool MapSpectatorPlayers(IntPtr net, IntPtr map)
    {
        var playerMap = N.Get<IntPtr>(map, "PlayerMap");
        var mapping = N.Get<IntPtr>(net, "Players");
        var slots = N.Items(N.Get<IntPtr>(N.Get<IntPtr>(map, "MapConfig"), "MultiCivs"));
        if (configuredMapConfig == IntPtr.Zero) return false;
        if (playerMap == IntPtr.Zero || mapping == IntPtr.Zero || slots.Count != Transport.ParticipantCount) return false;
        var entries = new List<(ulong Member, uint Player)>();
        var seen = new HashSet<uint>();
        var seenMembers = new HashSet<ulong>();
        uint first = 0;
        foreach (var slot in slots)
        {
            ulong member = N.Get<ulong>(slot, "MemberId");
            uint player = N.Get<uint>(slot, "PlayerId");
            if (!Transport.IsAI(member) || player == 0 || !seen.Add(player) || !seenMembers.Add(member)) return false;
            entries.Add((member, player));
            if (first == 0) first = player;
        }
        N.Call(mapping, "Clear");
        foreach (var entry in entries)
        {
            ulong member = entry.Member;
            uint player = entry.Player;
            N.Call(mapping, "Add", (IntPtr)(&member), (IntPtr)(&player));
        }
        // SelfPlayerId is a display perspective, not a network-owned human slot.
        uint currentView = N.Get<uint>(playerMap, "SelfPlayerId");
        if (currentView == 0 || !N.WithUInt(mapping, "ContainsValue", currentView))
            N.Set(playerMap, "SelfPlayerId", first);
        return first != 0;
    }

    internal void RefreshSpectatorView(IntPtr map)
    {
        if (!IsSpectatorView(map)) return;
        var renderer = N.Static("TH1Renderer.MapRenderer", "_instance");
        if (renderer != IntPtr.Zero) N.Call(renderer, "emy");
        Battle.Log.LogInfo("Spectator full-map presentation refreshed; player sight sets were not changed");
    }
}
