using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TohotopiaMod;

// The loader owns discovery and startup; our package stamp identifies the matching files on disk.
public static class ModEntry
{
    static Battle battle;
    static readonly List<IDisposable> hooks = new();
    public static void Initialize()
    {
        if (battle != null) return;
        string root = GameRuntime.InstallDirectory();
        Battle.InstallDirectory = root;
        Battle.DataDirectory = RuntimeStorage.Prepare(root, TohotopiaShared.RuntimePaths.PrepareRoot(null),
            new[] {
                Path.Combine(GameRuntime.PersistentDataPath, "Mods", "LLMBattle", "runtime"),
                Path.Combine(GameRuntime.GameDirectory, "BepInEx", "plugins", "LLMBattle", "runtime"),
                Path.Combine(root, "runtime")
            });
        var instance = new Battle();
        try
        {
            hooks.Add(LoaderHooks.Before("Main.Update", _ => instance.Update()));
            hooks.Add(LoaderHooks.Before("Main.CheckInNetLobbyButNotGame", _ => instance.LateUpdate()));
            hooks.Add(LoaderHooks.Before("Main.OnApplicationQuit", _ => Shutdown()));
            battle = instance;
            Battle.Log.LogInfo("LLM Battle loaded through TH1 Mod Loader (no BepInEx)");
        }
        catch
        {
            foreach (var hook in hooks) hook.Dispose();
            hooks.Clear();
            throw;
        }
    }
    public static void Shutdown()
    {
        var instance = battle;
        battle = null;
        try { instance?.OnDestroy(); }
        finally { foreach (var hook in hooks) hook.Dispose(); hooks.Clear(); }
    }
}

namespace TohotopiaMod
{
    internal sealed class ModLog
    {
        void Write(string level, object message)
        {
            string text = "[LLMBattle/" + level + "] " + message;
            try { GameReflection.InvokeStaticMethod(GameReflection.RequireClass("TH1ModFramework.ModLoader").GetMethod("LogToFile", new[] { typeof(string) }), new object[] { text }); }
            catch { System.Diagnostics.Debug.WriteLine(text); }
        }
        public void LogInfo(object message) => Write("info", message);
        public void LogWarning(object message) => Write("warning", message);
        public void LogError(object message) => Write("error", message);
        public void LogDebug(object message) => Write("debug", message);
    }
    internal static class GameRuntime
    {
        static object StaticProperty(string type, string name) => GameReflection.InvokeStaticMethod(GameReflection.FindMethod(type, "get_" + name, 0), Array.Empty<object>());
        public static double Now => Convert.ToDouble(StaticProperty("UnityEngine.Time", "realtimeSinceStartup"));
        public static float DeltaTime => Convert.ToSingle(StaticProperty("UnityEngine.Time", "deltaTime"));
        public static float TimeScale => Convert.ToSingle(StaticProperty("UnityEngine.Time", "timeScale"));
        public static bool BatchMode => (bool)StaticProperty("UnityEngine.Application", "isBatchMode");
        public static string Version => (string)StaticProperty("UnityEngine.Application", "version");
        public static bool SettingsKeyDown()
        {
            var input = GameReflection.RequireClass("UnityEngine.Input");
            var key = GameReflection.RequireClass("UnityEngine.KeyCode");
            var method = input.GetMethod("GetKeyDown", new[] { key });
            return (bool)method.Invoke(null, new[] { Enum.ToObject(key, 289) }); // F8
        }
        internal static string PersistentDataPath => (string)StaticProperty("UnityEngine.Application", "persistentDataPath");
        internal static string GameDirectory => Path.GetFullPath(Path.Combine((string)StaticProperty("UnityEngine.Application", "dataPath"), ".."));
        internal static string InstallDirectory()
        {
            var folders = (Dictionary<string, string>)GameReflection.InvokeStaticMethod(
                GameReflection.FindMethod("TH1ModFramework.ModLoader", "CollectModFolders", 0), Array.Empty<object>());
            var order = (List<string>)GameReflection.InvokeStaticMethod(
                GameReflection.FindMethod("TH1ModFramework.ModLoader", "LoadOrder", 0), Array.Empty<object>());
            var candidates = new List<string>();
            if (order == null) candidates.AddRange(folders.Values);
            else foreach (string id in order) if (folders.TryGetValue(id, out var path)) candidates.Add(path);
            return PackageLayout.Find(candidates, PackageIdentity.Build);
        }
    }
    internal sealed class GameLabel
    {
        readonly object label;
        public GameLabel(object label) { this.label = label; }
        public string Text { get => Native.Value<string>(label, "get_text"); set => Native.Call(label, "set_text", value); }
        public bool Active => Native.Value<bool>(Native.Call(label, "get_gameObject"), "get_activeInHierarchy");
    }

    internal sealed class LoaderHooks : IDisposable
    {
        static readonly Dictionary<string, List<LoaderHooks>> Registered = new();
        readonly string key;
        readonly Func<object[], object> invoke;
        readonly Action<object[], object> after;
        readonly Action<object[]> before;
        bool disposed, invoking;
        LoaderHooks(string key, Func<object[], object> invoke, Action<object[], object> after, Action<object[]> before)
        {
            this.key = key; this.invoke = invoke; this.after = after; this.before = before;
            if (!Registered.TryGetValue(key, out var hooks))
            {
                var register = GameReflection.RequireClass("TH1ModFramework.ModLoader")
                    .GetMethod("SetMethodOverride", new[] { typeof(string), typeof(Func<object[], Func<object[], object>, object>) })
                    ?? throw new MissingMethodException("TH1ModFramework.ModLoader", "SetMethodOverride(chain); TH1 Mod Loader 0.8.031 or newer is required");
                Func<object[], Func<object[], object>, object> handler = (args, next) =>
                    Run(Registered.TryGetValue(key, out var live) ? live.ToArray() : Array.Empty<LoaderHooks>(), live?.Count - 1 ?? -1, args, next);
                hooks = new List<LoaderHooks>();
                Registered[key] = hooks;
                try { GameReflection.InvokeStaticMethod(register, new object[] { key, handler }); }
                catch { Registered.Remove(key); throw; }
            }
            hooks.Add(this);
        }
        static object Run(LoaderHooks[] hooks, int index, object[] args, Func<object[], object> next)
        {
            if (index < 0) return next(args);
            var hook = hooks[index];
            if (hook.disposed || hook.invoking) return Run(hooks, index - 1, args, next);
            try { hook.before?.Invoke(args); }
            catch (Exception error) { Battle.Log.LogError(hook.key + ": " + error); }
            if (hook.invoke == null) return Run(hooks, index - 1, args, next);
            object result;
            hook.invoking = true;
            try { result = hook.invoke(args); }
            finally { hook.invoking = false; }
            try { hook.after?.Invoke(args, result); }
            catch (Exception error) { Battle.Log.LogWarning(hook.key + ": " + error); }
            return result;
        }
        public static IDisposable Before(string key, Action<object[]> callback) => new LoaderHooks(key, null, null, callback);
        public static IDisposable ObserveResult(string key, Func<object[], object> invoke, Action<object[], object> after) => new LoaderHooks(key, invoke, after, null);
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (Registered.TryGetValue(key, out var hooks)) hooks.Remove(this);
        }
    }
}

namespace System.Runtime.CompilerServices { internal static class IsExternalInit { } }
