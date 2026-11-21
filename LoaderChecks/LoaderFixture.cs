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
