using System.Reflection;
using TohotopiaMod;
using TohotopiaShared;
using Xunit;
using Json = TohotopiaMod.Json;

// The TH1ModFramework.ModLoader fixture keeps its override chains in static
// state, and every test in this project mutates that shared registry or the
// shared fixture singletons, so the whole assembly runs sequentially.
[Collection("Loader")]
public class LoaderContractTests
{
    [Fact]
    public void Mod_references_no_host_runtime_assemblies()
    {
        var references = typeof(ModEntry).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        Assert.True(!references.Any(n => n.Contains("BepInEx") || n.Contains("Il2Cpp") || n.Contains("IL2Cpp") || n.Contains("UnityEngine") || n == "System.Text.Json"),
            "mod references no BepInEx, interop or CoreCLR JSON assemblies");
    }

    [Fact]
    public void Native_access_preserves_game_member_shapes()
    {
        var item = new Fixture();
        Assert.True(Native.Get<uint>(item, "Id") == 42 && Native.Get<int>(item, "privateValue") == 7, "inherited private fields and typed values");
        Native.Set(item, "Mode", 1);
        Assert.True(item.Mode == Mode.Team && Native.EnumField(item, "Mode") == "Team", "enum assignment preserves game field type");
        Native.SetBool(item, "Flag", true);
        Assert.True(Native.Get<byte>(item, "Flag") == 1, "boolean fields support legacy numeric rule reads");
        Assert.True(Native.Value<int>(item, "get_Count") == 3, "property getter resolves through metadata");
        Assert.True(Native.Value<string>(item, "Dispatch", 1) == "Team", "enum method arguments converted");
        Assert.True(Native.Value<int>(new ExplicitKernel(), "Run", 4) == 5, "explicit interface dispatch uses the game contract");
        var dictionary = new Dictionary<uint, Fixture> { [42] = item };
        Assert.True(ReferenceEquals(Native.DictionaryObject(dictionary, 42), item) && Native.DictionaryObject(dictionary, 4) == null, "TryGetValue propagates ref/out object values");
        Assert.True(Native.TryDictionaryInt(new Dictionary<Mode, int> { [Mode.Team] = 8 }, 1, out int number) && number == 8, "enum dictionary key and ref/out numeric values");
        Assert.True(Native.Items(new[] { item }).Count == 1, "managed enumerable object traversal");
        var error = Assert.Throws<InvalidOperationException>(() => Native.Call(item, "Fail"));
        Assert.True(error.Message == "fixture", "game exceptions retain original type");
    }

    [Fact]
    public void Wire_contracts_keep_camelCase_records_and_observation_shapes()
    {
        var options = new Json.JsonSerializerOptions { PropertyNamingPolicy = Json.JsonNamingPolicy.CamelCase };
        var observation = Json.JsonSerializer.SerializeToElement(new { SelfId = 42, Stamp = "2026-11-02T00:00:00Z", Counts = new Dictionary<string, int> { ["UnitMove"] = 3 } }, options);
        var request = new CommunicationRequest("r1", "s1", 1, 42, 2, 1, observation, new[] { "public", "team" }, "规则");
        string wire = Json.JsonSerializer.Serialize(request, options);
        using (var parsed = System.Text.Json.JsonDocument.Parse(wire))
        {
            var root = parsed.RootElement;
            Assert.True(root.GetProperty("requestId").GetString() == "r1" && root.GetProperty("observation").GetProperty("selfId").GetInt32() == 42, "host protocol uses camelCase and embeds observations as JSON");
            Assert.True(root.GetProperty("observation").GetProperty("counts").GetProperty("UnitMove").GetInt32() == 3, "dictionary game identifiers keep case");
            Assert.True(root.GetProperty("observation").GetProperty("stamp").GetString() == "2026-11-02T00:00:00Z", "observation strings are not converted to dates");
        }
        using (var document = Json.JsonDocument.Parse("{\"actions\":[{\"candidateId\":\"a0\",\"message\":\"你好\"}]}"))
        {
            var steps = document.RootElement.GetProperty("actions");
            Assert.True(steps.GetArrayLength() == 1 && steps.Deserialize<List<PlannedStep>>(options)[0].CandidateId == "a0", "model action sequences deserialize into records");
        }
        var config = new RoomConfig { AIs = new() { new AISeat { Provider = "openai", Model = "test", TeamId = 2 } } };
        var roundTrip = Json.JsonSerializer.Deserialize<RoomConfig>(Json.JsonSerializer.Serialize(config, options), options);
        Assert.True(roundTrip.AIs.Count == 1 && roundTrip.AIs[0].TeamId == 2 && roundTrip.Revision == config.Revision, "room configuration record round trip");
    }

    [Fact]
    public void Shared_room_files_and_diagnostic_logs_use_the_loader_adapter()
    {
        var config = new RoomConfig { AIs = new() { new AISeat { Provider = "openai", Model = "test", TeamId = 2 } } };
        var dir = Path.Combine(Path.GetTempPath(), "llm-loader-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            RoomFiles.Write(dir, "room.json", config);
            Assert.True(RoomFiles.Read<RoomConfig>(dir, "room.json").AIs[0].Model == "test", "shared room files use loader JSON adapter");
            AgentEvents.Write(dir, new(DateTimeOffset.UtcNow, "game", "check", "test"));
            using var log = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(Path.Combine(dir, "logs"))[0]));
            Assert.True(log.RootElement.GetProperty("stage").GetString() == "check", "game diagnostic logs remain host compatible");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Hook_chains_compose_across_mods()
    {
        const string key = "Sender.Send";
        int previousCalls = 0, deliveries = 0;
        TH1ModFramework.ModLoader.SetMethodOverride(key, (values, next) => { previousCalls++; return next(values); });
        var sender = new Sender();
        using (var hook = LoaderHooks.ObserveResult(key, values => Native.Call(values[0], "Send", values[1]), (_, result) => { if (result is true) deliveries++; }))
        {
            Assert.True(sender.Send(true) && !sender.Send(false), "around hook returns original success/failure");
            Assert.True(sender.Calls == 2 && deliveries == 1 && previousCalls == 4, "around hook invokes original once; an earlier mod also sees the re-entrant original call");
        }
        previousCalls = 0; sender.Send(true);
        Assert.True(previousCalls == 1 && sender.Calls == 3 && deliveries == 1, "disposed hook leaves the earlier mod handler and the game method working");
        var trace = new List<string>();
        using (var a = LoaderHooks.Before(key, _ => trace.Add("A")))
        using (var b = LoaderHooks.Before(key, _ => trace.Add("B")))
        { sender.Send(true); Assert.True(string.Join("", trace) == "BA" && sender.Calls == 4, "multiple mod callbacks chain without replacing game execution"); }
        using (var a = LoaderHooks.Before(key, _ => trace.Add("A")))
        {
            var b = LoaderHooks.Before(key, _ => trace.Add("B"));
            a.Dispose(); trace.Clear(); sender.Send(true);
            Assert.True(string.Join("", trace) == "B", "out of order disposal leaves later mod handler working"); b.Dispose();
        }
        Assert.True(TH1ModFramework.ModLoader.HandlerCount(key) == 2, "re-registering a key reuses one loader handler instead of growing the chain");
        const string shadowed = "Sender.Shadowed";
        TH1ModFramework.ModLoader.SetMethodOverride(shadowed, _ => TH1ModFramework.ModLoader.PassThrough);
        bool reached = false;
        using (LoaderHooks.Before(shadowed, _ => reached = true))
        {
            object ignored = null;
            Assert.True(!TH1ModFramework.ModLoader.TryResolveMethod(shadowed, Array.Empty<object>(), ref ignored) && !reached, "an earlier single-argument override that passes through runs the game method but skips later mods");
        }
        TH1ModFramework.ModLoader.SetMethodOverride("VoidMethod", _ => null);
        using (var hook = LoaderHooks.Before("VoidMethod", _ => { }))
        {
            object voidResult = "unset";
            Assert.True(TH1ModFramework.ModLoader.TryResolveMethod("VoidMethod", Array.Empty<object>(), ref voidResult) && voidResult == null, "prior null override still suppresses a void game method");
        }
    }

    [Fact]
    public void Entry_point_matches_the_loader_contract() =>
        Assert.True(typeof(ModEntry).GetMethod("Initialize", BindingFlags.Static | BindingFlags.Public)?.GetParameters().Length == 0, "entry matches TH1 loader contract");

    [Fact]
    public void Post_action_delay_only_advances_while_unpaused_and_idle()
    {
        Assert.True(!ActionDelay.Required(typeof(AiLogic082)) && ActionDelay.Required(typeof(AiLogic085)), "only 0.8.5d AI logic, which skips its post-action delay while paused, needs the mod to advance it");
        var ai = new AiLogic085 { AILogicState = 3, _targetTime = 0.5f };
        ActionDelay.Advance(ai, presentationBusy: true, 1f);
        Assert.True(ai.AILogicState == 3 && ai._targetTime == 0.5f, "post-action delay waits while the game presentation is busy");
        ActionDelay.Advance(ai, presentationBusy: false, 0.2f);
        Assert.True(ai.AILogicState == 3 && Math.Abs(ai._targetTime - 0.3f) < 1e-6, "post-action delay counts down by frame time");
        ActionDelay.Advance(ai, presentationBusy: false, 0.3f);
        Assert.True(ai.AILogicState == 1, "model turn resumes playing once the post-action delay ends");
        ai.AILogicState = 4; ai._targetTime = 0;
        ActionDelay.Advance(ai, presentationBusy: false, 1f);
        Assert.True(ai.AILogicState == 4, "finished AI turns are never reopened");
        ActionDelay.Advance(null, presentationBusy: false, 1f);
    }

    [Fact]
    public void Enabled_message_identifies_the_match_by_MapID() =>
        Assert.True(NativeAiBridge.EnabledMessage(7, new MapFixture { MapID = 1500391662 }, 2) == "Native Steam lobby AI control enabled: lobby=7 map=1500391662 seats=2",
            "enabling model control identifies the match by MapData.MapID, which has no Id field");

    class MapFixture { public uint MapID; }
    class AiLogic082 { public int AILogicState; public float _targetTime; }
    class AiLogic085 { public int AILogicState; public float _targetTime; object _stallWatchdog = new(); }
    interface IKernel { int Run(int value); }
    class ExplicitKernel : IKernel { int IKernel.Run(int value) => value + 1; }
    enum Mode { Public, Team }
    class Parent { int privateValue = 7; }
    class Fixture : Parent
    {
        public uint Id = 42; public Mode Mode; public bool Flag; public int Count => 3;
        public string Dispatch(Mode mode) => mode.ToString();
        public void Fail() => throw new InvalidOperationException("fixture");
    }
    class Sender
    {
        public int Calls;
        public bool Send(bool success)
        {
            object result = null;
            if (TH1ModFramework.ModLoader.TryResolveMethod("Sender.Send", new object[] { this, success }, ref result)) return (bool)result;
            Calls++; return success;
        }
    }
}
