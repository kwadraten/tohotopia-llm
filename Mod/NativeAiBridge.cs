using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using IL2CppSharp;
using IL2CppSharp.Hooking;
using Il2CppInterop.Runtime.Injection;
using TohotopiaShared;
using UnityEngine;
using R = IL2CppSharp.Il2CppReflection;
using N = TohotopiaMod.Native;

namespace TohotopiaMod;

internal sealed unsafe class NativeAiBridge
{
    readonly string root;
    readonly Battle battle;
    readonly Dictionary<int, ChatInbox> teamInboxes = new();
    readonly Dictionary<string, double> teamMessageTimes = new();
    HookHandle chatHook;
    bool chatHookInstalled;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate bool ChatCall(IntPtr self, ulong recipient, IntPtr data);
    ChatCall chatOriginal, chatReplacement;
    float nextRelay;
    sealed class HookLog : IHookLogger
    {
        public void Debug(string text) => Battle.Log.LogDebug(text);
        public void Info(string text) => Battle.Log.LogInfo(text);
        public void Warning(string text) => Battle.Log.LogWarning(text);
        public void Error(string text) => Battle.Log.LogError(text);
    }
    void InstallChatHook()
    {
        if (chatHookInstalled) return;
        HookEngine.Initialize(new HookLog());
        var manager = N.Static("TH1_Logic.Net.LobbyManager", "<Instance>k__BackingField");
        var lobby = N.Get<IntPtr>(manager, "Lobby");
        if (lobby != IntPtr.Zero)
        {
            var known = N.Method(lobby, "exs", 0);
            byte flags = Marshal.ReadByte(known, 0x53);
            if ((flags & 0x70) == 0x30 && Marshal.ReadIntPtr(known, 0x58) != IntPtr.Zero
                && Marshal.ReadIntPtr(known, 0x60) == Marshal.ReadIntPtr(known)
                && Marshal.ReadIntPtr(known, 0x68) == Marshal.ReadIntPtr(known, 8))
            {
                HybridCLRCompat.UseCompactInterpreterFlags = true;
                Battle.Log.LogInfo("原生聊天路由：已确认 HybridCLR compact 方法布局");
            }
        }
        var method = R.FindMethod("TH1_Logic.Steam.GameNetSender", "nkk", 2);
        if (method == IntPtr.Zero || !HybridCLRCompat.IsInterpreterMethod(method))
            throw new InvalidOperationException("无法安全安装原生队内聊天路由");
        chatReplacement = (self, recipient, data) =>
        {
            bool sent = chatOriginal(self, recipient, data);
            try { if (sent) CaptureDelivery(recipient, data); }
            catch (Exception e) { Battle.Log.LogWarning("队内消息记录失败：" + e.Message); }
            return sent;
        };
        chatHook = HookEngine.Hook(method, chatReplacement, "native-team-chat");
        if (!chatHook.IsValid) throw new InvalidOperationException("无法安装原生队内聊天路由");
        chatOriginal = chatHook.GetOriginal<ChatCall>();
        chatHookInstalled = true;
        N.RefreshInterpreterCalls("TH1_Logic.Chat.ChatSession");
        N.RefreshInterpreterCalls("TH1_Logic.Steam.GameNetSender");
    }
    void CaptureDelivery(ulong recipient, IntPtr data)
    {
        if (!ModelsEnabled || data == IntPtr.Zero || N.Class(data) != R.FindClass("TH1_Logic.Chat.ChatDelivery") || N.Get<byte>(data, "Channel") != 1) return;
        int team = N.Get<int>(data, "Team");
        var slots = Slots(N.Get<IntPtr>(map, "MapConfig"));
        ulong sender = N.Get<ulong>(data, "Sender");
        if (team <= 0 || !slots.Any(s => s.MemberId == sender && s.TeamId == team)
            || !slots.Any(s => s.MemberId == recipient && s.TeamId == team)) return;
        string content = N.TextField(data, "Text");
        if (sender == hostMemberId && content.StartsWith("[房主代 AI 席位 ", StringComparison.Ordinal)) return;
        if (!teamInboxes.TryGetValue(team, out var inbox)) teamInboxes[team] = inbox = new();
        string id = N.Get<Guid>(data, "MessageId").ToString("N");
        if (!teamMessageTimes.ContainsKey(id)) teamMessageTimes[id] = Time.realtimeSinceStartupAsDouble;
        inbox.Add(new ChatObservation(id, sender, "盟友", "team", team, content));
        if (teamMessageTimes.Count > 500) teamMessageTimes.Clear();
    }

    readonly Dictionary<uint, int> seats = new();
    RoomConfig config;
    IntPtr map;
    ulong lobbyId, hostMemberId;
    string commandId = "", modelError = "";
    float lastStatus;
    bool active, joined, started, models, desiredModels;

    internal NativeAiBridge(string root, Battle battle)
    {
        this.root = root;
        this.battle = battle;
    }

    internal bool Active => active;
    internal bool ModelsEnabled => active && started && models;
    internal int Seat(uint playerId) => active && seats.TryGetValue(playerId, out int seat) ? seat : 0;
    internal string ModelName(uint playerId)
    {
        int seat = ModelsEnabled ? Seat(playerId) : 0;
        return seat > 0 && seat <= config.AIs.Count ? config.AIs[seat - 1].Model : "";
    }
    internal uint[] ControlledPlayers() => ModelsEnabled ? seats.Keys.ToArray() : Array.Empty<uint>();
    IntPtr ChatSession()
    {
        if (!ModelsEnabled || hostMemberId == 0) return IntPtr.Zero;
        var manager = N.Static("TH1_Logic.Chat.ChatManager", "Instance");
        var session = N.Get<IntPtr>(manager, "<Session>k__BackingField");
        return session != IntPtr.Zero && N.Get<ulong>(session, "Self") == hostMemberId ? session : IntPtr.Zero;
    }
    int Team(uint playerId) => Slots(N.Get<IntPtr>(map, "MapConfig"))
        .Where(s => s.PlayerId == playerId).Select(s => s.TeamId).FirstOrDefault();
    internal string[] ChatChannels(uint playerId)
    {
        if (Seat(playerId) == 0) return Array.Empty<string>();
        var session = ChatSession();
        var context = N.Get<IntPtr>(session, "<Context>k__BackingField");
        if (context == IntPtr.Zero) return Array.Empty<string>();
        int team = Team(playerId);
        return team > 0 && (chatHookInstalled || team == N.Get<int>(context, "Team"))
            && Slots(N.Get<IntPtr>(map, "MapConfig")).Any(s => s.MemberId != 0 && s.TeamId == team)
            ? new[] { "public", "team" } : new[] { "public" };
    }
    internal ChatParticipant[] ChatParticipants() => !ModelsEnabled ? Array.Empty<ChatParticipant>()
        : Slots(N.Get<IntPtr>(map, "MapConfig"))
            .Select(s => new ChatParticipant(s.PlayerId, s.MemberId, s.MemberId == 0 ? $"AI 席位 {s.Index + 1}" : $"玩家席位 {s.Index + 1}", s.TeamId)).ToArray();
    internal ChatObservation[] ChatHistory(uint playerId)
    {
        if (Seat(playerId) == 0) return Array.Empty<ChatObservation>();
        var session = ChatSession();
        var history = N.Get<IntPtr>(session, "History");
        int team = Team(playerId);
        bool sameTeam = ChatActions.CanRelayTeam(team, N.Get<int>(N.Get<IntPtr>(session, "<Context>k__BackingField"), "Team"));
        var local = N.Items(N.Get<IntPtr>(history, "_entries"))
            .Where(entry => entry != IntPtr.Zero && (N.Get<ulong>(entry, "Sender") != hostMemberId
                || !N.TextField(entry, "Text").StartsWith("[房主代 AI 席位 ", StringComparison.Ordinal)))
            .Where(entry => N.Get<byte>(entry, "Channel") == 0
                || sameTeam && N.Get<byte>(entry, "Channel") == 1 && N.Get<int>(entry, "Team") == team)
            .TakeLast(20)
            .Select(entry => (Message: new ChatObservation(N.Get<Guid>(entry, "MessageId").ToString("N"),
                N.Get<ulong>(entry, "Sender"), N.Get<byte>(entry, "Channel") == 1 ? "盟友" : "房间玩家",
                N.Get<byte>(entry, "Channel") == 1 ? "team" : "public", N.Get<int>(entry, "Team"), N.TextField(entry, "Text")), At: N.Get<double>(entry, "CreatedAt")))
            .ToArray();
        var relayed = teamInboxes.TryGetValue(team, out var inbox) ? inbox.Visible(team) : Array.Empty<ChatObservation>();
        return local.Concat(relayed.Select(m => (Message: m, At: teamMessageTimes.TryGetValue(m.MessageId, out double at) ? at : 0)))
            .GroupBy(x => (x.Message.SenderMemberId, x.Message.MessageId)).Select(g => g.First())
            .OrderBy(x => x.At).TakeLast(20).Select(x => x.Message).ToArray();
    }
    internal bool SendChat(uint playerId, string channel, string text, out string result)
    {
        if (!ChatChannels(playerId).Contains(channel))
        {
            result = "该频道不可用，聊天尚未就绪或房主与 AI 不满足同盟转发条件";
            return false;
        }
        string message = $"[房主代 AI 席位 {Seat(playerId)} 转发] {text.Trim()}";
        ChatActions.ValidateMessage(message);
        var session = ChatSession();
        var context = N.Get<IntPtr>(session, "<Context>k__BackingField");
        if (channel == "team" && Team(playerId) != N.Get<int>(context, "Team"))
            return SendCrossTeam(playerId, message, context, out result);
        var manager = N.Static("TH1_Logic.Chat.ChatManager", "Instance");
        byte nativeChannel = channel == "team" ? (byte)1 : (byte)0;
        var response = N.Call(manager, "sqb", (IntPtr)(&nativeChannel), R.CreateIl2CppString(message));
        bool sent = N.Get<bool>(response, "Submitted");
        result = sent ? "房主已提交标注身份的消息" : "原生聊天拒绝发送：" + N.EnumField(response, "Error");
        return sent;
    }

    bool SendCrossTeam(uint playerId, string message, IntPtr context, out string result)
    {
        if (context == IntPtr.Zero || Time.realtimeSinceStartup < nextRelay)
        {
            result = "队内聊天尚未就绪或发送过快";
            return false;
        }
        int team = Team(playerId);
        var lobby = N.Get<IntPtr>(N.Static("TH1_Logic.Net.LobbyManager", "<Instance>k__BackingField"), "Lobby");
        var recipients = Slots(N.Get<IntPtr>(map, "MapConfig")).Where(s => s.MemberId != 0 && s.TeamId == team).Select(s => s.MemberId).Distinct().ToArray();
        if (lobby == IntPtr.Zero || recipients.Length == 0) { result = "没有同队真人玩家"; return false; }
        var delivery = N.New("TH1_Logic.Chat.ChatDelivery");
        uint handle = N.il2cpp_gchandle_new(delivery, false);
        try
        {
            N.Set(delivery, "Session", N.Get<Guid>(context, "Session"));
            N.Set(delivery, "Revision", N.Get<ulong>(context, "Revision"));
            N.Set(delivery, "MessageId", Guid.NewGuid());
            N.Set(delivery, "Sender", hostMemberId);
            N.Set(delivery, "Channel", (byte)1);
            N.Set(delivery, "Team", team);
            N.SetText(delivery, "Text", message);
            var sender = N.Static("TH1_Logic.Steam.GameNetSender", "Instance");
            int sent = 0;
            foreach (ulong id in recipients)
            {
                ulong recipient = id;
                if (N.Value<bool>(lobby, "ezb", (IntPtr)(&recipient)) && N.Value<bool>(sender, "nkk", (IntPtr)(&recipient), delivery)) sent++;
            }
            nextRelay = Time.realtimeSinceStartup + 1.05f;
            result = $"队内消息已提交给 {sent}/{recipients.Length} 名真人盟友";
            return sent == recipients.Length;
        }
        finally { N.il2cpp_gchandle_free(handle); }
    }

    internal void ModelStopped(string message)
    {
        if (!models) return;
        models = false;
        modelError = message;
        State("model-error", message);
    }

    internal void Poll(RoomConfig next)
    {
        if (next?.NativeLobby != true || !next.Published)
        {
            Deactivate();
            return;
        }
        if (!active || config.Revision != next.Revision)
        {
            next.Validate();
            EndMatch();
            config = next;
            desiredModels = next.UseModels;
            modelError = "";
            commandId = RoomFiles.Read<RoomCommand>(root, "room-command.json")?.Id ?? "";
            active = true;
            lastStatus = 0;
        }
        var manager = N.Static("TH1_Logic.Net.LobbyManager", "<Instance>k__BackingField");
        var lobby = N.Get<IntPtr>(manager, "Lobby");
        bool host = lobby != IntPtr.Zero && N.Class(lobby) == R.FindClass("TH1_Logic.Steam.SteamLobbyManager")
            && N.Value<bool>(lobby, "ezt") && N.Value<bool>(lobby, "ezs") && !N.Value<bool>(lobby, "get_ufn");
        ulong currentId = host ? N.Value<ulong>(lobby, "ewm") : 0;
        ulong hostMember = host ? N.Value<ulong>(lobby, "ezp") : 0;
        if (!host || currentId == 0 || hostMember == 0)
        {
            EndMatch();
            joined = false;
            lobbyId = 0;
            hostMemberId = 0;
            Status("waiting", "请在游戏中建立原生 Steam 房间，并由房主添加 AI 席位。");
            return;
        }
        if (lobbyId != currentId)
        {
            EndMatch();
            lobbyId = currentId;
        }
        joined = true;
        hostMemberId = hostMember;
        var main = N.Static("TH1_Logic.Core.Main", "<Instance>k__BackingField");
        var nativeConfig = N.Get<IntPtr>(main, "MapConfig");
        var available = Slots(nativeConfig);
        var gameMap = N.Static("TH1_Logic.Core.Main", "MapData");
        var logic = N.Get<IntPtr>(main, "GameLogic");
        int state = N.Value<int>(logic, "gtw");
        bool playing = state != N.EnumValues("Logic.GameState")["Menu"]
            && state != N.EnumValues("Logic.GameState")["Finished"];
        bool match = playing && gameMap != IntPtr.Zero
            && N.EnumField(N.Get<IntPtr>(gameMap, "Net"), "Mode") == "Multi"
            && Slots(N.Get<IntPtr>(gameMap, "MapConfig")).Any(s => s.MemberId == hostMember);
        if (!match)
        {
            EndMatch();
            int availableCount = NativeAiSeats.Available(available);
            string message = availableCount < config.AIs.Count
                ? $"原生房间仅有 {availableCount} 个 AI 席位，配置要求 {config.AIs.Count} 个；请在游戏中增加 AI 席位。"
                : $"真实房间已连接：准备接管 {config.AIs.Count} 个 AI 席位；请在游戏内开始对局。";
            Status("waiting", message, ai: availableCount);
        }
        else
        {
            if (started && gameMap != map) EndMatch();
            if (!started)
            {
                var gameSlots = Slots(N.Get<IntPtr>(gameMap, "MapConfig"));
                try
                {
                    foreach (var item in NativeAiSeats.Assign(gameSlots, config.AIs.Count))
                        seats.Add(item.Key, item.Value);
                }
                catch (InvalidOperationException e)
                {
                    seats.Clear();
                    Status("waiting", "无法接管现有 AI 席位：" + e.Message, ai: NativeAiSeats.Available(gameSlots));
                    HandleCommand();
                    return;
                }
                map = gameMap;
                started = true;
                modelError = "";
                if (desiredModels) StartModels();
            }
            uint mapId = N.Get<uint>(gameMap, "MapID");
            uint human = Slots(N.Get<IntPtr>(gameMap, "MapConfig"))
                .Where(s => s.MemberId == hostMember).Select(s => s.PlayerId).FirstOrDefault();
            string status = modelError.Length > 0 ? "model-error" : models ? "playing" : "native";
            string message = modelError.Length > 0 ? modelError
                : models ? $"已接管真实房间的 {seats.Count} 个 AI 席位" + (chatHookInstalled ? "" : "；跨队聊天路由不可用")
                : "真实房间正常进行中，AI 席位使用原生逻辑";
            Status(status, message, mapId, human, seats.Count, N.Get<int>(N.Get<IntPtr>(gameMap, "Net"), "Mode"));
        }
        HandleCommand();
    }

    static NativeAiSeat[] Slots(IntPtr config)
    {
        if (config == IntPtr.Zero) return Array.Empty<NativeAiSeat>();
        return N.Items(N.Get<IntPtr>(config, "MultiCivs"))
            .Where(s => s != IntPtr.Zero)
            .Select(s => new NativeAiSeat(N.Get<int>(s, "Index"), N.Get<ulong>(s, "MemberId"),
                N.Get<uint>(s, "PlayerId"), N.Get<bool>(s, "IsAI"), N.Get<bool>(s, "IsHostControlled"), N.Get<int>(s, "TeamId")))
            .ToArray();
    }

    void HandleCommand()
    {
        var command = RoomFiles.Read<RoomCommand>(root, "room-command.json");
        if (command == null || command.Id == commandId) return;
        commandId = command.Id;
        switch (command.Action)
        {
            case "models":
                desiredModels = true;
                modelError = "";
                if (started && !models) StartModels();
                break;
            case "native":
                desiredModels = false;
                if (models)
                {
                    models = false;
                    battle.StopModels();
                }
                modelError = "";
                break;
        }
        lastStatus = 0;
    }

    void StartModels()
    {
        try { InstallChatHook(); }
        catch (Exception e)
        {
            if (chatHookInstalled)
            {
                chatHook.Unhook();
                chatHookInstalled = false;
                N.RefreshInterpreterCalls("TH1_Logic.Chat.ChatSession");
                N.RefreshInterpreterCalls("TH1_Logic.Steam.GameNetSender");
            }
            Battle.Log.LogWarning("跨队聊天路由不可用，模型接管继续运行：" + e.Message);
        }
        models = true;
        battle.StartModels();
        Battle.Log.LogInfo($"Native Steam lobby AI control enabled: lobby={lobbyId} map=0x{map:X} seats={seats.Count}");
    }

    void EndMatch()
    {
        if (models)
        {
            models = false;
            battle.StopModels();
        }
        started = false;
        map = IntPtr.Zero;
        seats.Clear();
        teamInboxes.Clear();
        teamMessageTimes.Clear();
        if (chatHookInstalled)
        {
            chatHook.Unhook();
            chatHookInstalled = false;
            N.RefreshInterpreterCalls("TH1_Logic.Chat.ChatSession");
            N.RefreshInterpreterCalls("TH1_Logic.Steam.GameNetSender");
        }
    }

    void Deactivate()
    {
        if (!active) return;
        EndMatch();
        active = joined = desiredModels = false;
        lobbyId = hostMemberId = 0;
        State("ready", "未启用 AI 接管；原生房间和原生 AI 不受 Mod 干预。");
    }

    void Status(string state, string message, uint mapId = 0, uint human = 0, int ai = 0, int mode = 0)
    {
        if (lastStatus != 0 && Time.realtimeSinceStartup - lastStatus < 1) return;
        lastStatus = Time.realtimeSinceStartup;
        State(state, message, mapId, human, ai, mode);
    }

    void State(string state, string message, uint mapId = 0, uint human = 0, int ai = 0, int mode = 0)
    {
        try
        {
            RoomFiles.Write(root, "room-status.json",
                new RoomStatus(state, message, joined, started, mapId, human, ai, mode, models));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Battle.Log.LogWarning("真实房间 AI 接管状态写入失败：" + e.Message);
        }
    }

    internal void Dispose() => Deactivate();
}
