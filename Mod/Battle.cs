using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using TohotopiaMod.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using R = TohotopiaMod.GameReflection;
using N = TohotopiaMod.Native;
using TohotopiaShared;
namespace TohotopiaMod;

public partial class Battle
{
    internal static ModLog Log = new();
    public static string InstallDirectory = "";
    internal static string DataDirectory = "";
    string data; HttpClient http; bool ready, enabled, oldEditor, oldGo, nativeDirty; object previousMap, currentPlayer, currentAI;
    uint playerId, turn, eventMapId; int sequence; string requestId, status = "尚未启用 LLM 对战"; Task<string> pending; CancellationTokenSource cancellation;
    Dictionary<string, int> terrainNames, terrainFeatureNames, vegetationNames, resourceNames, techNames, actionPointNames;
    Dictionary<string, object> actions = new(); Dictionary<string, string> actionDescriptions = new(); float next; RoomBridge room;
    readonly ActionPlan plan = new();
    string feedback = "";
    readonly HashSet<string> chatCandidates = new();
    int chatsThisTurn;
    bool rulesExported;
    bool pauseHeld;
    readonly ModelTurnFallback turnFallback = new();
    readonly Dictionary<string, string> unitNameCache = new();
    JsonSerializerOptions json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    readonly SettingsLauncher settingsLauncher = new();
    public void LateUpdate()
    {
        if (ready) room?.PollTurnName(turnFallback);
    }
    public void Update()
    {
        if (GameRuntime.SettingsKeyDown())
            settingsLauncher.Open();
        bool processingModelTurn = false;
        try
        {
            if (enabled) SynchronizeTurn(true);
            if ((float)GameRuntime.Now < next)
                return;
            next = (float)GameRuntime.Now + 0.15f;
            if (!ready)
            {
                if (R.FindClass("TH1_Logic.Core.Main") == null)
                    return;
                data = DataDirectory;
                Directory.CreateDirectory(data);
                http = new HttpClient() { BaseAddress = new Uri("http://127.0.0.1:17861"), Timeout = TimeSpan.FromSeconds(620) };
                terrainNames = N.EnumValues("TerrainType");
                terrainFeatureNames = N.EnumValues("TerrainFeature");
                vegetationNames = N.EnumValues("Vegetation");
                resourceNames = N.EnumValues("ResourceType");
                techNames = N.EnumValues("TechType");
                actionPointNames = N.EnumValues("RuntimeData.ActionPointType");
                room = new RoomBridge(data, this);
                ready = true;
            }
            room.Poll();
            if (enabled) SynchronizeTurn();
            if (GameRules.ExportStep(data, 8) && !rulesExported)
            {
                rulesExported = true;
                Log.LogInfo("Chinese game rule catalog exported for model queries");
            }
            string refreshRules = Path.Combine(data, "rules-refresh.request");
            if (File.Exists(refreshRules))
            {
                File.Delete(refreshRules);
                GameRules.RequestRefresh();
                unitNameCache.Clear();
                rulesExported = false;
            }
            string preview = Path.Combine(data, "preview.request");
            if (File.Exists(preview))
            {
                File.Delete(preview);
                if (!enabled)
                {
                    var previewMap = N.Static("TH1_Logic.Core.Main", "MapData");
                    if (previewMap != null)
                    {
                        var previewPlayer = N.Call(previewMap, "get_ivn");
                        if (previewPlayer != null)
                            Request(previewMap, previewPlayer, false);
                    }
                }
            }
            if (!enabled)
                return;
            var map = N.Static("TH1_Logic.Core.Main", "MapData");
            if (map == null || previousMap != map) return;
            var main = N.Static("TH1_Logic.Core.Main", "<Instance>k__BackingField");
            if (!ExecutionReady(main, map)) return;
            PollAmbient(map);
            var ai = currentAI;
            var player = currentPlayer;
            if (!pauseHeld || !CurrentTurn()) return;
            uint pid = playerId, pt = turn;
            processingModelTurn = true;
            if (turnFallback.IsNative(pid, pt))
                return;
            if (pending != null)
            {
                if (!pending.IsCompleted)
                    return;
                var task = pending;
                pending = null;
                cancellation?.Dispose();
                cancellation = null;
                if (task.IsFaulted || task.IsCanceled)
                {
                    FallbackTurn("模型请求失败：" + (task.Exception?.GetBaseException().Message ?? "请求已取消"));
                    return;
                }
                using var reply = JsonDocument.Parse(room.DeliverDecision(pid, task.Result));
                var root = reply.RootElement;
                if (root.GetProperty("requestId").GetString() != requestId)
                {
                    FallbackTurn("收到过期决策");
                    return;
                }
                status = root.GetProperty("reason").GetString();
                var proposed = new List<PlannedStep>();
                if (root.TryGetProperty("actions", out var steps) && steps.ValueKind == JsonValueKind.Array)
                {
                    if (steps.GetArrayLength() > ActionPlan.MaxSteps)
                        throw new InvalidOperationException("动作序列过长");
                    proposed.AddRange(steps.Deserialize<List<PlannedStep>>(json) ?? []);
                }
                else
                {
                    string selected = root.GetProperty("candidateId").GetString();
                    string message = root.TryGetProperty("message", out var messageJson) ? messageJson.GetString() ?? "" : "";
                    proposed.Add(new PlannedStep(selected, message));
                }
                if (proposed.Count == 0 || proposed.Any(s => s is null || (s.CandidateId != "end_turn" && !actions.ContainsKey(s.CandidateId) && !chatCandidates.Contains(s.CandidateId))))
                {
                    FallbackTurn("模型返回空序列或未知候选");
                    return;
                }
                plan.Load(proposed);
                Event("plan_received", $"模型提供 {plan.Count} 步动作序列；{status}");
            }
            if (plan.Active)
            {
                int aiState = N.Get<int>(ai, "AILogicState");
                if (aiState != 1)
                {
                    if (aiState == 4) Cancel();
                    return;
                }
                if (sequence >= MaxDecisions())
                {
                    N.SetInt(ai, "AILogicState", 4);
                    status = "达到本回合行动上限";
                    Event("budget_end", status);
                    Cancel();
                    return;
                }
                AdvancePlan(ai, pid, pt);
                return;
            }
            if (N.Get<int>(ai, "AILogicState") != 1)
                return;
            if (sequence >= MaxDecisions())
            {
                N.SetInt(ai, "AILogicState", 4);
                status = "达到本回合行动上限";
                Event("budget_end", status);
                return;
            }
            Request(map, player);
        }
        catch (Exception e)
        {
            Log.LogError(e);
            if (processingModelTurn && enabled)
                FallbackTurn("本席位决策处理失败：" + e.Message);
            else
                Stop("LLM 已停止：" + e.Message);
        }
    }
    internal void StartModels()
    {
        Stop("");
        previousMap = N.Static("TH1_Logic.Core.Main", "MapData");
        if (previousMap == null) throw new InvalidOperationException("当前没有可接管的对局");
        eventMapId = previousMap != null ? N.Get<uint>(previousMap, "MapID") : 0;
        enabled = true;
        sequence = 0;
        chatsThisTurn = 0;
        playerId = 0;
        turn = 0;
        requestId = "";
        ResetAmbient();
        SynchronizeTurn();
        Log.LogInfo("Model control enabled");
        Event("control_enabled", "模型接管已启用；只有执行日志能证明已行动");
    }
    internal void StopModels() => Stop("原 AI 已恢复");
    void SynchronizeTurn(bool advanceDelay = false)
    {
        var map = N.Static("TH1_Logic.Core.Main", "MapData");
        var main = N.Static("TH1_Logic.Core.Main", "<Instance>k__BackingField");
        var ai = N.Get<object>(N.Get<object>(main, "GameLogic"), "_aiLogic");
        var player = map == null ? null : N.Call(map, "get_ivn");
        if (map != previousMap) CancelAmbient();
        if (player != null && ambientPlayer == N.Id(player)) CancelAmbient();
        bool controlled = map == previousMap && player != null && N.Get<int>(player, "PlayerFactionType") == 0 && room.Seat(N.Id(player)) > 0;
        if (TrackTurn(map, ai, controlled ? player : null))
            Event("turn_start", "开始 AI 回合");
        bool fallback = currentPlayer != null && turnFallback.IsNative(playerId, turn);
        Pause(ModelTakeover.Holds(controlled, fallback, currentAI != null, N.Get<int>(currentAI, "AILogicState")));
        if (advanceDelay && pauseHeld) AdvanceActionDelay(main, map);
    }
    internal bool TrackTurn(object map, object ai, object player)
    {
        if (map != previousMap) player = null;
        uint pid = player == null ? 0 : N.Id(player), pt = N.Get<uint>(player, "Turn");
        bool matches = ModelTakeover.Matches(ai, map, player);
        bool changed = currentPlayer != player || playerId != pid || turn != pt || map != previousMap
            || currentAI != null && (currentAI != ai || !matches);
        if (changed)
        {
            Cancel();
            nativeDirty = false;
            sequence = chatsThisTurn = 0;
        }
        currentPlayer = player;
        currentAI = matches ? ai : null;
        playerId = pid;
        turn = pt;
        return changed && player != null;
    }
    internal bool CurrentTurn() => currentAI != null && currentPlayer != null
        && N.Static("TH1_Logic.Core.Main", "MapData") == previousMap
        && N.Call(previousMap, "get_ivn") == currentPlayer
        && N.Get<uint>(currentPlayer, "Turn") == turn && N.Id(currentPlayer) == playerId
        && N.Get<object>(N.Get<object>(N.Static("TH1_Logic.Core.Main", "<Instance>k__BackingField"), "GameLogic"), "_aiLogic") == currentAI
        && ModelTakeover.Matches(currentAI, previousMap, currentPlayer);
    internal bool ExecutionReady(object main, object map)
    {
        var editor = N.Static("Logic.MainEditor", "Instance");
        bool editorPaused = pauseHeld ? oldEditor && !oldGo : N.Get<bool>(editor, "IsEditor") && !N.Get<bool>(editor, "IsGo");
        if (map == null || main == null || editorPaused || GameRuntime.TimeScale <= 0 || !N.Get<bool>(main, "_isStarted")
            || N.Get<object>(main, "_mapStartChoice") != null || N.Get<bool>(main, "IsNetActionExecuting")
            || !ModelTakeover.Running(N.Value<int>(N.Get<object>(main, "GameLogic"), "gtw"))) return false;
        if ((bool)R.InvokeStaticMethod(R.FindMethod("TH1_Logic.Net.MultiplayerMatchLoading", "get_wmw", 0), Array.Empty<object>())
            || (bool)R.InvokeStaticMethod(R.FindMethod("Logic.Action.ActionExecutionSafety", "rpf", 1), new[] { map })
            || (bool)R.InvokeStaticMethod(R.FindMethod("TH1_Core.Managers.PresentationManager", "get_iwf", 0), Array.Empty<object>())
            || (bool)R.InvokeStaticMethod(R.FindMethod("TH1_Core.Managers.PresentationManager", "get_zfo", 0), Array.Empty<object>())) return false;
        return true;
    }
    int MaxDecisions()
    {
        string settings = Path.Combine(data, "settings.json");
        if (!File.Exists(settings)) return 80;
        using var c = JsonDocument.Parse(File.ReadAllText(settings));
        return c.RootElement.TryGetProperty("maxDecisionsPerTurn", out var limit) ? limit.GetInt32() : 80;
    }
    internal void AdvancePlan(object ai, uint pid, uint pt)
    {
        if (ai != currentAI || pid != playerId || pt != turn || !pauseHeld || !CurrentTurn()
            || N.Get<int>(ai, "AILogicState") != 1
            || !ExecutionReady(N.Static("TH1_Logic.Core.Main", "<Instance>k__BackingField"), previousMap)) return;
        var step = plan.Current;
        string selected = step.CandidateId;
        if (ChatActions.Channel(selected) != "")
        {
            if (!chatCandidates.Contains(selected) || chatsThisTurn >= ChatActions.MaxPerTurn)
            {
                BlockPlan(selected, "聊天候选已失效或达到本回合发送上限");
                return;
            }
            ChatActions.ValidateMessage(step.Message);
            string channel = ChatActions.Channel(selected);
            bool sent = room.SendChat(pid, channel, step.Message, out var receipt);
            chatsThisTurn++;
            Event(sent ? "message_sent" : "message_rejected", $"{(channel == "public" ? "公共" : "队内")}消息：{step.Message.Trim()}；{receipt}", selected, sent ? "info" : "warning");
            if (!sent)
            {
                BlockPlan(selected, receipt);
                return;
            }
        }
        else if (selected == "end_turn")
        {
            N.SetInt(ai, "AILogicState", 4);
            Event("end_turn", "模型选择结束回合：" + status, selected);
            Cancel();
            return;
        }
        else if (!actions.TryGetValue(selected, out var action))
        {
            BlockPlan(selected, "动作候选已不存在");
            return;
        }
        else
        {
            var param = N.Get<object>(action, "Param");
            var logic = N.Get<object>(action, "ActionLogic");
            bool valid;
            try
            {
                N.Call(param, "hyi");
                valid = N.Value<bool>(logic, "fqb", param);
            }
            catch (Exception e)
            {
                BlockPlan(selected, "游戏动作预校验失败：" + e.Message);
                return;
            }
            if (!valid)
            {
                BlockPlan(selected, "游戏判定动作不再满足规则或资源条件：" + actionDescriptions.GetValueOrDefault(selected, "未知动作"));
                return;
            }
            int before = N.Get<int>(ai, "_actionCount");
            room.ExecuteModelAction(() =>
            {
                nativeDirty = true;
                N.Call(action, "htr");
                if (N.Get<bool>(action, "IsInSight"))
                    N.Call(logic, "hzo", param);
                N.Call(ai, "kof", action);
            });
            Event("executed", $"原生动作已执行：{actionDescriptions.GetValueOrDefault(selected, "未知动作")}；计数 {before}->{N.Get<int>(ai, "_actionCount")}；{status}", selected);
            Log.LogInfo($"LLM action player={pid} turn={pt} candidate={selected} count={before}->{N.Get<int>(ai, "_actionCount")}");
        }
        sequence++;
        plan.Advance();
        if (!plan.Active) ClearCandidates();
    }
    void BlockPlan(string selected, string reason)
    {
        feedback = plan.Block(reason.Length <= 500 ? reason : reason[..500]);
        status = feedback;
        Event("plan_blocked", feedback, selected, "warning");
        ClearCandidates();
        sequence++;
    }
    void Pause(bool on)
    {
        if (!pauseHeld && !on)
            return;
        var editor = N.Static("Logic.MainEditor", "Instance");
        if (on && !pauseHeld)
        {
            oldEditor = N.Get<bool>(editor, "IsEditor");
            oldGo = N.Get<bool>(editor, "IsGo");
        }
        N.SetBool(editor, "IsEditor", on || oldEditor);
        N.SetBool(editor, "IsGo", on ? false : oldGo);
        pauseHeld = on;
    }
    bool? pauseSkipsDelay;
    void AdvanceActionDelay(object main, object map)
    {
        pauseSkipsDelay ??= ActionDelay.Required(R.RequireClass("Logic.AI.AILogic"));
        if (pauseSkipsDelay != true) return;
        if (CurrentTurn() && ExecutionReady(main, map))
            ActionDelay.Advance(currentAI, false, GameRuntime.DeltaTime);
    }
    void Cancel()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        pending = null;
        plan.Clear();
        feedback = "";
        ClearCandidates();
    }
    void ClearCandidates()
    {
        actions.Clear();
        actionDescriptions.Clear();
        chatCandidates.Clear();
    }
    void Stop(string text)
    {
        if (nativeDirty) RestartNativeTurn();
        Pause(false);
        bool wasEnabled = enabled;
        enabled = false;
        nativeDirty = false;
        currentAI = currentPlayer = null;
        ResetAmbient();
        Cancel();
        turnFallback.Clear();
        status = text;
        if (wasEnabled && text.Length > 0)
        {
            Event("fallback", text, level: "warning");
            room?.ModelStopped(text);
        }
    }
    void FallbackTurn(string reason)
    {
        if (!CurrentTurn()) { Cancel(); Pause(false); return; }
        turnFallback.Fail(playerId, turn);
        Cancel();
        bool resumed = RestartNativeTurn();
        Pause(false);
        status = resumed
            ? $"AI 席位 {room.Seat(playerId)} 本回合已交还原生 AI，其他席位继续模型决策；{reason}"
            : $"AI 席位 {room.Seat(playerId)} 原生 AI 初始化失败，本回合结束；其他席位继续模型决策；{reason}";
        Log.LogWarning(status);
        Event("seat_fallback", status, level: "warning");
    }
    internal bool RestartNativeTurn()
    {
        if (!CurrentTurn()) return false;
        bool resumed = ModelTakeover.Restart(currentAI, previousMap, currentPlayer);
        nativeDirty = false;
        return resumed;
    }
    public void OnDestroy()
    {
        Stop("插件已卸载");
        room?.Dispose();
        http?.Dispose();
    }
    void Request(object map, object player, bool send = true)
    {
        ClearCandidates();
        var observation = Observe(map, player, out var visibleGrids, out var visibleUnits, out var knownPlayers);
        var candidates = new List<object>();
        var generator = R.FindClass("Logic.AI.AIActionGenerator");
        var list = R.InvokeStaticMethod(R.FindMethod(generator, "huh", 2), new[] { map, player });
        foreach (var action in N.Items(list))
        {
            var p = N.Get<object>(action, "Param");
            var logic = N.Get<object>(action, "ActionLogic");
            N.Call(p, "hyi");
            if (!N.Value<bool>(logic, "fqb", p))
                continue;
            uint uid = N.Get<uint>(p, "UnitId"), cid = N.Get<uint>(p, "CityId"), gid = N.Get<uint>(p, "GridId"), tu = N.Get<uint>(p, "TargetUnitId"), tg = N.Get<uint>(p, "TargetGridId"), tp = N.Get<uint>(p, "TargetPlayerId");
            string kind = N.TextCall(action, "htt");
            if (!Visibility.Candidate(kind, gid, tu, tg, tp, visibleGrids, visibleUnits, knownPlayers))
                continue;
            string id = "a" + candidates.Count;
            actions[id] = action;
            var actionId = N.Call(logic, "get_ixx");
            string details = N.TextCall(actionId, "hys");
            int cost = N.Value<int>(logic, "jhs", p);
            actionDescriptions[id] = $"{kind} unit={uid},city={cid},grid={gid},targetGrid={tg},targetUnit={tu}; {details}";
            candidates.Add(new
            {
                id,
                kind,
                unitId = uid,
                cityId = cid,
                gridId = gid,
                targetUnitId = tu,
                targetGridId = tg,
                targetPlayerId = tp,
                description = details + "; coinCost=" + cost + (gid != 0 && !visibleGrids.Contains(gid) ? "（前往未探索格子，地形未知）" : "")
            });
        }
        if (chatsThisTurn < ChatActions.MaxPerTurn)
            foreach (var channel in room.ChatChannels(N.Id(player)))
            {
                string id = channel == "public" ? ChatActions.PublicId : ChatActions.TeamId;
                chatCandidates.Add(id);
                candidates.Add(new
                {
                    id,
                    kind = ChatActions.Kind(id),
                    unitId = 0,
                    cityId = 0,
                    gridId = 0,
                    targetUnitId = 0,
                    targetGridId = 0,
                    targetPlayerId = 0,
                    description = (channel == "public" ? "向房间玩家发送公共消息" : "仅向同队玩家发送队内消息") + "；在回复 message 字段填写正文，不消耗金币／单位行动点；每回合最多两条，最多 2048 UTF-8 字节"
                });
            }
        candidates.Add(new
        {
            id = "end_turn",
            kind = "EndTurn",
            unitId = 0,
            cityId = 0,
            gridId = 0,
            targetUnitId = 0,
            targetGridId = 0,
            targetPlayerId = 0,
            description = "结束回合"
        });
        requestId = Guid.NewGuid().ToString("N");
        var request = new
        {
            requestId,
            sessionId = room.MatchSession,
            mapId = N.Get<uint>(map, "MapID"),
            playerId = N.Id(player),
            turn = N.Get<uint>(player, "Turn"),
            sequence,
            aiSlot = room.Seat(N.Id(player)),
            observation,
            candidates,
            feedback,
            civilizationRules = Civilization(player)
        };
        string payload = JsonSerializer.Serialize(request, json);
        File.WriteAllText(Path.Combine(data, "last-observation.json"), payload);
        if (!send)
        {
            ClearCandidates();
            Log.LogInfo("LLM preview exported");
            return;
        }
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", File.ReadAllText(Path.Combine(data, "bridge.token")).Trim());
        cancellation = new CancellationTokenSource();
        status = $"文明 {playerId} 第 {turn} 回合正在决策 · {candidates.Count} 个候选";
        Event("game_request", $"已导出可见状态并提交模型请求，可见格子={visibleGrids.Count}，可见单位={visibleUnits.Count}，候选数={candidates.Count}");
        pending = Send(payload, cancellation.Token);
        feedback = "";
    }
    void Event(string stage, string message, string candidate = "", string level = "info")
    {
        try
        {
            int seat = room?.Seat(playerId) ?? 0;
            var cfg = TohotopiaShared.RoomFiles.Read<TohotopiaShared.RoomConfig>(data, "room.json");
            var ai = seat > 0 && cfg != null && seat <= cfg.AIs.Count ? cfg.AIs[seat - 1] : null;
            TohotopiaShared.AgentEvents.Write(data, new(DateTimeOffset.UtcNow, "game", stage, message, level, eventMapId, playerId, turn, seat, ai?.Provider ?? "", ai?.Model ?? "", requestId ?? "", candidate, sequence));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { Log.LogWarning("Agent 日志写入失败：" + e.GetType().Name); }
    }
    async Task<string> Send(string body, CancellationToken ct, string endpoint = "/decision")
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var result = await http.PostAsync(endpoint, content, ct);
        if (!result.IsSuccessStatusCode)
            throw new InvalidOperationException(await result.Content.ReadAsStringAsync());
        return await result.Content.ReadAsStringAsync();
    }
    object Observe(object map, object player, out HashSet<uint> gridIds, out HashSet<uint> unitIds, out HashSet<uint> known, bool ambient = false)
    {
        gridIds = new();
        unitIds = new();
        known = new() { N.Id(player) };
        var grids = new List<object>();
        var units = new List<object>();
        var cities = new List<object>();
        var players = new List<object>();
        var sight = N.Get<object>(player, "Sight");
        var mapConfig = N.Get<object>(map, "MapConfig");
        uint mapWidth = N.Get<uint>(mapConfig, "Width"), mapHeight = N.Get<uint>(mapConfig, "Height");
        foreach (var g in N.Items(N.Get<object>(N.Get<object>(map, "GridMap"), "GridList")))
        {
            uint id = N.Id(g);
            if (!N.WithUInt(sight, "gdu", id))
                continue;
            gridIds.Add(id);
            var pos = N.Get<object>(g, "Pos");
            var buildingDef = N.Call(g, "get_tbl");
            var resourceDef = N.Call(g, "get_tbm");
            var roadDef = N.Call(g, "get_tbn");
            var landformDef = ContentDefinition(g, "_landformReference", 1);
            var vegetationDef = ContentDefinition(g, "_vegetationReference", 2);
            string terrainName = TerrainLabel(N.Get<int>(g, "Terrain"));
            int terrainFeature = N.Value<int>(g, "get_tbv");
            string landformName = ContentName(landformDef, "地形未知");
            if (landformName.Length == 0)
            {
                landformName = EnumName(terrainFeatureNames, terrainFeature);
                if (landformName is "None" or "Road" or "0") landformName = "";
            }
            string vegetationName = ContentName(vegetationDef, "植被未知");
            if (vegetationName.Length == 0) vegetationName = EnumName(vegetationNames, N.Value<int>(g, "get_tbw"));
            string buildingName = ContentName(buildingDef, "建筑未知"), resourceName = ContentName(resourceDef, "资源未知"), roadName = ContentName(roadDef, "道路未知");
            grids.Add(new
            {
                id,
                x = N.Get<int>(pos, "X"),
                y = N.Get<int>(pos, "Y"),
                // Keep the historical fields for old consumers. New consumers should use the
                // content-specific names below: get_tbx is the compatibility building-first view.
                terrain = EnumName(terrainNames, N.Get<int>(g, "Terrain")),
                resource = EnumName(resourceNames, N.Value<int>(g, "get_tby")),
                buildingLevel = N.Value<int>(g, "get_tbz"),
                building = EnumName(resourceNames, N.Value<int>(g, "get_tbx")),
                terrainName,
                landformName,
                vegetationName,
                buildingName,
                resourceName,
                roadName
            });
        }
        foreach (var u in N.Items(N.Get<object>(N.Get<object>(map, "UnitMap"), "UnitList")))
        {
            var owner = N.Call(u, "Player", map);
            uint pid = N.Id(owner), gid = N.Id(N.Call(u, "Grid", map));
            if (!Visibility.Unit(pid, N.Id(player), gid, pid != N.Id(player) && N.Value<bool>(u, "gjw", map, player), gridIds))
                continue;
            uint id = N.Id(u);
            unitIds.Add(id);
            known.Add(pid);
            var ap = new Dictionary<string, int>();
            if (pid == N.Id(player))
            {
                var points = N.Get<object>(u, "ActionPoint");
                foreach (var pair in actionPointNames)
                    if (N.WithUInt(points, "ContainsKey", (uint)pair.Value))
                        ap[pair.Key] = N.DictionaryInt(points, pair.Value);
            }
            units.Add(new
            {
                id,
                name = UnitName(u),
                definitionId = UnitDefinitionId(u),
                level = N.Value<uint>(u, "get_ivz"),
                playerId = pid,
                gridId = gid,
                health = N.Get<int>(u, "Health"),
                type = N.Value<int>(u, "get_ivx"),
                attack = N.Value<float>(u, "gij", map, null),
                defense = N.Value<float>(u, "gir", map, null),
                actionPoints = ap
            });
        }
        foreach (var c in N.Items(N.Get<object>(N.Get<object>(map, "CityMap"), "CityList")))
        {
            uint gid = N.Id(N.Call(c, "Grid", map)), pid = N.Id(N.Call(c, "Player", map));
            if (pid != N.Id(player) && !gridIds.Contains(gid))
                continue;
            known.Add(pid);
            cities.Add(new
            {
                id = N.Id(c),
                playerId = pid,
                gridId = gid,
                level = N.Get<int>(c, "Level"),
                exp = pid == N.Id(player) ? N.Get<int>(c, "LevelExp") : 0
            });
        }
        var met = N.Get<object>(player, "MeetPlayers");
        var all = N.Items(N.Get<object>(N.Get<object>(map, "PlayerMap"), "PlayerDataList"));
        foreach (var p in all)
        {
            uint id = N.Id(p);
            if (N.WithUInt(met, "Contains", id))
                known.Add(id);
            if (id == N.Id(player))
            {
                var techs = new List<string>();
                var set = N.Get<object>(N.Get<object>(p, "TechTree"), "TechSet");
                foreach (var tech in techNames)
                    if (N.WithUInt(set, "Contains", (uint)tech.Value))
                        techs.Add(tech.Key);
                players.Add(new
                {
                    id,
                    coin = N.Get<int>(p, "_playerCoin"),
                    techPoint = N.Get<int>(p, "_playerTechPoint"),
                    score = N.Get<int>(p, "PlayerScore"),
                    turn = N.Get<uint>(p, "Turn"),
                    civId = N.Get<uint>(p, "PlayerCivId"),
                    forceId = N.Get<uint>(p, "PlayerForceId"),
                    technologies = techs
                });
            }
            else if (known.Contains(id))
                players.Add(new
                {
                    id,
                    civId = N.Get<uint>(p, "PlayerCivId"),
                    forceId = N.Get<uint>(p, "PlayerForceId")
                });
        }
        return new
        {
            selfId = N.Id(player),
            matchRules = MatchRules(mapConfig),
            mapWidth,
            mapHeight,
            grids,
            units,
            cities,
            players,
            chat = new
            {
                availableChannels = room.ChatChannels(N.Id(player)),
                participants = room.ChatParticipants(),
                remainingMessagesThisTurn = ambient ? (int?)null : Math.Max(0, ChatActions.MaxPerTurn - chatsThisTurn),
                communicationAvailableOutOfTurn = ambient && room.ChatChannels(N.Id(player)).Length > 0,
                maxMessageBytes = ChatActions.MaxTextBytes,
                recentMessages = room.ChatHistory(N.Id(player))
            }
        };
    }
    static string ContentName(object definition)
    {
        if (definition == null) return "";
        try
        {
            string name = N.TextCall(definition, "get_Name");
            if (uint.TryParse(name, out uint key))
            {
                var manager = N.Static("Logic.Multilingual.MultilingualManager", "Instance");
                string localized = N.TextCallWithUInt(manager, "hoy", key);
                return uint.TryParse(localized, out _) ? "" : localized;
            }
            return name;
        }
        catch { return ""; }
    }
    static object ContentDefinition(object grid, string referenceField, int kind)
    {
        uint reference = N.Get<uint>(grid, referenceField);
        if (reference == 0) return null;
        return N.Call(grid, "smi", reference, kind);
    }
    object MatchRules(object config)
    {
        if (GameRules.ExportMatchContext(data, config))
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "match-rules.json")));
                return document.RootElement.Clone();
            }
            catch (Exception error) when (error is IOException or JsonException) { }
        return new { status = "未知", description = "当前对局胜利条件未能读取，不可根据模式名称猜测。" };
    }
    static string ContentName(object definition, string unknown)
    {
        if (definition == null) return "";
        string name = ContentName(definition);
        return string.IsNullOrWhiteSpace(name) ? unknown : name;
    }
    string TerrainLabel(int type) => EnumName(terrainNames, type) switch
    {
        "Land" => "陆地", "Sea" or "Ocean" => "海洋", "None" or "0" => "地形未知",
        var label => label
    };
    static string UnitDefinitionId(object unit)
    {
        try { return N.DefinitionId(unit, "UnitDefId"); }
        catch { return ""; }
    }
    string UnitName(object unit)
    {
        try
        {
            string definitionId = UnitDefinitionId(unit);
            if (definitionId.Length > 0 && unitNameCache.TryGetValue(definitionId, out var cached))
                return cached;
            var table = N.Static("Table", "<Instance>k__BackingField");
            var assets = N.Get<object>(table, "UnitTypeDataAssets");
            int type = N.Value<int>(unit, "get_ivx"), giant = N.Value<int>(unit, "get_ivy");
            uint level = N.Value<uint>(unit, "get_ivz");
            foreach (var info in N.Items(N.Get<object>(assets, "UnitTypeList")))
                if (definitionId.Length > 0 ? N.DefinitionId(info, "UnitDefId") == definitionId
                    : N.Get<int>(info, "UnitType") == type && N.Get<int>(info, "GiantType") == giant && N.Get<uint>(info, "UnitLevel") == level)
                {
                    var name = N.LocalizedField(info, "Name");
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        if (definitionId.Length > 0) unitNameCache[definitionId] = name;
                        return name;
                    }
                }
        }
        catch { }
        return "未知兵种";
    }
    static string EnumName(Dictionary<string, int> names, int value) => names.FirstOrDefault(p => p.Value == value).Key ?? value.ToString();
    static string LocalizedOrUnknown(object item, string field)
    {
        var text = N.LocalizedField(item, field);
        return string.IsNullOrWhiteSpace(text) ? "未知" : text;
    }
    string Civilization(object player)
    {
        uint civ = N.Get<uint>(player, "PlayerCivId"), force = N.Get<uint>(player, "PlayerForceId");
        var table = N.Static("Table", "<Instance>k__BackingField");
        var assets = N.Get<object>(table, "PlayerDataAssets");
        var text = new StringBuilder();
        foreach (var info in N.Items(N.Get<object>(assets, "PlayerDataList")))
            if (N.Get<uint>(info, "CivId") == civ && N.Get<uint>(info, "ForceId") == force)
            {
                foreach (string f in new[] { "CivName", "ForceName", "LeaderName", "EmpireDesc", "LeaderDesc" })
                    text.AppendLine(f + ": " + LocalizedOrUnknown(info, f));
                var atomSet = N.Get<object>(info, "TechAtomList");
                var techAssets = N.Get<object>(table, "TechDataAssets");
                foreach (var atom in N.Items(N.Get<object>(techAssets, "TechAtomList")))
                    if (atomSet != null && N.WithUInt(atomSet, "Contains", N.Get<uint>(atom, "TechAtom")))
                        text.AppendLine("Civilization ability: " + LocalizedOrUnknown(atom, "TechAtomName") + " — " + LocalizedOrUnknown(atom, "Desc"));
            }
        foreach (var info in N.Items(N.Get<object>(assets, "EmpireDefinitionList")))
            if (N.Get<uint>(info, "LegacyCivId") == civ && N.Get<uint>(info, "LegacyForceId") == force)
            {
                text.AppendLine("Empire: " + LocalizedOrUnknown(info, "EmpireName") + "\n" + LocalizedOrUnknown(info, "EmpireDesc"));
                var ids = new HashSet<string>(N.Items(N.Get<object>(info, "TechAtomIds")).Select(id => N.TextCall(id, "ToString")));
                var techAssets = N.Get<object>(table, "TechDataAssets");
                foreach (var atom in N.Items(N.Get<object>(techAssets, "TechAtomList")))
                    if (ids.Contains(N.TextCall(N.BoxField(atom, "TechAtomId"), "ToString")))
                        text.AppendLine("Civilization ability: " + LocalizedOrUnknown(atom, "TechAtomName") + " — " + LocalizedOrUnknown(atom, "Desc"));
            }
        return text.ToString();
    }
}
