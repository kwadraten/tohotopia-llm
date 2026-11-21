using TohotopiaMod;
using TohotopiaShared;
using AI = Logic.AI.AILogic;
using Main = TH1_Logic.Core.Main;
using Editor = Logic.MainEditor;
using Presentation = TH1_Core.Managers.PresentationManager;

static class TakeoverChecks
{
    internal static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "llm-takeover-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var p1 = new TestPlayer { Id = 1, Turn = 1 };
            var p2 = new TestPlayer { Id = 2, Turn = 1 };
            var map = new TestMap { Current = p1 };
            var ai = new AI { _mapData = map, _playerData = p1, AILogicState = 1 };
            Main.MapData = map;
            Main.Instance.GameLogic._aiLogic = ai;
            var battle = new Battle();
            Native.Set(battle, "data", root);
            Native.Set(battle, "previousMap", map);
            Native.Set(battle, "enabled", true);
            Native.Set(battle, "ready", true);
            Native.Set(battle, "next", 1000f);
            var room = new RoomBridge(root, battle);
            var bridge = Native.Get<NativeAiBridge>(room, "native");
            Native.Set(bridge, "active", true);
            Native.Set(bridge, "started", true);
            Native.Set(bridge, "models", true);
            var seats = Native.Get<Dictionary<uint, int>>(bridge, "seats");
            seats[1] = 1; seats[2] = 2;
            Native.Set(battle, "room", room);
            Editor.Instance.IsEditor = false; Editor.Instance.IsGo = true;
            UnityEngine.Time.timeScale = 1;
            UnityEngine.Time.deltaTime = 0.1f;
            battle.Update();
            check(battle.CurrentTurn() && Editor.Instance.IsEditor && !Editor.Instance.IsGo, "the before-update hook locks the authoritative model turn even before the throttled poll runs");
            var plan = Native.Get<ActionPlan>(battle, "plan");
            plan.Load(new[] { new PlannedStep("end_turn", "") });
            foreach (var (description, block) in new (string, Action)[] {
                ("network action executing", () => Main.Instance.IsNetActionExecuting = true),
                ("presentation busy", () => Presentation.iwf = true),
                ("blocking presentation UI", () => Presentation.zfo = true),
                ("game time paused", () => UnityEngine.Time.timeScale = 0),
                ("multiplayer loading barrier", () => TH1_Logic.Net.MultiplayerMatchLoading.wmw = true),
                ("action synchronization failure", () => Logic.Action.ActionExecutionSafety.Failed = true),
                ("game ForceUpdating state", () => Main.Instance.GameLogic.State = 5),
                ("map start choice open", () => Main.Instance._mapStartChoice = new()),
                ("Main.Start incomplete", () => Main.Instance._isStarted = false) })
            {
                block();
                battle.AdvancePlan(ai, 1, 1);
                check(ai.AILogicState == 1 && plan.Active, "model plans wait while " + description);
                ResetGates();
            }
            Native.Set(battle, "oldEditor", true); Native.Set(battle, "oldGo", false);
            battle.AdvancePlan(ai, 1, 1);
            check(ai.AILogicState == 1 && plan.Active, "taking the model pause never bypasses an existing editor pause");
            Native.Set(battle, "oldEditor", false); Native.Set(battle, "oldGo", true);
            battle.AdvancePlan(ai, 1, 1);
            check(ai.AILogicState == 4 && !plan.Active, "the first model turn can finish through the native Finished state");
            var pending = new TaskCompletionSource<string>();
            var cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            Native.Set(battle, "pending", pending.Task);
            Native.Set(battle, "cancellation", cancellation);
            plan.Load(new[] { new PlannedStep("end_turn", "") });
            var ambient = new CancellationTokenSource();
            var ambientToken = ambient.Token;
            Native.Set(battle, "ambientPending", new TaskCompletionSource<string>().Task);
            Native.Set(battle, "ambientCancellation", ambient);
            Native.Set(battle, "ambientPlayer", 2u);
            map.Current = p2;
            battle.Update();
            check(token.IsCancellationRequested && !plan.Active && Native.Get<object>(battle, "pending") == null
                && Native.Get<object>(battle, "currentAI") == null && !battle.CurrentTurn(),
                "an authoritative player change cancels pending work before the game replaces its stale AI player");
            check(ambientToken.IsCancellationRequested && Native.Get<object>(battle, "ambientPending") == null,
                "out-of-turn communication is canceled immediately when its player becomes current, even between polls");
            check(Editor.Instance.IsEditor && !Editor.Instance.IsGo, "the next controlled seat is paused before GameLogic initializes its AI");
            ai._playerData = p2; ai.AILogicState = 1;
            battle.Update();
            plan.Load(new[] { new PlannedStep("end_turn", "") });
            battle.AdvancePlan(ai, 2, 1);
            check(ai.AILogicState == 4 && !plan.Active && battle.CurrentTurn(), "a second consecutive model seat attaches to the new native AI context and finishes its turn");
            p2.Turn = 2;
            ai.AILogicState = 3; ai._targetTime = 0.5f;
            battle.Update();
            check(Math.Abs(ai._targetTime - 0.4f) < 1e-6, "a new round on the same player object is rebound and its paused action delay advances once per frame");
            Main.Instance.IsNetActionExecuting = true;
            battle.Update();
            check(Math.Abs(ai._targetTime - 0.4f) < 1e-6 && ai.AILogicState == 3, "the model does not advance Pausing while a network action is executing");
            ResetGates();
            Native.Set(battle, "nativeDirty", true);
            Native.Set(bridge, "models", false);
            battle.StopModels();
            check(ai._kernel.Starts == 1 && ai._kernel.Stops == 1 && ai.AILogicState == 3
                && Math.Abs(ai._targetTime - 0.4f) < 1e-6 && !Editor.Instance.IsEditor && Editor.Instance.IsGo,
                "restoring native AI rebuilds its cached turn after model actions while preserving Pausing and its remaining delay");
            Native.Set(bridge, "models", true);
            battle.StartModels();
            check(battle.CurrentTurn() && Editor.Instance.IsEditor && !Editor.Instance.IsGo,
                "native to models can retake the same still-active turn without stale work or a lost pause");
            ai.AILogicState = 2;
            battle.Update();
            check(!Editor.Instance.IsEditor && Editor.Instance.IsGo
                && !ModelTakeover.Restart(ai, map, p2) && ai.AILogicState == 2,
                "native PrePlay presentations resolve without the model pause or a kernel restart");
            ai.AILogicState = 3; ai._targetTime = 0.1f;
            battle.Update();
            check(ai.AILogicState == 1 && Editor.Instance.IsEditor && !Editor.Instance.IsGo,
                "takeover resumes after native PrePlay resolves and its post-action delay expires");
            map.Current = p1;
            ai._kernel.Fail = true;
            check(!battle.RestartNativeTurn() && ai.AILogicState == 1 && ai._kernel.Starts == 1,
                "a stale model context can never restart or finish another player's native turn");
            map.Current = p2;
            ai.AILogicState = 3;
            check(!battle.RestartNativeTurn() && ai.AILogicState == 4,
                "a failed native restart from Pausing finishes only the still-authoritative AI turn");
            ai._kernel.Fail = false;
            Main.MapData = new TestMap { Current = p1 };
            battle.Update();
            check(!Editor.Instance.IsEditor && Editor.Instance.IsGo && !battle.CurrentTurn()
                && Native.Get<object>(battle, "currentAI") == null,
                "map replacement releases the old pause and context before the throttled room transition poll");
            Main.MapData = null;
            var failedBattle = new Battle();
            var failedBridge = new NativeAiBridge(root, failedBattle);
            Native.Set(failedBridge, "chatHookInstalled", true);
            Native.Set(failedBridge, "active", true);
            Native.Set(failedBridge, "started", true);
            typeof(NativeAiBridge).GetMethod("StartModels", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(failedBridge, null);
            check(!failedBridge.ModelsEnabled && !Native.Get<bool>(failedBattle, "enabled")
                && Native.Get<string>(failedBridge, "modelError").Length > 0,
                "failed model startup rolls back both control flags and publishes model-error rather than playing");
        }
        finally { ResetGates(); Main.MapData = null; Editor.Instance.IsEditor = false; Editor.Instance.IsGo = false; Directory.Delete(root, true); }
    }
    static void ResetGates()
    {
        Main.Instance.IsNetActionExecuting = false;
        Main.Instance._isStarted = true;
        Main.Instance._mapStartChoice = null;
        Main.Instance.GameLogic.State = 3;
        Presentation.iwf = Presentation.zfo = false;
        UnityEngine.Time.timeScale = 1;
        TH1_Logic.Net.MultiplayerMatchLoading.wmw = false;
        Logic.Action.ActionExecutionSafety.Failed = false;
    }
}
class TestPlayer { public uint Id, Turn; public int PlayerFactionType = 0; }
class TestMap { public uint MapID = 9; public TestPlayer Current; public TestPlayer ivn => Current; }
class TestLogic { public Logic.AI.AILogic _aiLogic; public int State = 3; public int gtw() => State; }
class TestKernel
{
    public int Starts, Stops;
    public bool Fail;
    public void wct() { Stops++; }
    public void wcr(object map, object player) { if (Fail) throw new InvalidOperationException("fixture kernel failed"); Starts++; }
}
namespace TH1_Logic.Core
{
    class Main
    {
        public static Main Instance { get; } = new();
        public static TestMap MapData;
        public TestLogic GameLogic = new();
        public bool IsNetActionExecuting;
        public bool _isStarted = true;
        public object _mapStartChoice;
    }
}
namespace Logic
{
    class MainEditor
    {
        public static MainEditor Instance = new();
        public bool IsEditor, IsGo;
    }
}
namespace Logic.AI
{
    class AILogic
    {
        public TestMap _mapData;
        public TestPlayer _playerData;
        public TestKernel _kernel = new();
        public int AILogicState;
        public float _targetTime;
        object _stallWatchdog = new();
    }
}
namespace TH1_Core.Managers { static class PresentationManager { public static bool iwf { get; set; } public static bool zfo { get; set; } } }
namespace TH1_Logic.Net { static class MultiplayerMatchLoading { public static bool wmw { get; set; } } }
namespace Logic.Action { static class ActionExecutionSafety { public static bool Failed; public static bool rpf(object map) => Failed; } }
namespace UnityEngine
{
    enum KeyCode { F8 = 289 }
    static class Input { public static bool GetKeyDown(KeyCode key) => false; }
    static class Time
    {
        public static float realtimeSinceStartup => 10;
        public static float deltaTime { get; set; } = 0.1f;
        public static float timeScale { get; set; } = 1;
    }
}
