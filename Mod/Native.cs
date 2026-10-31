using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using IL2CppSharp;
using Il2CppInterop.Runtime.Injection;
using R = IL2CppSharp.Il2CppReflection;
namespace TohotopiaMod;

internal static unsafe class Native
{
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr il2cpp_object_get_class(IntPtr o);
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr il2cpp_class_get_parent(IntPtr c);
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr il2cpp_class_get_methods(IntPtr c, ref IntPtr iterator);
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)] static extern void il2cpp_field_set_value(IntPtr o, IntPtr f, IntPtr v);
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)] internal static extern uint il2cpp_gchandle_new(IntPtr o, bool pinned);
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)] internal static extern void il2cpp_gchandle_free(uint handle);
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr il2cpp_class_get_type(IntPtr c);
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr il2cpp_class_get_property_from_name(IntPtr c, string name);
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr il2cpp_property_get_get_method(IntPtr p);
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr il2cpp_type_get_object(IntPtr t);
    internal static IntPtr Class(IntPtr o) => o != IntPtr.Zero ? il2cpp_object_get_class(o) : throw new InvalidOperationException("Null native object");
    internal static int RefreshInterpreterCalls(string type)
    {
        int refreshed = 0;
        var klass = R.FindClass(type);
        if (klass == IntPtr.Zero)
            return refreshed;
        IntPtr iterator = IntPtr.Zero, method;
        while ((method = il2cpp_class_get_methods(klass, ref iterator)) != IntPtr.Zero)
        {
            var info = HybridCLRCompat.WrapMethodInfo(method);
            if (info != null && info.IsInterpterImpl && info.InterpData != IntPtr.Zero)
            {
                // Do not free old code: active interpreter frames may still use it.
                // The next invocation transforms IL against the new method flags.
                info.InterpData = IntPtr.Zero;
                refreshed++;
            }
        }
        return refreshed;
    }
    static IntPtr Field(IntPtr o, string name)
    {
        for (var c = Class(o); c != IntPtr.Zero; c = il2cpp_class_get_parent(c))
        {
            var f = R.FindField(c, name);
            if (f != IntPtr.Zero)
                return f;
        }
        throw new InvalidOperationException("Missing field " + name);
    }
    internal static T Get<T>(IntPtr o, string name) where T : unmanaged
    {
        if (o == IntPtr.Zero)
            return default;
        T result = default;
        R.GetFieldValueRaw(o, Field(o, name), &result);
        return result;
    }
    internal static void SetInt(IntPtr o, string name, int v) => il2cpp_field_set_value(o, Field(o, name), (IntPtr)(&v));
    internal static void Set<T>(IntPtr o, string name, T v) where T : unmanaged => il2cpp_field_set_value(o, Field(o, name), (IntPtr)(&v));
    // IL2CPP reference fields take the object pointer itself, while value fields
    // above take a pointer to value storage. Passing &v stores a dangling stack
    // address in Lobby, _lobby and string fields.
    internal static void Set(IntPtr o, string name, IntPtr v) => il2cpp_field_set_value(o, Field(o, name), v);
    internal static void SetText(IntPtr o, string name, string value) => Set(o, name, R.CreateIl2CppString(value));
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr il2cpp_object_new(IntPtr c);
    internal static IntPtr New(string name)
    {
        var c = R.FindClass(name);
        if (c == IntPtr.Zero)
            throw new TypeLoadException(name);
        var o = il2cpp_object_new(c);
        Call(o, ".ctor");
        return o;
    }
    internal static void UIntCall(IntPtr o, string name, uint a)
    {
        Call(o, name, (IntPtr)(&a));
    }
    internal static void TwoInts(IntPtr o, string name, int a, int b)
    {
        Call(o, name, (IntPtr)(&a), (IntPtr)(&b));
    }
    internal static void SetBool(IntPtr o, string name, bool v) => R.SetFieldValueBool(o, Field(o, name), v);
    internal static IntPtr Static(string type, string field) => R.GetStaticFieldValue(R.FindClass(type), field);
    internal static IntPtr Method(IntPtr o, string name, int count)
    {
        for (var c = Class(o); c != IntPtr.Zero; c = il2cpp_class_get_parent(c))
        {
            if (name.StartsWith("get_"))
            {
                var p = il2cpp_class_get_property_from_name(c, name.Substring(4));
                if (p != IntPtr.Zero)
                {
                    var getter = il2cpp_property_get_get_method(p);
                    if (getter != IntPtr.Zero)
                        return getter;
                }
            }
            var m = R.FindMethod(c, name, count);
            if (m != IntPtr.Zero)
                return m;
        }
        throw new MissingMethodException(name);
    }
    internal static IntPtr Call(IntPtr o, string method, params IntPtr[] args) => R.InvokeMethod(Method(o, method, args.Length), o, args);
    internal static T Value<T>(IntPtr o, string method, params IntPtr[] args) where T : unmanaged => R.InvokeGet<T>(Method(o, method, args.Length), o, args);
    internal static bool WithUInt(IntPtr o, string method, uint v) => Value<bool>(o, method, (IntPtr)(&v));
    internal static string TextField(IntPtr o, string field)
    {
        var p = Get<IntPtr>(o, field);
        return p == IntPtr.Zero ? "" : Il2CppAPI.Il2CppStringToManaged(p);
    }
    internal static string LocalizedField(IntPtr o, string field)
    {
        var text = TextField(o, field);
        if (!uint.TryParse(text, out uint id))
            return text;
        var manager = Static("Logic.Multilingual.MultilingualManager", "Instance");
        if (manager == IntPtr.Zero) return "";
        var result = Call(manager, "hoy", (IntPtr)(&id));
        var localized = result == IntPtr.Zero ? "" : Il2CppAPI.Il2CppStringToManaged(result);
        return uint.TryParse(localized, out _) ? "" : localized;
    }
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr il2cpp_field_get_value_object(IntPtr field, IntPtr obj);
    internal static IntPtr BoxField(IntPtr o, string name) => il2cpp_field_get_value_object(Field(o, name), o);
    // A definition ID is five uints, not an object pointer. Avoid invoking a
    // value-type ToString through runtime_invoke with a boxed this pointer.
    internal struct ModIdentity { public uint Part0, Part1, Part2, Part3; }
    internal struct DefinitionIdentity { public ModIdentity ModId; public uint LocalId; }
    internal static string DefinitionId(IntPtr o, string field)
    {
        var id = Get<DefinitionIdentity>(o, field);
        return FormatIdentity(id.ModId, id.LocalId);
    }
    internal static string FormatIdentity(ModIdentity mod, uint local) =>
        Guid.ParseExact($"{mod.Part0:x8}{mod.Part1:x8}{mod.Part2:x8}{mod.Part3:x8}", "N").ToString("D") + "/" + local.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
    internal static string BoxedDefinitionId(IntPtr o) => FormatIdentity(Get<ModIdentity>(o, "ModId"), Get<uint>(o, "LocalId"));
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr il2cpp_field_get_type(IntPtr field);
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr il2cpp_class_from_type(IntPtr type);
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr il2cpp_class_get_name(IntPtr type);
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr il2cpp_class_get_namespace(IntPtr type);
    static readonly Dictionary<IntPtr, Dictionary<string, int>> EnumFields = new();
    internal static string EnumField(IntPtr o, string field)
    {
        var f = Field(o, field);
        if (!EnumFields.TryGetValue(f, out var names))
        {
            var type = il2cpp_class_from_type(il2cpp_field_get_type(f));
            string ns = Marshal.PtrToStringAnsi(il2cpp_class_get_namespace(type)) ?? "";
            string name = Marshal.PtrToStringAnsi(il2cpp_class_get_name(type)) ?? "";
            EnumFields[f] = names = EnumValues(ns.Length == 0 ? name : ns + "." + name);
        }
        int value = Get<int>(o, field);
        foreach (var name in names) if (name.Value == value) return name.Key;
        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr il2cpp_class_get_fields(IntPtr c, ref IntPtr iterator);
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr il2cpp_field_get_name(IntPtr f);
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)] static extern uint il2cpp_field_get_flags(IntPtr f);
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)] static extern void il2cpp_field_static_get_value(IntPtr f, IntPtr v);
    internal static Dictionary<string, int> EnumValues(string name)
    {
        var result = new Dictionary<string, int>();
        var c = R.FindClass(name);
        if (c == IntPtr.Zero)
            return result;
        IntPtr iterator = IntPtr.Zero, f;
        while ((f = il2cpp_class_get_fields(c, ref iterator)) != IntPtr.Zero)
        {
            if ((il2cpp_field_get_flags(f) & 0x40) == 0)
                continue;
            int value = 0;
            il2cpp_field_static_get_value(f, (IntPtr)(&value));
            result[Marshal.PtrToStringAnsi(il2cpp_field_get_name(f))] = value;
        }
        return result;
    }
    internal static int DictionaryInt(IntPtr dictionary, int key)
    {
        return Value<int>(dictionary, "get_Item", (IntPtr)(&key));
    }
    internal static IntPtr DictionaryObject(IntPtr dictionary, uint key)
    {
        IntPtr value = IntPtr.Zero;
        return Value<bool>(dictionary, "TryGetValue", (IntPtr)(&key), (IntPtr)(&value)) ? value : IntPtr.Zero;
    }
    internal static string TextCall(IntPtr o, string method) => R.InvokeGetString(Method(o, method, 0), o);
    internal static string TextCallWithUInt(IntPtr o, string method, uint value)
    {
        var result = Call(o, method, (IntPtr)(&value));
        return result == IntPtr.Zero ? "" : Il2CppAPI.Il2CppStringToManaged(result);
    }
    internal static List<IntPtr> Items(IntPtr list)
    {
        var result = new List<IntPtr>();
        if (list == IntPtr.Zero)
            return result;
        int n = Value<int>(list, "get_Count");
        for (int i = 0; i < n; i++)
        {
            int index = i;
            result.Add(Call(list, "get_Item", (IntPtr)(&index)));
        }
        return result;
    }
    internal static uint Id(IntPtr o) => Get<uint>(o, "Id");
    internal static IntPtr FindObject(string type)
    {
        var c = R.FindClass(type);
        if (c == IntPtr.Zero)
            return IntPtr.Zero;
        var t = il2cpp_type_get_object(il2cpp_class_get_type(c));
        var m = R.FindMethod(R.FindClass("UnityEngine.Object"), "FindObjectOfType", 1);
        return R.InvokeStaticMethod(m, new[] { t });
    }
}
