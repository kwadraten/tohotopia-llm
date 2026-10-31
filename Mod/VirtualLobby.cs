using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using IL2CppSharp.Hooking;
using UnityEngine;
using Il2CppInterop.Runtime.Injection;
using R = IL2CppSharp.Il2CppReflection;
using N = TohotopiaMod.Native;

namespace TohotopiaMod;

// The native UI requires a SteamLobbyManager concrete type. This uninitialized
// adapter satisfies that UI contract; every network operation uses VirtualP2P.
internal sealed unsafe partial class VirtualLobby : IDisposable
{
    const string LobbyType = "TH1_Logic.Steam.SteamLobbyManager";
    readonly List<HookHandle> hooks = new();
    readonly List<uint> handles = new();
    internal readonly VirtualP2P Transport = new();
    internal readonly VirtualChat Chat;
    internal IntPtr Pointer
    {
        get; private set;
    }
    IntPtr previous;
    uint previousHandle;
    int inLobby;
    int memberOK;
    readonly Dictionary<ulong, string> decisions = new();
    bool disposed;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void VoidCall(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate bool BoolCall(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate bool ReadyCall(IntPtr self, IntPtr reason);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong IdCall(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int IntCall(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr ObjectCall(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate bool MemberCall(IntPtr self, ulong id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr AvatarCall(IntPtr self, ulong id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate bool CapacityCall(IntPtr self, int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate bool SendCall(IntPtr self, ulong id, IntPtr data, bool reliable);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate bool BroadcastCall(IntPtr self, IntPtr data, bool reliable);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate bool PlayerCall(IntPtr self, uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void LobbyDataCall(IntPtr self, IntPtr key, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr LobbyReadCall(IntPtr self, IntPtr key);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate bool ChatCall(IntPtr self, ulong recipient, IntPtr data);
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)] static extern uint il2cpp_array_length(IntPtr array);

    internal VirtualLobby()
    {
        Chat = new(Transport);
        try
        {
            Pointer = N.New(LobbyType);
            handles.Add(N.il2cpp_gchandle_new(Pointer, false));
            inLobby = N.EnumValues("TH1_Logic.Net.LobbyState")["InLobby"];
            memberOK = N.EnumValues("RuntimeData.MemberNetState")["OK"];
            Transport.Received += Receive;
            Transport.Received += Chat.Receive;
        }
        catch { Dispose(); throw; }
    }

    void InstallHooks()
    {
        if (!Transport.Joined || hooks.Count != 0)
            throw new InvalidOperationException("Virtual 联机必须在加入房间后启用，且不能重复启用");
        try
        {
            HookEngine.Initialize(new HookLog());
            var knownMethod = N.Method(Pointer, "exs", 0);
            byte flags = Marshal.ReadByte(knownMethod, 0x53);
            if ((flags & 0x70) == 0x30
                && Marshal.ReadIntPtr(knownMethod, 0x58) != IntPtr.Zero
                && Marshal.ReadIntPtr(knownMethod, 0x60) == Marshal.ReadIntPtr(knownMethod)
                && Marshal.ReadIntPtr(knownMethod, 0x68) == Marshal.ReadIntPtr(knownMethod, 8))
            {
                HybridCLRCompat.UseCompactInterpreterFlags = true;
                Battle.Log.LogInfo("Virtual HybridCLR layout verified: interpreter flags at bits 4/5");
            }
            foreach (string name in new[] { "exs", "ewc", "get_itu", "get_itv", "get_itw" })
                Bind<BoolCall>(LobbyType, name, 0, original => self => self == Pointer || original(self));
            foreach (string name in new[] { "ewd", "jwh" })
                Bind<ReadyCall>(LobbyType, name, 1, original => (self, reason) =>
                {
                    if (self != Pointer)
                        return original(self, reason);
                    Marshal.WriteIntPtr(reason, R.CreateIl2CppString(""));
                    return name == "ewd" || Transport.Joined;
                });
            foreach (string name in new[] { "ezt", "ezs" })
                Bind<BoolCall>(LobbyType, name, 0, original => self => self == Pointer ? Transport.Joined : original(self));
            foreach (string name in new[] { "get_ufn", "get_ufo", "exb", "exc" })
                Bind<BoolCall>(LobbyType, name, 0, original => self => self == Pointer ? false : original(self));
            foreach (string name in new[] { "ezp", "ezq" })
                Bind<IdCall>(LobbyType, name, 0, original => self => self == Pointer ? VirtualP2P.HostId : original(self));
            Bind<IdCall>(LobbyType, "ewm", 0, original => self => self == Pointer ? VirtualP2P.RoomId : original(self));
            Bind<IntCall>(LobbyType, "ezr", 0, original => self => self == Pointer ? (Transport.Joined ? inLobby : 0) : original(self));
            foreach (string name in new[] { "ezk", "ezl" })
                Bind<IntCall>(LobbyType, name, 0, original => self => self == Pointer ? Transport.ParticipantCount : original(self));
            foreach (string name in new[] { "ezn", "ezb" })
                Bind<MemberCall>(LobbyType, name, 1, original => (self, id) => self == Pointer ? Transport.Joined && Transport.Members.ContainsKey(id) : original(self, id));
            Bind<MemberCall>(LobbyType, "tkl", 1, original => (self, id) => self == Pointer
                ? Transport.Joined && (Transport.IsParticipant(id) || (spectatorAccess.ChatServer && id == VirtualP2P.HostId)) : original(self, id));
            foreach (string name in new[] { "ezj", "fcd" })
                if (R.FindMethod(LobbyType, name, 1) != IntPtr.Zero)
                    Bind<AvatarCall>(LobbyType, name, 1, original => (self, id) => self == Pointer ? IntPtr.Zero : original(self, id));
            Bind<CapacityCall>(LobbyType, "ezm", 1, original => (self, count) => self == Pointer ? count == Transport.ParticipantCount : original(self, count));
            foreach (string name in new[] { "evy", "eyp", "exi", "ezg", "exu", "ewi", "ezw" })
                Bind<VoidCall>(LobbyType, name, 0, original => self => { if (self != Pointer) original(self); });
            Bind<VoidCall>(LobbyType, "ewh", 0, original => self => { if (self == Pointer) Transport.Leave(); else original(self); });
            foreach (string name in new[] { "eyz", "eza" })
                Bind<SendCall>(LobbyType, name, 3, original => (self, id, data, reliable) => self == Pointer ? Transport.Send(VirtualP2P.HostId, id, Bytes(data)) : original(self, id, data, reliable));
            Bind<BroadcastCall>(LobbyType, "ezc", 2, original => (self, data, reliable) => self == Pointer ? Transport.Broadcast(VirtualP2P.HostId, Bytes(data)) : original(self, data, reliable));
            Bind<LobbyDataCall>(LobbyType, "exy", 2, original => (self, key, value) => { if (self != Pointer) original(self, key, value); });
            Bind<LobbyReadCall>(LobbyType, "exz", 1, original => (self, key) => self == Pointer ? R.CreateIl2CppString("") : original(self, key));
            foreach (string name in new[] { "ewn", "exr", "ewy" })
                Bind<ObjectCall>(LobbyType, name, 0, original => self => self == Pointer ? N.Get<IntPtr>(Pointer, "RoomName") : original(self));
            Bind<ChatCall>("TH1_Logic.Steam.GameNetSender", "nkk", 2, original => (self, recipient, data) =>
            {
                var manager = N.Static("TH1_Logic.Net.LobbyManager", "<Instance>k__BackingField");
                return Transport.Joined && N.Get<IntPtr>(manager, "Lobby") == Pointer
                    ? Chat.Route(recipient, data) : original(self, recipient, data);
            });
            // Network players normally wait for remote input. Model endpoints run
            // on the shared host simulation, using the existing AI action engine.
            Bind<PlayerCall>("RuntimeData.MapData", "fzv", 1, original => (self, id) => IsModelPlayer(self, id) ? spectatorAccess.EvaluatingSettlement : original(self, id));
            Bind<BoolCall>("Logic.GameLogic", "guc", 0, original => self =>
            {
                var map = N.Static("TH1_Logic.Core.Main", "MapData");
                var player = map == IntPtr.Zero ? IntPtr.Zero : N.Call(map, "get_ivn");
                return IsModelPlayer(map, N.Id(player)) || original(self);
            });
            if (Transport.SpectatorMode) InstallSpectatorHooks();
            Battle.Log.LogInfo("Virtual network hooks installed after room entry request");
            RefreshCallers();
        }
        catch { Dispose(); throw; }
    }

    void Bind<T>(string type, string name, int count, Func<T, T> wrap, bool trace = true) where T : Delegate
    {
        T original = null!;
        // The callback closes over a forwarder whose target is assigned after installation.
        // GetOriginal supplies the HybridCLR shadow MethodInfo automatically.
        T forward = Forward<T>(() => original, name, trace);
        var method = type == LobbyType ? N.Method(Pointer, name, count) : R.FindMethod(type, name, count);
        Battle.Log.LogInfo($"Virtual hook installing: {type}.{name} method=0x{method:X} entry=0x{Marshal.ReadIntPtr(method):X}");
        if (!HybridCLRCompat.IsInterpreterMethod(method))
        {
            var layout = new byte[128];
            Marshal.Copy(method, layout, 0, layout.Length);
            Battle.Log.LogError("Virtual interpreter layout: " + Convert.ToHexString(layout));
            throw new InvalidOperationException("HybridCLR 解释器方法识别失败，已拒绝修改共享调用桥：" + type + "." + name);
        }
        T replacement = wrap(forward);
        var hook = HookEngine.Hook(method, trace ? Trace(replacement, name) : replacement, type + "." + name);
        if (!hook.IsValid)
            throw new InvalidOperationException("无法安装 Virtual 联机 Hook：" + type + "." + name);
        hooks.Add(hook);
        original = hook.GetOriginal<T>();
    }

    static T Trace<T>(T target, string name) where T : Delegate
    {
        var invoke = typeof(T).GetMethod("Invoke")!;
        var parameters = Array.ConvertAll(invoke.GetParameters(), p => System.Linq.Expressions.Expression.Parameter(p.ParameterType, p.Name));
        return System.Linq.Expressions.Expression.Lambda<T>(System.Linq.Expressions.Expression.Block(
            System.Linq.Expressions.Expression.Call(typeof(VirtualLobby).GetMethod(nameof(TraceEntry), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!,
                System.Linq.Expressions.Expression.Constant(name), parameters[0]),
            System.Linq.Expressions.Expression.Invoke(System.Linq.Expressions.Expression.Constant(target), parameters)), parameters).Compile();
    }

    static void TraceEntry(string name, IntPtr self) => Battle.Log.LogDebug($"Virtual hook entered: {name} self=0x{self:X}");
    static void TraceOriginal(string name) => Battle.Log.LogDebug("Virtual hook forwarding original: " + name);

    static void RefreshCallers()
    {
        int count = 0;
        foreach (string type in new[]
        {
            "TH1_UI.View.Outside.UIOutsideMultiplayView", LobbyType,
            "TH1_Logic.Net.LobbyGameplayParticipants", "RuntimeData.MapConfig",
            "TH1_Logic.Core.Main", "Logic.GameLogic", "RuntimeData.MapData",
            "TH1_Logic.Steam.GameNetSender", "TH1_Logic.Steam.GameNetReceiver",
            "TH1_Logic.Chat.ChatManager", "TH1_Logic.Chat.ChatSession",
            "RuntimeData.NetData", "Logic.InputLogic", "Logic.MapInteraction", "Logic.PlayerLogic",
            "Logic.Action.ActionLogicBase", "Logic.AI.AIActionBase", "Logic.AI.AILogic",
            "TH1_Core.Managers.PresentedSightGate", "TH1_Logic.Net.LiveSpectatorGame.SpectatorExecution",
            "TH1Renderer.GridRenderer", "TH1Renderer.MapRenderer", "TH1_Renderer.UnitRenderer",
            "TH1_Logic.MatchConfig.NormalMatchSettlement", "TH1_Logic.MatchConfig.MatchSettlementLogicFactory"
        })
            count += N.RefreshInterpreterCalls(type);
        Battle.Log.LogInfo("Virtual interpreter callers refreshed: " + count);
    }

    static T Forward<T>(Func<T> target, string name, bool trace) where T : Delegate
    {
        var invoke = typeof(T).GetMethod("Invoke")!;
        var parameters = Array.ConvertAll(invoke.GetParameters(), p => System.Linq.Expressions.Expression.Parameter(p.ParameterType, p.Name));
        var call = System.Linq.Expressions.Expression.Invoke(System.Linq.Expressions.Expression.Invoke(System.Linq.Expressions.Expression.Constant(target)), parameters);
        System.Linq.Expressions.Expression body = trace ? System.Linq.Expressions.Expression.Block(
            System.Linq.Expressions.Expression.Call(typeof(VirtualLobby).GetMethod(nameof(TraceOriginal), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!, System.Linq.Expressions.Expression.Constant(name)), call) : call;
        return System.Linq.Expressions.Expression.Lambda<T>(body, parameters).Compile();
    }

    static byte[] Bytes(IntPtr data)
    {
        if (data == IntPtr.Zero)
            return Array.Empty<byte>();
        int length = checked((int)il2cpp_array_length(data));
        if (length > 128 * 1024 * 1024)
            return Array.Empty<byte>();
        var result = new byte[length];
        Marshal.Copy(data + 4 * IntPtr.Size, result, 0, length);
        return result;
    }

    bool IsModelPlayer(IntPtr map, uint id)
    {
        if (!Transport.Joined || map == IntPtr.Zero || id == 0)
            return false;
        foreach (var slot in N.Items(N.Get<IntPtr>(N.Get<IntPtr>(map, "MapConfig"), "MultiCivs")))
            if (N.Get<uint>(slot, "PlayerId") == id && Transport.IsAI(N.Get<ulong>(slot, "MemberId")))
                return true;
        return false;
    }

    internal void Join(TohotopiaShared.RoomConfig config, IntPtr view)
    {
        spectatorAccess.Enabled = config.SpectatorMode;
        Transport.Join(config.AIs.ConvertAll(ai => ai.Model), config.SpectatorMode);
        N.SetInt(Pointer, "<CurrentState>k__BackingField", inLobby);
        N.SetText(Pointer, "RoomName", config.Name);
        var members = N.Get<IntPtr>(Pointer, "_memberInfos");
        N.Call(members, "Clear");
        foreach (var entry in Transport.Members)
        {
            var member = N.New("TH1_Logic.Net.MemberInfo");
            N.Set(member, "Id", entry.Key);
            N.SetText(member, "Name", entry.Value);
            ulong id = entry.Key;
            N.Call(members, "Add", (IntPtr)(&id), member);
        }
        // Membership is ready before any Steam adapter method is intercepted.
        InstallHooks();
        var manager = N.Static("TH1_Logic.Net.LobbyManager", "<Instance>k__BackingField");
        previous = N.Get<IntPtr>(manager, "Lobby");
        previousHandle = N.il2cpp_gchandle_new(previous, false);
        N.Set(manager, "Lobby", Pointer);
        if (N.Get<IntPtr>(manager, "Lobby") != Pointer)
            throw new InvalidOperationException("Virtual 房间引用写入失败");
        if (view != IntPtr.Zero)
            N.Set(view, "_lobby", Pointer);
    }

    void Receive(VirtualP2P.Packet packet)
    {
        // AI endpoints observe the same map as the host. Receipt acknowledges
        // their shared state; sending broadcasts through GameNetReceiver would
        // execute host actions twice and corrupt the action history.
        if (Transport.IsAI(packet.Recipient))
            Confirm(packet.Recipient);
        else if (packet.Recipient == VirtualP2P.HostId && Transport.IsAI(packet.Sender))
            decisions[packet.Sender] = System.Text.Encoding.UTF8.GetString(packet.Data);
    }

    internal string DeliverDecision(int seat, string payload)
    {
        ulong sender = VirtualP2P.MemberId(seat);
        if (!Transport.IsAI(sender) || !Transport.Send(sender, VirtualP2P.HostId, System.Text.Encoding.UTF8.GetBytes(payload)))
            throw new InvalidOperationException("模型玩家已离开房间或 Virtual 消息队列已满");
        Transport.Pump();
        if (!decisions.Remove(sender, out var decision))
            throw new InvalidOperationException("Virtual 联机未收到模型玩家决策");
        return decision;
    }

    void Confirm(ulong id)
    {
        var main = N.Static("TH1_Logic.Core.Main", "<Instance>k__BackingField");
        var confirms = N.Get<IntPtr>(main, "ConfirmMap");
        if (confirms == IntPtr.Zero)
            return;
        var confirm = N.Call(confirms, "gax", (IntPtr)(&id));
        if (confirm == IntPtr.Zero)
            return;
        N.Call(confirm, "gas");
        N.SetInt(confirm, "State", memberOK);
        N.Set(confirm, "ConfirmTime", Time.time);
    }

    internal void Poll()
    {
        Transport.Pump();
        if (Transport.Joined)
            foreach (var id in Transport.Members.Keys)
                if (Transport.IsAI(id))
                    Confirm(id);
    }

    internal void Restore(IntPtr view)
    {
        if (disposed)
            return;
        Transport.Leave();
        if (Pointer != IntPtr.Zero)
            N.SetInt(Pointer, "<CurrentState>k__BackingField", 0);
        decisions.Clear();
        Chat.Clear();
        if (previous == IntPtr.Zero)
            return;
        var manager = N.Static("TH1_Logic.Net.LobbyManager", "<Instance>k__BackingField");
        if (N.Get<IntPtr>(manager, "Lobby") == Pointer)
        {
            // Virtual uses a stable room ID. Clear the native chat session so a
            // rapid leave/rejoin cannot retain the previous room's history.
            N.Call(N.Static("TH1_Logic.Chat.ChatManager", "Instance"), "sop");
            N.Set(manager, "Lobby", previous);
        }
        if (view != IntPtr.Zero && N.Get<IntPtr>(view, "_lobby") == Pointer)
            N.Set(view, "_lobby", previous);
        previous = IntPtr.Zero;
        if (previousHandle != 0)
            N.il2cpp_gchandle_free(previousHandle);
        previousHandle = 0;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        Restore(IntPtr.Zero);
        for (int i = hooks.Count - 1; i >= 0; i--)
            hooks[i].Unhook();
        if (hooks.Count > 0)
            RefreshCallers();
        foreach (var handle in handles)
            N.il2cpp_gchandle_free(handle);
        handles.Clear();
        disposed = true;
    }

    sealed class HookLog : IHookLogger
    {
        public void Debug(string text) => Battle.Log.LogDebug(text);
        public void Info(string text) => Battle.Log.LogInfo(text);
        public void Warning(string text) => Battle.Log.LogWarning(text);
        public void Error(string text) => Battle.Log.LogError(text);
    }
}
