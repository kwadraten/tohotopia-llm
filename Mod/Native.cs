using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace TohotopiaMod;

// All objects stay in the game's managed runtime. No native pointers or interop wrappers.
internal static class Native
{
    internal static Type Class(object o) => o?.GetType() ?? throw new InvalidOperationException("Null game object");
    static readonly Dictionary<(Type, string), FieldInfo> Fields = new();
    static FieldInfo Field(Type type, string name)
    {
        if (Fields.TryGetValue((type, name), out var cached)) return cached;
        for (var t = type; t != null; t = t.BaseType)
        {
            var field = t.GetField(name, GameReflection.Flags | BindingFlags.DeclaredOnly);
            if (field != null) { Fields[(type, name)] = field; return field; }
        }
        throw new MissingFieldException(type.FullName, name);
    }
    internal static T ConvertValue<T>(object value)
    {
        if (value == null) return default;
        if (value is T typed) return typed;
        return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
    internal static T Get<T>(object o, string name) => o == null ? default : ConvertValue<T>(Field(o.GetType(), name).GetValue(o));
    internal static void Set(object o, string name, object value)
    {
        var field = Field(Class(o), name);
        field.SetValue(o, GameReflection.Argument(value, field.FieldType));
    }
    internal static void SetInt(object o, string name, int v) => Set(o, name, v);
    internal static void SetBool(object o, string name, bool v) => Set(o, name, v);
    internal static void SetText(object o, string name, string v) => Set(o, name, v);
    internal static object Static(string type, string field) => Field(GameReflection.RequireClass(type), field).GetValue(null);
    internal static object New(string type) => Activator.CreateInstance(GameReflection.RequireClass(type), true);
    internal static MethodInfo Method(object o, string name, int count) =>
        GameReflection.FindMethod(Class(o), name, count) ?? throw new MissingMethodException(Class(o).FullName, name);
    internal static object Call(object o, string method, params object[] args) => GameReflection.InvokeMethod(Method(o, method, args.Length), o, args);
    internal static T Value<T>(object o, string method, params object[] args) => ConvertValue<T>(Call(o, method, args));
    internal static void UIntCall(object o, string method, uint v) => Call(o, method, v);
    internal static void TwoInts(object o, string method, int a, int b) => Call(o, method, a, b);
    internal static bool WithUInt(object o, string method, uint v) => Value<bool>(o, method, v);
    internal static string TextField(object o, string field) => Get<string>(o, field) ?? "";
    internal static string LocalizedField(object o, string field)
    {
        string text = TextField(o, field);
        if (!uint.TryParse(text, out uint id)) return text;
        var manager = Static("Logic.Multilingual.MultilingualManager", "Instance");
        var localized = manager == null ? "" : (string)Call(manager, "hoy", id);
        return uint.TryParse(localized, out _) ? "" : localized ?? "";
    }
    internal static object BoxField(object o, string field) => Get<object>(o, field);
    internal static string DefinitionId(object o, string field) => BoxedDefinitionId(BoxField(o, field));
    internal static string BoxedDefinitionId(object o) => o?.ToString() ?? "";
    internal static string EnumField(object o, string field) => Get<object>(o, field)?.ToString() ?? "";
    internal static Dictionary<string, int> EnumValues(string name)
    {
        var result = new Dictionary<string, int>();
        var type = GameReflection.FindClass(name);
        if (type == null) return result;
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            if (field.IsLiteral) result[field.Name] = Convert.ToInt32(field.GetRawConstantValue());
        return result;
    }
    internal static int DictionaryInt(object dictionary, int key) => Value<int>(dictionary, "get_Item", key);
    internal static bool TryDictionaryInt(object dictionary, int key, out int value)
    {
        object[] args = { key, 0 };
        bool found = Value<bool>(dictionary, "TryGetValue", args);
        value = ConvertValue<int>(args[1]);
        return found;
    }
    internal static object DictionaryObject(object dictionary, uint key)
    {
        if (dictionary == null) return null;
        object[] args = { key, null };
        return Value<bool>(dictionary, "TryGetValue", args) ? args[1] : null;
    }
    internal static string TextCall(object o, string method) => (string)Call(o, method) ?? "";
    internal static string TextCallWithUInt(object o, string method, uint v) => (string)Call(o, method, v) ?? "";
    internal static List<object> Items(object list)
    {
        var result = new List<object>();
        if (list is IEnumerable items) foreach (var item in items) result.Add(item);
        return result;
    }
    internal static uint Id(object o) => Get<uint>(o, "Id");
    internal static object FindObject(string type)
    {
        var unityObject = GameReflection.RequireClass("UnityEngine.Object");
        var method = unityObject.GetMethod("FindObjectOfType", new[] { typeof(Type) });
        return method.Invoke(null, new object[] { GameReflection.RequireClass(type) });
    }
}

internal static class GameReflection
{
    internal const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static readonly Dictionary<string, Type> Types = new();
    internal static Type RequireClass(string name) => FindClass(name) ?? throw new TypeLoadException(name);
    internal static Type FindClass(string name)
    {
        if (Types.TryGetValue(name, out var cached)) return cached;
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var assembly in assemblies)
        {
            var type = assembly.GetType(name, false);
            if (type != null) { Types[name] = type; return type; }
        }
        if (name.IndexOf('.') < 0)
            foreach (var assembly in assemblies.Where(a => a.GetName().Name == "TH1.Hotfix"))
                foreach (var type in assembly.GetTypes())
                    if (type.Name == name) { Types[name] = type; return type; }
        return null;
    }
    static readonly Dictionary<(Type, string, int), MethodInfo> Methods = new();
    internal static MethodInfo FindMethod(Type type, string name, int count)
    {
        if (Methods.TryGetValue((type, name, count), out var cached)) return cached;
        var method = ResolveMethod(type, name, count);
        if (method != null) Methods[(type, name, count)] = method;
        return method;
    }
    static MethodInfo ResolveMethod(Type type, string name, int count)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            // Getter names in metadata can be obfuscated independently of the property.
            if (name.StartsWith("get_", StringComparison.Ordinal))
            {
                var getter = t.GetProperty(name.Substring(4), Flags | BindingFlags.DeclaredOnly)?.GetGetMethod(true);
                if (getter != null && getter.GetParameters().Length == count) return getter;
            }
            var matches = t.GetMethods(Flags | BindingFlags.DeclaredOnly).Where(m => m.Name == name && m.GetParameters().Length == count).ToArray();
            if (matches.Length > 1) throw new AmbiguousMatchException(t.FullName + "." + name);
            if (matches.Length == 1) return matches[0];
        }
        foreach (var contract in type.GetInterfaces())
        {
            var matches = contract.GetMethods().Where(m => m.Name == name && m.GetParameters().Length == count).ToArray();
            if (matches.Length == 1) return matches[0];
        }
        return null;
    }
    internal static MethodInfo FindMethod(string type, string name, int count) => FindMethod(RequireClass(type), name, count);
    internal static object Argument(object value, Type type)
    {
        if (type.IsByRef) type = type.GetElementType();
        if (value == null || type.IsInstanceOfType(value)) return value;
        return type.IsEnum ? Enum.ToObject(type, value) : Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture);
    }
    internal static object InvokeMethod(MethodInfo method, object target, object[] args)
    {
        var parameters = method.GetParameters();
        for (int i = 0; i < args.Length; i++) args[i] = Argument(args[i], parameters[i].ParameterType);
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    internal static object InvokeStaticMethod(MethodInfo method, object[] args) => InvokeMethod(method, null, args);
}
