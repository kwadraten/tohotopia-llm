using System.Reflection;
using TohotopiaMod;
using TohotopiaShared;
using Json = TohotopiaMod.Json;

static class Program
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
    static void Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--launch-link")
        {
            Console.WriteLine(SettingsLaunchLink.Write(args[1], args[2], int.Parse(args[3])));
            return;
        }
        PackageChecks();
        var references = typeof(ModEntry).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        Check(!references.Any(n => n.Contains("BepInEx") || n.Contains("Il2Cpp") || n.Contains("IL2Cpp") || n.Contains("UnityEngine") || n == "System.Text.Json"), "mod references no BepInEx, interop or CoreCLR JSON assemblies");
        var item = new Fixture();
        Check(Native.Get<uint>(item, "Id") == 42 && Native.Get<int>(item, "privateValue") == 7, "inherited private fields and typed values");
        Native.Set(item, "Mode", 1);
        Check(item.Mode == Mode.Team && Native.EnumField(item, "Mode") == "Team", "enum assignment preserves game field type");
        Native.SetBool(item, "Flag", true);
        Check(Native.Get<byte>(item, "Flag") == 1, "boolean fields support legacy numeric rule reads");
        Check(Native.Value<int>(item, "get_Count") == 3, "property getter resolves through metadata");
        Check(Native.Value<string>(item, "Dispatch", 1) == "Team", "enum method arguments converted");
        Check(Native.Value<int>(new ExplicitKernel(), "Run", 4) == 5, "explicit interface dispatch uses the game contract");
        var dictionary = new Dictionary<uint, Fixture> { [42] = item };
        Check(ReferenceEquals(Native.DictionaryObject(dictionary, 42), item) && Native.DictionaryObject(dictionary, 4) == null, "TryGetValue propagates ref/out object values");
        Check(Native.TryDictionaryInt(new Dictionary<Mode, int> { [Mode.Team] = 8 }, 1, out int number) && number == 8, "enum dictionary key and ref/out numeric values");
        Check(Native.Items(new[] { item }).Count == 1, "managed enumerable object traversal");
        try { Native.Call(item, "Fail"); throw new Exception("Expected failure"); } catch (InvalidOperationException error) { Check(error.Message == "fixture", "game exceptions retain original type"); }

        var options = new Json.JsonSerializerOptions { PropertyNamingPolicy = Json.JsonNamingPolicy.CamelCase };
        var observation = Json.JsonSerializer.SerializeToElement(new { SelfId = 42, Stamp = "2026-11-02T00:00:00Z", Counts = new Dictionary<string, int> { ["UnitMove"] = 3 } }, options);
        var request = new CommunicationRequest("r1", "s1", 1, 42, 2, 1, observation, new[] { "public", "team" }, "规则");
        string wire = Json.JsonSerializer.Serialize(request, options);
        using (var parsed = System.Text.Json.JsonDocument.Parse(wire))
        {
            var root = parsed.RootElement;
            Check(root.GetProperty("requestId").GetString() == "r1" && root.GetProperty("observation").GetProperty("selfId").GetInt32() == 42, "host protocol uses camelCase and embeds observations as JSON");
            Check(root.GetProperty("observation").GetProperty("counts").GetProperty("UnitMove").GetInt32() == 3, "dictionary game identifiers keep case");
            Check(root.GetProperty("observation").GetProperty("stamp").GetString() == "2026-11-02T00:00:00Z", "observation strings are not converted to dates");
        }
        using (var document = Json.JsonDocument.Parse("{\"actions\":[{\"candidateId\":\"a0\",\"message\":\"你好\"}]}"))
        {
            var steps = document.RootElement.GetProperty("actions");
            Check(steps.GetArrayLength() == 1 && steps.Deserialize<List<PlannedStep>>(options)[0].CandidateId == "a0", "model action sequences deserialize into records");
        }
        var config = new RoomConfig { AIs = new() { new AISeat { Provider = "openai", Model = "test", TeamId = 2 } } };
        var roundTrip = Json.JsonSerializer.Deserialize<RoomConfig>(Json.JsonSerializer.Serialize(config, options), options);
        Check(roundTrip.AIs.Count == 1 && roundTrip.AIs[0].TeamId == 2 && roundTrip.Revision == config.Revision, "room configuration record round trip");
        var dir = Path.Combine(Path.GetTempPath(), "llm-loader-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            RoomFiles.Write(dir, "room.json", config);
            Check(RoomFiles.Read<RoomConfig>(dir, "room.json").AIs[0].Model == "test", "shared room files use loader JSON adapter");
            AgentEvents.Write(dir, new(DateTimeOffset.UtcNow, "game", "check", "test"));
            using var log = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(Path.Combine(dir, "logs"))[0]));
            Check(log.RootElement.GetProperty("stage").GetString() == "check", "game diagnostic logs remain host compatible");
        }
        finally { Directory.Delete(dir, true); }

        const string key = "Sender.Send";
        int previousCalls = 0, deliveries = 0;
        TH1ModFramework.ModLoader.SetMethodOverride(key, (values, next) => { previousCalls++; return next(values); });
        var sender = new Sender();
        using (var hook = LoaderHooks.ObserveResult(key, values => Native.Call(values[0], "Send", values[1]), (_, result) => { if (result is true) deliveries++; }))
        {
            Check(sender.Send(true) && !sender.Send(false), "around hook returns original success/failure");
            Check(sender.Calls == 2 && deliveries == 1 && previousCalls == 4, "around hook invokes original once; an earlier mod also sees the re-entrant original call");
        }
        previousCalls = 0; sender.Send(true);
        Check(previousCalls == 1 && sender.Calls == 3 && deliveries == 1, "disposed hook leaves the earlier mod handler and the game method working");
        var trace = new List<string>();
        using (var a = LoaderHooks.Before(key, _ => trace.Add("A")))
        using (var b = LoaderHooks.Before(key, _ => trace.Add("B")))
        { sender.Send(true); Check(string.Join("", trace) == "BA" && sender.Calls == 4, "multiple mod callbacks chain without replacing game execution"); }
        using (var a = LoaderHooks.Before(key, _ => trace.Add("A")))
        {
            var b = LoaderHooks.Before(key, _ => trace.Add("B"));
            a.Dispose(); trace.Clear(); sender.Send(true);
            Check(string.Join("", trace) == "B", "out of order disposal leaves later mod handler working"); b.Dispose();
        }
        Check(TH1ModFramework.ModLoader.HandlerCount(key) == 2, "re-registering a key reuses one loader handler instead of growing the chain");
        const string shadowed = "Sender.Shadowed";
        TH1ModFramework.ModLoader.SetMethodOverride(shadowed, _ => TH1ModFramework.ModLoader.PassThrough);
        bool reached = false;
        using (LoaderHooks.Before(shadowed, _ => reached = true))
        {
            object ignored = null;
            Check(!TH1ModFramework.ModLoader.TryResolveMethod(shadowed, Array.Empty<object>(), ref ignored) && !reached, "an earlier single-argument override that passes through runs the game method but skips later mods");
        }
        TH1ModFramework.ModLoader.SetMethodOverride("VoidMethod", _ => null);
        using (var hook = LoaderHooks.Before("VoidMethod", _ => { }))
        {
            object voidResult = "unset";
            Check(TH1ModFramework.ModLoader.TryResolveMethod("VoidMethod", Array.Empty<object>(), ref voidResult) && voidResult == null, "prior null override still suppresses a void game method");
        }
        Check(typeof(ModEntry).GetMethod("Initialize", BindingFlags.Static | BindingFlags.Public)?.GetParameters().Length == 0, "entry matches TH1 loader contract");
        ActionDelayChecks();
        TakeoverChecks.Run(Check);
        Check(NativeAiBridge.EnabledMessage(7, new MapFixture { MapID = 1500391662 }, 2) == "Native Steam lobby AI control enabled: lobby=7 map=1500391662 seats=2", "enabling model control identifies the match by MapData.MapID, which has no Id field");
        if (args.Length > 0) CheckLoaderMetadata(args[0]);
        int packageOption = Array.IndexOf(args, "--package");
        if (packageOption >= 0) CheckBuiltPackage(Path.GetFullPath(args[packageOption + 1]));
    }
    static void ActionDelayChecks()
    {
        Check(!ActionDelay.Required(typeof(AiLogic082)) && ActionDelay.Required(typeof(AiLogic085)), "only 0.8.5d AI logic, which skips its post-action delay while paused, needs the mod to advance it");
        var ai = new AiLogic085 { AILogicState = 3, _targetTime = 0.5f };
        ActionDelay.Advance(ai, presentationBusy: true, 1f);
        Check(ai.AILogicState == 3 && ai._targetTime == 0.5f, "post-action delay waits while the game presentation is busy");
        ActionDelay.Advance(ai, presentationBusy: false, 0.2f);
        Check(ai.AILogicState == 3 && Math.Abs(ai._targetTime - 0.3f) < 1e-6, "post-action delay counts down by frame time");
        ActionDelay.Advance(ai, presentationBusy: false, 0.3f);
        Check(ai.AILogicState == 1, "model turn resumes playing once the post-action delay ends");
        ai.AILogicState = 4; ai._targetTime = 0;
        ActionDelay.Advance(ai, presentationBusy: false, 1f);
        Check(ai.AILogicState == 4, "finished AI turns are never reopened");
        ActionDelay.Advance(null, presentationBusy: false, 1f);
    }
    class MapFixture { public uint MapID; }
    class AiLogic082 { public int AILogicState; public float _targetTime; }
    class AiLogic085 { public int AILogicState; public float _targetTime; object _stallWatchdog = new(); }
    static void CheckBuiltPackage(string root)
    {
        foreach (string file in new[] { "mod.dll", "mod_loader.txt", "mod_info.json", "package.build", "preview.png", "settings.example.json", "README.txt", "Remove-BepInEx.ps1", "Remove-BepInEx.bat", "AgentHost/AgentHost.exe", "Settings/TH1LLMBattlePanel.exe" })
            Check(File.Exists(Path.Combine(root, file)), "published package includes " + file);
        foreach (string component in new[] { "AgentHost", "Settings" })
            Check(!Directory.EnumerateFiles(Path.Combine(root, component), "*", SearchOption.AllDirectories)
                .Any(f => Path.GetFileName(f) is "coreclr.dll" or "hostfxr.dll" or "hostpolicy.dll" || f.EndsWith(".runtimeconfig.json") || f.EndsWith(".pdb") || f.EndsWith(component + ".dll")),
                component + " is a self-contained NativeAOT executable without runtime host files or symbols");
        var context = new System.Runtime.Loader.AssemblyLoadContext("package-check", isCollectible: true);
        try
        {
            var assembly = context.LoadFromAssemblyPath(Path.Combine(root, "mod.dll"));
            string stamp = (string)assembly.GetType("TohotopiaMod.PackageIdentity").GetField("Build", BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue();
            Check(File.ReadAllText(Path.Combine(root, "package.build")).Trim() == stamp, "published entry and package stamp match");
        }
        finally { context.Unload(); }
        Check(!Directory.Exists(Path.Combine(root, "runtime")) && !File.Exists(Path.Combine(root, "install-path.txt")) && !File.Exists(Path.Combine(root, "game-root.txt")), "published package contains no user data or machine path registration");
    }
    static void PackageChecks()
    {
        string root = Path.Combine(Path.GetTempPath(), "llm-workshop-check-" + Guid.NewGuid().ToString("N"));
        string old = Path.Combine(root, "Mods", "LLMBattle"), workshop = Path.Combine(root, "workshop", "1234567890");
        string data = Path.Combine(root, "user-data"), legacy = Path.Combine(root, "legacy", "runtime");
        Directory.CreateDirectory(old); Directory.CreateDirectory(workshop); Directory.CreateDirectory(legacy);
        try
        {
            File.WriteAllText(Path.Combine(old, "package.build"), "old-build");
            File.WriteAllText(Path.Combine(workshop, "package.build"), PackageIdentity.Build);
            File.WriteAllText(Path.Combine(workshop, "mod_loader.txt"), "TH1_CODE_MOD_V1");
            foreach (string file in new[] { "mod.dll", "settings.example.json", "AgentHost/AgentHost.exe", "Settings/TH1LLMBattlePanel.exe" })
            { string path = Path.Combine(workshop, file); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, "{}"); }
            Check(PackageLayout.Find(new[] { old, workshop }, PackageIdentity.Build) == workshop, "Workshop package is located by the executing build, without path registration");
            Check(!File.Exists(Path.Combine(workshop, "install-path.txt")), "subscription package requires no installer registration");
            try { PackageLayout.Find(new[] { old }, PackageIdentity.Build); throw new Exception("Disabled package accepted"); }
            catch (DirectoryNotFoundException) { Check(true, "disabled or mismatched Workshop copy is not selected"); }
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"maxDecisionsPerTurn\":19}");
            Directory.CreateDirectory(Path.Combine(legacy, "accounts"));
            File.WriteAllText(Path.Combine(legacy, "accounts", "token.bin"), "encrypted-fixture");
            RuntimeStorage.Prepare(workshop, data, new[] { legacy });
            Check(File.ReadAllText(Path.Combine(data, "settings.json")).Contains("19") && File.Exists(Path.Combine(data, "accounts", "token.bin")), "first startup migrates legacy settings and account files outside Workshop content");
            File.WriteAllText(Path.Combine(data, "settings.json"), "{\"maxDecisionsPerTurn\":23}");
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");
            string moved = Path.Combine(root, "another-library", "1234567890");
            Directory.CreateDirectory(Path.GetDirectoryName(moved)); Directory.Move(workshop, moved);
            RuntimeStorage.Prepare(moved, data, new[] { legacy });
            Check(File.ReadAllText(Path.Combine(data, "settings.json")).Contains("23"), "Workshop update or library move preserves current user configuration");
            Check(!Directory.Exists(Path.Combine(moved, "runtime")), "startup never writes private runtime files into the published package");
            string fresh = Path.Combine(root, "fresh-user"); RuntimeStorage.Prepare(moved, fresh, Array.Empty<string>());
            Check(File.ReadAllText(Path.Combine(fresh, "settings.json")) == "{}", "fresh subscription initializes bundled defaults automatically");
            File.Delete(Path.Combine(moved, "Settings", "TH1LLMBattlePanel.exe"));
            try { PackageLayout.Find(new[] { moved }, PackageIdentity.Build); throw new Exception("Incomplete package accepted"); }
            catch (FileNotFoundException) { Check(true, "partial Workshop downloads are detected before startup"); }
        }
        finally { Directory.Delete(root, true); }
    }
    static void CheckLoaderMetadata(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var methods = new Dictionary<string, HashSet<string>>();
        var allFields = new HashSet<string>(); var allProperties = new HashSet<string>(); var allMethods = new HashSet<string>();
        var staticFields = new Dictionary<string, HashSet<string>>();
        int setOverrideCount = 0;
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            string name = metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
            var names = type.GetMethods().Select(m => metadata.GetString(metadata.GetMethodDefinition(m).Name)).ToList();
            methods[name] = names.ToHashSet();
            allMethods.UnionWith(names);
            var fields = type.GetFields().Select(f => metadata.GetString(metadata.GetFieldDefinition(f).Name)).ToHashSet();
            allFields.UnionWith(fields);
            staticFields[name.TrimStart('.')] = fields;
            allProperties.UnionWith(type.GetProperties().Select(p => metadata.GetString(metadata.GetPropertyDefinition(p).Name)));
            if (name == "TH1ModFramework.ModLoader") setOverrideCount = names.Count(n => n == "SetMethodOverride");
        }
        var unresolved = GameMemberReferences(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Mod"))
            .Where(r => r.Static != null
                ? !staticFields.TryGetValue(r.Static, out var declared) || !declared.Contains(r.Name)
                : r.Field ? !allFields.Contains(r.Name)
                : !allMethods.Contains(r.Name) && !CommonMembers.Contains(r.Name) && !(r.Name.StartsWith("get_") && allProperties.Contains(r.Name[4..])))
            .Select(r => r.File + ": " + (r.Static != null ? r.Static + "." : "") + r.Name).Distinct().ToArray();
        Check(unresolved.Length == 0, "every game field and method named by the mod exists in the installed game" + (unresolved.Length > 0 ? ": " + string.Join(", ", unresolved) : ""));
        GameAccessAudit.Run(path, Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Mod"), Check);
        Check(setOverrideCount == 2, "installed loader offers the chained SetMethodOverride used since 0.8.031");
        foreach (var (type, names) in new[] {
            ("TH1ModFramework.ModLoader", new[] { "SetMethodOverride", "TryResolveMethod", "LogToFile", "CollectModFolders", "LoadOrder" }),
            ("TH1_Logic.Core.Main", new[] { "Update", "CheckInNetLobbyButNotGame", "OnApplicationQuit" }),
            ("TH1_Logic.Steam.GameNetSender", new[] { "nkk" }),
            ("Logic.AI.IAIKernel", new[] { "wct", "wcr" }) })
            Check(methods.TryGetValue(type, out var available) && names.All(available.Contains), "installed loader exports required methods: " + type);
    }
    static readonly HashSet<string> CommonMembers = new() { "Contains", "ContainsKey", "TryGetValue", "get_Item", "get_Count", "ToString", "get_Name", "get_text", "set_text", "get_gameObject", "get_activeInHierarchy" };
    record GameMemberReference(string File, string Static, string Name, bool Field);
    static IEnumerable<GameMemberReference> GameMemberReferences(string modDirectory)
    {
        var member = new System.Text.RegularExpressions.Regex(@"\b(?:N|Native)\.(Get<[^>]+>|Set|SetInt|SetBool|SetText|TextField|LocalizedField|BoxField|EnumField|DefinitionId|Call|Value<[^>]+>|WithUInt|TextCall|TextCallWithUInt|UIntCall)\(\s*(?:[^,()""]+|\([^()]*\))\s*,\s*""([^""]+)""");
        var statics = new System.Text.RegularExpressions.Regex(@"\b(?:N|Native)\.Static\(\s*""([^""]+)""\s*,\s*""([^""]+)""");
        foreach (string file in Directory.GetFiles(modDirectory, "*.cs"))
        {
            string text = File.ReadAllText(file), name = Path.GetFileName(file);
            foreach (System.Text.RegularExpressions.Match m in member.Matches(text))
            {
                string kind = m.Groups[1].Value;
                bool field = !(kind.StartsWith("Call") || kind.StartsWith("Value") || kind == "WithUInt" || kind.StartsWith("TextCall") || kind == "UIntCall");
                yield return new(name, null, m.Groups[2].Value, field);
            }
            foreach (System.Text.RegularExpressions.Match m in statics.Matches(text))
                yield return new(name, m.Groups[1].Value, m.Groups[2].Value, true);
        }
    }
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
namespace TH1ModFramework
{
    public static class ModLoader
    {
        static readonly Dictionary<string, List<Func<object[], Func<object[], object>, object>>> _methodOverrides = new();
        public static readonly object PassThrough = new();
        public static void SetMethodOverride(string key, Func<object[], Func<object[], object>, object> handler)
        {
            if (handler == null) { _methodOverrides.Remove(key); return; }
            if (!_methodOverrides.TryGetValue(key, out var list)) _methodOverrides[key] = list = new();
            list.Add(handler);
        }
        public static void SetMethodOverride(string key, Func<object[], object> handler) =>
            SetMethodOverride(key, handler == null ? null : (args, _) => handler(args));
        public static int HandlerCount(string key) => _methodOverrides.TryGetValue(key, out var list) ? list.Count : 0;
        public static bool TryResolveMethod(string key, object[] args, ref object result)
        {
            if (!_methodOverrides.TryGetValue(key, out var list) || list.Count == 0) { result = null; return false; }
            Func<object[], object> chain = _ => PassThrough;
            for (int i = list.Count - 1; i >= 0; i--) { var handler = list[i]; var inner = chain; chain = a => handler(a, inner); }
            result = chain(args);
            if (ReferenceEquals(result, PassThrough)) { result = null; return false; }
            return true;
        }
        public static void LogToFile(string text) => Console.WriteLine(text);
    }
}

