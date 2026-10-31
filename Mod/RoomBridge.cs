using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using IL2CppSharp;
using R = IL2CppSharp.Il2CppReflection;
using N = TohotopiaMod.Native;
using TohotopiaShared;
namespace TohotopiaMod;

internal sealed class RoomBridge
{
    readonly string root;
    readonly Battle battle;
    readonly NativeAiBridge native;
    VirtualLobby virtualLobby;
    IntPtr lobby, view, virtualRow, listingHall;
    GameObject listingObject;
    UnityEngine.Events.UnityAction joinAction;
    bool joined, started, models, joinRequested;
    float lastStatus;
    string commandId = "";
    RoomConfig config;
    readonly Dictionary<uint, int> seats = new();
    public RoomBridge(string root, Battle battle)
    {
        this.root = root;
        this.battle = battle;
        native = new NativeAiBridge(root, battle);
        State("ready", "请在 WinForms 配置原生房间 AI 接管，并在游戏内建立房间。");
    }
    public int Seat(uint playerId) => native.Active ? native.Seat(playerId) : seats.TryGetValue(playerId, out var s) ? s : 0;
    internal uint[] ControlledPlayers() => native.Active ? native.ControlledPlayers() : models && started ? seats.Keys.ToArray() : Array.Empty<uint>();
    internal string[] ChatChannels(uint playerId) => native.Active ? native.ChatChannels(playerId) : virtualLobby?.Chat.Channels(Seat(playerId)) ?? Array.Empty<string>();
    internal TohotopiaShared.ChatObservation[] ChatHistory(uint playerId) => native.Active ? native.ChatHistory(playerId) : virtualLobby?.Chat.Observe(Seat(playerId)) ?? Array.Empty<TohotopiaShared.ChatObservation>();
    internal bool IsOwnMessage(uint playerId, ulong memberId) => !native.Active && memberId == VirtualP2P.MemberId(Seat(playerId));
    internal bool SendChat(uint playerId, string channel, string text, out string result)
    {
        return native.Active ? native.SendChat(playerId, channel, text, out result)
            : virtualLobby.Chat.Send(Seat(playerId), channel, text, out result);
    }
    internal TohotopiaShared.ChatParticipant[] ChatParticipants()
    {
        if (native.Active) return native.ChatParticipants();
        if (virtualLobby == null || !joined || !started)
            return Array.Empty<TohotopiaShared.ChatParticipant>();
        var map = N.Static("TH1_Logic.Core.Main", "MapData");
        var slots = N.Items(N.Get<IntPtr>(N.Get<IntPtr>(map, "MapConfig"), "MultiCivs"));
        return slots.Select(slot =>
        {
            ulong member = N.Get<ulong>(slot, "MemberId");
            return new TohotopiaShared.ChatParticipant(N.Get<uint>(slot, "PlayerId"), member,
                virtualLobby.Transport.Members.TryGetValue(member, out var name) ? name : "玩家", N.Get<int>(slot, "TeamId"));
        }).ToArray();
    }
    internal string DeliverDecision(uint playerId, string payload) => native.Active ? payload : virtualLobby.DeliverDecision(Seat(playerId), payload);
    public bool ModelsEnabled => native.Active ? native.ModelsEnabled : models && started;
    internal bool NativeLobbyActive => native.Active;
    internal bool SpectatorMode => joined && config?.SpectatorMode == true;
    internal void ExecuteModelAction(Action action)
    {
        if (native.Active) action();
        else virtualLobby.ExecuteModelAction(action);
    }
    string modelError = "";
    public void ModelStopped(string message)
    {
        if (native.Active) { native.ModelStopped(message); return; }
        if (!models)
            return;
        models = false;
        modelError = message;
        Battle.Log.LogError(message);
        State("model-error", message);
    }
    bool statusWriteFailed;
    void State(string state, string message, uint map = 0, uint human = 0, int ai = 0, int mode = 0)
    {
        try
        {
            RoomFiles.Write(root, "room-status.json", new RoomStatus(state, message, joined, started, map, human, ai, mode, models, config?.SpectatorMode == true));
            statusWriteFailed = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Status reporting must not disable model control. Poll retries next second.
            if (!statusWriteFailed)
                Battle.Log.LogWarning("房间状态写入失败，将在下次轮询重试：" + Path.Combine(root, "room-status.json") + "；" + e.Message);
            statusWriteFailed = true;
        }
    }
    public void Poll()
    {
        var controller = N.Static("TH1_UI.Core.ViewControllerManager", "_outsideMultiplayController");
        view = N.Get<IntPtr>(controller, "_script");
        bool shown = view != IntPtr.Zero && N.Get<bool>(view, "_show");
        battle.SettingsPoll(shown);
        var next = RoomFiles.Read<RoomConfig>(root, "room.json");
        if (!joined && (native.Active || next?.NativeLobby == true))
        {
            if (listingObject != null) listingObject.SetActive(false);
            native.Poll(next);
            return;
        }
        if (!joined && next?.Published == true && !next.NativeLobby)
        {
            if (listingObject != null) listingObject.SetActive(false);
            if (Time.realtimeSinceStartup - lastStatus > 1)
            {
                lastStatus = Time.realtimeSinceStartup;
                State("ready", "旧版虚拟房间配置已停用，请在 WinForms 保存原生房间 AI 接管配置。");
            }
            return;
        }
        if (!joined && (next == null || !next.Published))
        {
            joinRequested = false;
            if (listingObject != null)
                listingObject.SetActive(false);
            return;
        }
        if (!joined)
        {
            next.Validate();
            config = next;
        }
        var manager = N.Static("TH1_Logic.Net.LobbyManager", "<Instance>k__BackingField");
        lobby = N.Get<IntPtr>(manager, "Lobby");
        if (lobby == IntPtr.Zero)
            return;
        virtualLobby?.Poll();
        if (joined && !N.Value<bool>(lobby, "ezt"))
        {
            bool wasSpectating = SpectatorMode && started;
            joined = false;
            started = false;
            models = false;
            seats.Clear();
            battle.StopModels();
            if (wasSpectating) ReturnToMenu();
            ReleaseVirtualLobby();
            State("left", "已离开房间");
            return;
        }
        if (!joined && ReadyView(shown))
        {
            PublishListing();
        }
        else if (listingObject != null)
            listingObject.SetActive(false);
        // The UI callback only requests entry. Install hooks on the next Update,
        // after the native button event and hall rendering have returned.
        if (joinRequested && !joined)
        {
            joinRequested = false;
            Join();
        }
        if (joined && view != IntPtr.Zero && !started)
        {
            NameRows();
            HideRoomControls();
        }
        var command = RoomFiles.Read<RoomCommand>(root, "room-command.json");
        if (command != null && command.Id != commandId)
        {
            commandId = command.Id;
            switch (command.Action)
            {
                case "start":
                    if (!joined)
                    {
                        State("waiting", "请先从游戏联机列表加入虚拟房间");
                        break;
                    }
                    if (started)
                        break;
                    Configure();
                    var main = N.Static("TH1_Logic.Core.Main", "<Instance>k__BackingField");
                    if (!N.Value<bool>(main, "MainMemberStartMatch"))
                    {
                        State("failed", "游戏拒绝启动联机对局，详情见 BepInEx 日志");
                        break;
                    }
                    started = true;
                    MapStarted();
                    HideLobby();
                    break;
                case "models":
                    if (!started)
                    {
                        State("waiting", "请先验证并启动联机对局");
                        break;
                    }
                    modelError = "";
                    models = true;
                    battle.StartModels();
                    State("playing", "已启用模型对战");
                    break;
                case "native":
                    if (SpectatorMode)
                    {
                        State("spectating", "观战模式由模型控制全部参赛文明；需要原生 AI 验证时请另建普通房间。");
                        break;
                    }
                    modelError = "";
                    models = false;
                    battle.StopModels();
                    State("playing", "已恢复原生 AI");
                    break;
                case "leave":
                    bool returnFromSpectator = SpectatorMode && started;
                    modelError = "";
                    models = false;
                    battle.StopModels();
                    N.Call(lobby, "ewh");
                    if (returnFromSpectator) ReturnToMenu();
                    joined = false;
                    started = false;
                    seats.Clear();
                    ReleaseVirtualLobby();
                    State("left", "已离开房间");
                    break;
            }
        }
        if (started && Time.realtimeSinceStartup - lastStatus > 1)
        {
            lastStatus = Time.realtimeSinceStartup;
            if (modelError.Length > 0)
                State("model-error", modelError);
            else
                MapStarted(false);
        }
    }
    float viewReadyAt; IntPtr observedView;
    static void ReturnToMenu()
    {
        var main = N.Static("TH1_Logic.Core.Main", "<Instance>k__BackingField");
        var logic = N.Get<IntPtr>(main, "GameLogic");
        if (logic != IntPtr.Zero) N.UIntCall(logic, "gtv", (uint)N.EnumValues("Logic.GameState")["Menu"]);
    }
    bool ReadyView(bool shown)
    {
        if (!shown || view != observedView)
        {
            observedView = view;
            viewReadyAt = Time.realtimeSinceStartup + 0.6f;
            return false;
        }
        return Time.realtimeSinceStartup >= viewReadyAt && N.Get<IntPtr>(view, "_lobby") != IntPtr.Zero && N.Get<IntPtr>(view, "_lobbyRowList") != IntPtr.Zero && N.Get<IntPtr>(view, "HallRowPrefab") != IntPtr.Zero && N.Get<IntPtr>(view, "HallList") != IntPtr.Zero;
    }
    void PublishListing()
    {
        var hall = N.Get<IntPtr>(view, "HallList");
        if (listingObject == null || listingHall != hall)
        {
            ClearListing();
            var prefab = new GameObject(N.Get<IntPtr>(view, "HallRowPrefab"));
            listingObject = UnityEngine.Object.Instantiate(prefab, new GameObject(hall).transform);
            listingObject.name = "VirtualRoomRow";
            listingHall = hall;
            var rowClass = R.FindClass("UIOutsideMultiplayLobbyRowMono");
            foreach (var component in listingObject.GetComponents<Component>())
                if (component != null && N.Class(component.Pointer) == rowClass)
                    virtualRow = component.Pointer;
            if (virtualRow == IntPtr.Zero)
            {
                ClearListing();
                throw new InvalidOperationException("Virtual 房间预制体缺少列表行组件");
            }
            // This row belongs only to the mod. It is never added to Steam's
            // _lobbyListInfos/_lobbyRowList and never calls the native drs renderer.
            var button = new Button(N.Get<IntPtr>(virtualRow, "JoinButton"));
            button.onClick.RemoveAllListeners();
            joinAction = (UnityEngine.Events.UnityAction)(() =>
            {
                if (!joined)
                    joinRequested = true;
            });
            button.onClick.AddListener(joinAction);
            foreach (string field in new[] { "SpectatorButton", "ActionButton" })
            {
                var pointer = N.Get<IntPtr>(virtualRow, field);
                if (pointer != IntPtr.Zero)
                    new Button(pointer).gameObject.SetActive(false);
            }
            var lockPointer = N.Get<IntPtr>(virtualRow, "Lock");
            if (lockPointer != IntPtr.Zero)
                new GameObject(lockPointer).SetActive(false);
            Battle.Log.LogInfo("Virtual hall row created; Steam lists unchanged; networking inactive");
        }
        listingObject.SetActive(true);
        SetLabel(virtualRow, "RoomNameText", config.Name);
        SetLabel(virtualRow, "OwnerNameText", "本地模型房间 · Virtual");
        SetLabel(virtualRow, "PlayerCountText", config.SpectatorMode ? config.AIs.Count + " 模型 · 观战" : "1+" + config.AIs.Count + "AI");
        SetLabel(virtualRow, "GameStateText", "等待加入");
        SetLabel(virtualRow, "VersionText", "Virtual");
        new Button(N.Get<IntPtr>(virtualRow, "JoinButton")).interactable = !joinRequested;
    }
    void ClearListing()
    {
        if (listingObject != null)
        {
            if (virtualRow != IntPtr.Zero)
                new Button(N.Get<IntPtr>(virtualRow, "JoinButton")).onClick.RemoveAllListeners();
            UnityEngine.Object.Destroy(listingObject);
        }
        listingObject = null;
        virtualRow = IntPtr.Zero;
        listingHall = IntPtr.Zero;
        joinAction = null;
    }
    void ReleaseVirtualLobby()
    {
        if (virtualLobby == null)
            return;
        var session = virtualLobby;
        virtualLobby = null;
        try
        {
            session.Restore(view);
        }
        finally { session.Dispose(); }
        if (view != IntPtr.Zero)
            N.Call(view, "czf");
    }
    static void SetLabel(IntPtr row, string field, string text)
    {
        var p = N.Get<IntPtr>(row, field);
        if (p != IntPtr.Zero)
        {
            var label = new TextMeshProUGUI(p);
            label.text = text;
        }
    }
    void Join()
    {
        if (joined)
            return;
        config.Validate();
        virtualLobby ??= new VirtualLobby();
        try
        {
            virtualLobby.Join(config, view);
            lobby = virtualLobby.Pointer;
            joined = true;
            Configure();
            State("joined", config.SpectatorMode ? "已加入观战房间：" + config.AIs.Count + " 名模型玩家，人类不参战" : "已加入 Virtual 房间：1 名玩家 + " + config.AIs.Count + " 名模型玩家");
            Battle.Log.LogInfo("Virtual room joined; no Steam lobby or P2P session created");
        }
        catch (Exception error)
        {
            joined = false;
            ReleaseVirtualLobby();
            State("failed", "Virtual 房间加入失败：" + error.Message);
            Battle.Log.LogError(error);
        }
    }
    void Configure()
    {
        Battle.Log.LogInfo("Room configure begin");
        config.Validate();
        var main = N.Static("TH1_Logic.Core.Main", "<Instance>k__BackingField");
        var cfg = N.Get<IntPtr>(main, "MapConfig");
        var gamePlayers = config.Players();
        virtualLobby.ConfigureMap(cfg);
        N.Set(cfg, "PlayerCount", (uint)gamePlayers.Count);
        N.Set(cfg, "Width", (uint)config.Width);
        N.Set(cfg, "Height", (uint)config.Height);
        N.SetInt(cfg, "MapSeed", config.Seed);
        N.SetInt(cfg, "WaterType", config.WaterType);
        N.SetInt(cfg, "GameMode", config.GameMode);
        N.SetInt(cfg, "CityStateCount", config.CityStates);
        N.SetBool(cfg, "EnableBarbarianCamps", config.BarbarianCamps);
        N.SetBool(cfg, "IsCustomMap", false);
        N.SetBool(cfg, "IsLimitTime", false);
        N.SetBool(cfg, "IsResumeArchiveSelected", false);
        N.Set(cfg, "selfCivId", gamePlayers[0].Civilization);
        N.Set(cfg, "selfForceId", gamePlayers[0].Civilization);
        var slots = N.Get<IntPtr>(cfg, "MultiCivs");
        N.Call(slots, "Clear");
        foreach (var entry in gamePlayers)
        {
            int i = entry.Index;
            Battle.Log.LogInfo("Room slot allocate " + i);
            var slot = N.New("RuntimeData.MemberCiv");
            Battle.Log.LogInfo("Room slot allocated " + i);
            uint civ = entry.Civilization;
            N.SetInt(slot, "Index", i);
            N.Set(slot, "MemberId", VirtualP2P.MemberId(entry.ModelSeat));
            N.Set(slot, "CivId", civ);
            N.Set(slot, "ForceId", civ);
            N.SetInt(slot, "TeamId", entry.TeamId);
            N.SetBool(slot, "IsAI", false);
            N.SetBool(slot, "IsReady", true);
            N.SetBool(slot, "IsCivFixed", true);
            Battle.Log.LogInfo("Room slot identity " + i);
            N.Call(slot, "mio");
            N.Call(slots, "Add", slot);
            Battle.Log.LogInfo("Room slot ready " + i);
        }
        Battle.Log.LogInfo("Room slots normalize");
        int mode = 2;
        N.UIntCall(cfg, "ftt", (uint)mode);
        if (view != IntPtr.Zero)
        {
            N.SetInt(view, "_roomSeatCount", gamePlayers.Count);
            N.SetBool(view, "_seedSessionInitialized", true);
            N.Call(view, "czf");
            ShowVirtualRoom();
            NameRows();
            HideRoomControls();
        }
        Battle.Log.LogInfo("Virtual room configured: human=" + (config.SpectatorMode ? 0 : 1) + " AI=" + config.AIs.Count);
    }
    void NameRows()
    {
        var data = N.Items(N.Get<IntPtr>(view, "_roomMemberRows"));
        var rows = N.Items(N.Get<IntPtr>(view, "_memberRowList"));
        int rowIndex = 0;
        foreach (var item in data)
        {
            var slot = N.Get<IntPtr>(item, "MemberCiv");
            if (slot == IntPtr.Zero)
                continue;
            if (rowIndex >= rows.Count)
                break;
            var row = rows[rowIndex++];
            int index = virtualLobby.Transport.ModelSeat(N.Get<ulong>(slot, "MemberId"));
            if (index <= 0 || index > config.AIs.Count)
                continue;
            var text = N.Get<IntPtr>(row, "NameText");
            if (text != IntPtr.Zero)
                new TextMeshProUGUI(text).text = config.AIs[index - 1].Model;
        }
    }
    void ShowVirtualRoom()
    {
        // Steam normally drives this transition through its lobby-entered event.
        // Virtual entry explicitly displays the room after membership is installed.
        N.SetBool(view, "_noRoom", false);
        N.SetBool(view, "_advancedConfigOverlayVisible", false);
        N.Call(view, "czi");
        var hall = N.Get<IntPtr>(view, "HallArea");
        var roomArea = N.Get<IntPtr>(view, "RoomArea");
        if (hall != IntPtr.Zero)
            new GameObject(hall).SetActive(false);
        if (roomArea != IntPtr.Zero)
            new GameObject(roomArea).SetActive(true);
        Battle.Log.LogInfo("Virtual room UI shown; member rows=" + N.Items(N.Get<IntPtr>(view, "_memberRowList")).Count);
    }
    void HideRoomControls()
    {
        foreach (string f in new[] { "StartGameButton", "AdvancedConfigButton", "ReadyButton", "CancelReadyButton" })
        {
            var p = N.Get<IntPtr>(view, f);
            if (p != IntPtr.Zero)
                new Button(p).gameObject.SetActive(false);
        }
    }
    void HideLobby()
    {
        var controller = N.Static("TH1_UI.Core.ViewControllerManager", "_outsideMultiplayController");
        if (controller != IntPtr.Zero)
            N.Call(controller, "upr");
    }
    void MapStarted(bool initial = true)
    {
        var map = N.Static("TH1_Logic.Core.Main", "MapData");
        if (map == IntPtr.Zero)
            return;
        var slots = N.Items(N.Get<IntPtr>(N.Get<IntPtr>(map, "MapConfig"), "MultiCivs"));
        seats.Clear();
        uint human = 0;
        foreach (var s in slots)
        {
            ulong member = N.Get<ulong>(s, "MemberId");
            int index = virtualLobby.Transport.ModelSeat(member);
            uint id = N.Get<uint>(s, "PlayerId");
            if (member == VirtualP2P.HostId && !config.SpectatorMode)
                human = id;
            else if (index > 0 && index <= config.AIs.Count)
                seats[id] = index;
        }
        uint mapId = N.Get<uint>(map, "MapID");
        int netMode = N.Get<int>(N.Get<IntPtr>(map, "Net"), "Mode");
        if (initial && config.SpectatorMode)
            virtualLobby.RefreshSpectatorView(map);
        string message = config.SpectatorMode ? "观战中：" + seats.Count + " 名模型玩家互相对战 · 人类不参战 · 全图只读视角" : "Virtual 联机对局：1 名玩家 + " + seats.Count + " 名模型玩家";
        State(config.SpectatorMode ? "spectating" : "playing", message + (models ? " · 模型已启用" : config.UseModels ? " · 模型正在启动" : " · 使用原生 AI 验证"), mapId, human, seats.Count, netMode);
        if (initial)
        {
            Battle.Log.LogInfo($"Virtual match started: map={mapId} NetMode={netMode} human={human} AI={seats.Count}");
            if (config.UseModels)
            {
                modelError = "";
                models = true;
                battle.StartModels();
            }
        }
    }
    public void Dispose()
    {
        native.Dispose();
        ReleaseVirtualLobby();
        ClearListing();
    }
}



