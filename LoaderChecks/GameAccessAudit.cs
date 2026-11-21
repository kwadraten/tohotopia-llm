using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static class GameAccessAudit
{
    internal record Access(string Site, string Type, string Kind, string Member, int Arguments, string Value);
    internal record Site(string Key, string File, int Line, string Method, string Expression);
    internal static Site[] Sites(string root) => Directory.GetFiles(root, "*.cs").SelectMany(file =>
        CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot().DescendantNodes()
        .Where(i => i is InvocationExpressionSyntax call && IsAccess(call) || i is MemberAccessExpressionSyntax member
            && member.Expression is IdentifierNameSyntax owner && new[] { "N", "Native", "R", "GameReflection" }.Contains(owner.Identifier.Text)
            && member.Parent is not InvocationExpressionSyntax && member.Name.Identifier.Text != "Flags")
        .Select(i => new Site(Path.GetFileName(file) + "|" + Scope(i) + "|" + i.WithoutTrivia().NormalizeWhitespace().ToFullString(),
            Path.GetFileName(file), i.GetLocation().GetLineSpan().StartLinePosition.Line + 1, Scope(i), i.ToString())))
        .GroupBy(s => s.Key).SelectMany(g => g.Select((s, i) => s with { Key = s.Key + "#" + (i + 1) })).ToArray();
    static string Scope(SyntaxNode i) => i.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault(m => m is MethodDeclarationSyntax or PropertyDeclarationSyntax or ConstructorDeclarationSyntax) switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        PropertyDeclarationSyntax p => p.Identifier.Text,
        ConstructorDeclarationSyntax c => c.Identifier.Text,
        _ => "?"
    };
    static bool IsAccess(InvocationExpressionSyntax i)
    {
        if (Path.GetFileName(i.SyntaxTree.FilePath) == "Native.cs") return true;
        string name = i.Expression is MemberAccessExpressionSyntax m ? m.Name.Identifier.Text : i.Expression is IdentifierNameSyntax n ? n.Identifier.Text : "";
        if (i.Expression is MemberAccessExpressionSyntax access && access.Expression is IdentifierNameSyntax receiver
            && new[] { "N", "Native", "R", "GameReflection" }.Contains(receiver.Identifier.Text))
            return true;
        return new[] { "GetField", "GetFields", "GetMethod", "GetMethods", "GetProperty", "GetProperties", "GetInterfaces", "GetType", "StaticProperty", "Localized", "Scalar", "FieldString", "ReadBool", "ReadInt", "ReadUInt", "FieldIds", "EnumText", "LocalizedOrUnknown", "ContentDefinition", "AddVariants", "Aliases" }.Contains(name);
    }
    internal static void Run(string dll, string root, Action<bool, string> check)
    {
        var sites = Sites(root);
        var accesses = JsonSerializer.Deserialize<Access[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "GameAccesses.json")));
        var declared = accesses.Select(a => a.Site).ToHashSet();
        var missing = sites.Where(s => !declared.Contains(s.Key)).Select(s => $"{s.File}:{s.Line} {s.Expression}").ToArray();
        var stale = declared.Except(sites.Select(s => s.Key)).ToArray();
        check(missing.Length == 0 && stale.Length == 0, "every reflection access site and dynamic helper expansion is explicitly audited" + Errors(missing.Concat(stale)));
        check(accesses.Where(a => a.Kind == "externalContract").All(a =>
            a.Type == "UnityEngine.Application" && a.Member == "get_isBatchMode" && a.Arguments == 0
            || a.Type == "UnityEngine.Object" && a.Member == "FindObjectOfType" && a.Arguments == 1),
            "only the unused Unity batch-mode and object-finder helpers rely on API contracts absent from Hotfix member references");
        using var game = new Model(dll);
        var errors = accesses.Where(a => a.Kind is not ("helper" or "externalContract")).Select(a => (a, error: game.Verify(a))).Where(x => x.error != null)
            .Select(x => $"{sites.First(s => s.Key == x.a.Site).File}:{sites.First(s => s.Key == x.a.Site).Line} {x.a.Type}.{x.a.Member}: {x.error}").Distinct().ToArray();
        check(errors.Length == 0, "reflection receivers, inherited members, getter properties, arity, staticness and value conversions match the installed game" + Errors(errors));
        check(game.Verify(new("", "RuntimeData.MapData", "field", "Id", 0, "uint")) != null
            && game.Verify(new("", "RuntimeData.MapData", "field", "MapID", 0, "uint")) == null,
            "MapData.MapID is a numeric field and MapData.Id is rejected even when other game types declare Id");
        check(game.Verify(new("", "TH1_Logic.Steam.GameNetSender", "static", "Instance", 0, "object")) != null
            && game.Verify(new("", "TH1_Logic.Steam.GameNetSender", "static", "<Instance>k__BackingField", 0, "object")) == null,
            "static auto-properties must be read through their backing fields rather than treated as fields");
        check(game.Verify(new("", "RuntimeData.MapData", "field", "MapConfig", 0, "uint")) != null
            && game.Verify(new("", "Logic.Action.CommonActionId", "field", "ActionType", 0, "int")) == null,
            "boxed structures and numeric enum conversions are valid but reference objects cannot be read as integers");
        check(new[] { "ActionInfo", "TechInfo", "TechAtomInfo", "SkillInfo", "UnitTypeInfo" }
            .All(t => game.Verify(new("", t, "field", "Retired", 0, "byte")) == null)
            && new[] { "TerrainInfo", "ResourceInfo", "TH1_Logic.MatchConfig.WikiItem", "NaturalWonderInfo", "TH1_Data.GridContentDefinition", "HeroInfoData" }
            .All(t => game.Verify(new("", t, "field", "Retired", 0, "byte")) != null),
            "only action, technology, technology-effect, skill and unit catalog rows declare Retired");
    }
    static string Errors(IEnumerable<string> errors) => errors.Any() ? ":\n" + string.Join("\n", errors) : "";
    internal sealed class Model : ISignatureTypeProvider<string, object>, IDisposable
    {
        readonly Stream stream;
        readonly PEReader pe;
        internal readonly MetadataReader Reader;
        readonly Dictionary<string, TypeDefinitionHandle> types;
        internal Model(string path)
        {
            stream = File.OpenRead(path); pe = new(stream); Reader = pe.GetMetadataReader();
            types = Reader.TypeDefinitions.ToDictionary(h => Name(h));
        }
        internal string Name(EntityHandle h) => h.IsNil ? "" : h.Kind switch
        {
            HandleKind.TypeDefinition => Full(Reader.GetTypeDefinition((TypeDefinitionHandle)h)),
            HandleKind.TypeReference => Full(Reader.GetTypeReference((TypeReferenceHandle)h)),
            HandleKind.TypeSpecification => Reader.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(this, null),
            _ => ""
        };
        string Full(TypeDefinition t) => t.IsNested ? Name(t.GetDeclaringType()) + "+" + Reader.GetString(t.Name) : (Reader.GetString(t.Namespace) + "." + Reader.GetString(t.Name)).TrimStart('.');
        string Full(TypeReference t) => (Reader.GetString(t.Namespace) + "." + Reader.GetString(t.Name)).TrimStart('.');
        internal IEnumerable<(string Type, string Kind, string Member, string Value, int Count)> Members(string name)
        {
            if (name.Contains('<') && !name.StartsWith("<"))
            {
                int start = name.IndexOf('<');
                string definition = name[..start];
                var arguments = name[(start + 1)..^1].Split(',');
                foreach (var member in Members(definition))
                {
                    string value = member.Value;
                    for (int i = 0; i < arguments.Length; i++) value = value.Replace("!" + i, arguments[i]);
                    yield return member with { Value = value };
                }
                yield break;
            }
            if (name.StartsWith("System."))
            {
                var runtime = Type.GetType(name) ?? typeof(List<>).Assembly.GetType(name);
                if (runtime != null)
                    foreach (var m in runtime.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                        yield return (name, m.IsStatic ? "staticMethod" : "method", m.Name, RuntimeName(m.ReturnType), m.GetParameters().Length);
                yield break;
            }
            if (!types.TryGetValue(name, out var h)) yield break;
            var t = Reader.GetTypeDefinition(h);
            foreach (var f in t.GetFields())
            {
                var field = Reader.GetFieldDefinition(f);
                yield return (name, (field.Attributes & FieldAttributes.Static) != 0 ? "static" : "field", Reader.GetString(field.Name), field.DecodeSignature(this, null), 0);
            }
            foreach (var p in t.GetProperties())
            {
                var prop = Reader.GetPropertyDefinition(p); var getter = prop.GetAccessors().Getter;
                if (getter.IsNil) continue;
                var m = Reader.GetMethodDefinition(getter); var sig = m.DecodeSignature(this, null);
                yield return (name, (m.Attributes & MethodAttributes.Static) != 0 ? "staticMethod" : "method", "get_" + Reader.GetString(prop.Name), sig.ReturnType, sig.ParameterTypes.Length);
            }
            foreach (var m in t.GetMethods())
            {
                var method = Reader.GetMethodDefinition(m); var sig = method.DecodeSignature(this, null);
                yield return (name, (method.Attributes & MethodAttributes.Static) != 0 ? "staticMethod" : "method", Reader.GetString(method.Name), sig.ReturnType, sig.ParameterTypes.Length);
            }
            foreach (var member in Members(Name(t.BaseType))) yield return member;
            foreach (var contract in t.GetInterfaceImplementations())
                foreach (var member in Members(Name(Reader.GetInterfaceImplementation(contract).Interface))) yield return member;
        }
        string RuntimeName(Type t) => t.IsGenericParameter ? "!" + t.GenericParameterPosition : t == typeof(int) ? "int" : t == typeof(bool) ? "bool" : t == typeof(string) ? "string" : t == typeof(object) ? "object" : t.FullName ?? t.ToString();
        internal string Verify(Access a)
        {
            if (a.Kind == "type")
                return types.Keys.Any(t => t == a.Type || !a.Type.Contains('.') && t.Split('.').Last() == a.Type)
                    || Reader.TypeReferences.Any(h => Name(h) == a.Type) ? null : "type is absent";
            if (a.Kind is not ("externalStatic" or "externalMethod") && !KnownType(a.Type)) return "receiver type is absent";
            if (a.Kind is "externalStatic" or "externalMethod")
            {
                var references = Reader.MemberReferences.Select(h => Reader.GetMemberReference(h))
                    .Where(m => m.GetKind() == MemberReferenceKind.Method && Reader.GetString(m.Name) == a.Member)
                    .Select(m => (Type: Name(m.Parent), Signature: m.DecodeMethodSignature(this, null)))
                    .Where(m => m.Type == a.Type || a.Type == "TMPro.TMP_Text" && m.Type is "UnityEngine.Component" or "TMPro.TextMeshProUGUI")
                    .Where(m => m.Signature.ParameterTypes.Length == a.Arguments && m.Signature.Header.IsInstance == (a.Kind == "externalMethod")).ToArray();
                return references.Any(m => Compatible(m.Signature.ReturnType, a.Value)) ? null : "no compatible external API reference in the game metadata";
            }
            var matches = Members(a.Type).Where(m => m.Member == a.Member && m.Kind == a.Kind && m.Count == a.Arguments).Distinct().ToArray();
            if (matches.Length == 0) return $"no {a.Kind} with {a.Arguments} arguments";
            return matches.Any(m => Compatible(m.Value, a.Value)) ? null : $"requested {a.Value}, actual {string.Join(", ", matches.Select(m => m.Value))}";
        }
        bool KnownType(string name)
        {
            if (name.Contains('<'))
            {
                int start = name.IndexOf('<');
                return KnownType(name[..start]) && name[(start + 1)..^1].Split(',').All(KnownType);
            }
            return new[] { "object", "string", "bool", "byte", "int", "uint", "float", "double", "ulong" }.Contains(name)
                || types.ContainsKey(name) || Type.GetType(name) != null || typeof(List<>).Assembly.GetType(name) != null;
        }
        bool Compatible(string actual, string wanted)
        {
            if (wanted is "" or "object" || actual == wanted) return true;
            var numeric = new[] { "bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double", "char" };
            bool scalar = numeric.Contains(actual) || (types.TryGetValue(actual, out var h) && Name(Reader.GetTypeDefinition(h).BaseType) == "System.Enum");
            return scalar && (numeric.Contains(wanted) || wanted == "string") || actual == "string" && wanted == "string";
        }
        public void Dispose() { pe.Dispose(); stream.Dispose(); }
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "pointer";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";
        public string GetGenericMethodParameter(object context, int index) => "!!" + index;
        public string GetGenericTypeParameter(object context, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool required) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPrimitiveType(PrimitiveTypeCode code) => code switch { PrimitiveTypeCode.Boolean => "bool", PrimitiveTypeCode.Byte => "byte", PrimitiveTypeCode.SByte => "sbyte", PrimitiveTypeCode.Int16 => "short", PrimitiveTypeCode.UInt16 => "ushort", PrimitiveTypeCode.Int32 => "int", PrimitiveTypeCode.UInt32 => "uint", PrimitiveTypeCode.Int64 => "long", PrimitiveTypeCode.UInt64 => "ulong", PrimitiveTypeCode.Single => "float", PrimitiveTypeCode.Double => "double", PrimitiveTypeCode.String => "string", PrimitiveTypeCode.Object => "object", PrimitiveTypeCode.Void => "void", PrimitiveTypeCode.Char => "char", _ => code.ToString() };
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte kind) => Name(handle);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte kind) => Name(handle);
        public string GetTypeFromSpecification(MetadataReader reader, object context, TypeSpecificationHandle handle, byte kind) => Name(handle);
    }
}
