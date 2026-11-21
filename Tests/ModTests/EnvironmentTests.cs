using System.Reflection;
using Xunit;

// Environment-dependent audits: point the variables at real artifacts to run
// them. TOHOTOPIA_LOADER_DLL = the installed TH1.Hotfix.dll,
// TOHOTOPIA_PACKAGE_DIR = a built dist\LLMBattle folder.
[Collection("Loader")]
public class EnvironmentTests
{
    [Fact]
    public void Installed_loader_exports_every_member_the_mod_uses()
    {
        string? path = Environment.GetEnvironmentVariable("TOHOTOPIA_LOADER_DLL");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) Assert.Skip("Set TOHOTOPIA_LOADER_DLL to an installed TH1.Hotfix.dll");
        AuditLoaderMetadata(path);
    }

    [Fact]
    public void Built_package_is_complete_and_self_contained()
    {
        string? root = Environment.GetEnvironmentVariable("TOHOTOPIA_PACKAGE_DIR");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) Assert.Skip("Set TOHOTOPIA_PACKAGE_DIR to a built dist\\LLMBattle folder");
        AuditBuiltPackage(Path.GetFullPath(root));
    }

    static void AuditLoaderMetadata(string path)
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
        var unresolved = GameMemberReferences(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Mod"))
            .Where(r => r.Static != null
                ? !staticFields.TryGetValue(r.Static, out var declared) || !declared.Contains(r.Name)
                : r.Field ? !allFields.Contains(r.Name)
                : !allMethods.Contains(r.Name) && !CommonMembers.Contains(r.Name) && !(r.Name.StartsWith("get_") && allProperties.Contains(r.Name[4..])))
            .Select(r => r.File + ": " + (r.Static != null ? r.Static + "." : "") + r.Name).Distinct().ToArray();
        Assert.True(unresolved.Length == 0, "every game field and method named by the mod exists in the installed game" + (unresolved.Length > 0 ? ": " + string.Join(", ", unresolved) : ""));
        GameAccessAudit.Run(path, Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Mod"), Assert.True);
        Assert.True(setOverrideCount == 2, "installed loader offers the chained SetMethodOverride used since 0.8.031");
        foreach (var (type, names) in new[] {
            ("TH1ModFramework.ModLoader", new[] { "SetMethodOverride", "TryResolveMethod", "LogToFile", "CollectModFolders", "LoadOrder" }),
            ("TH1_Logic.Core.Main", new[] { "Update", "CheckInNetLobbyButNotGame", "OnApplicationQuit" }),
            ("TH1_Logic.Steam.GameNetSender", new[] { "nkk" }),
            ("Logic.AI.IAIKernel", new[] { "wct", "wcr" }) })
            Assert.True(methods.TryGetValue(type, out var available) && names.All(available.Contains), "installed loader exports required methods: " + type);
    }

    static void AuditBuiltPackage(string root)
    {
        foreach (string file in new[] { "mod.dll", "mod_loader.txt", "mod_info.json", "package.build", "preview.png", "settings.example.json", "README.txt", "Remove-BepInEx.ps1", "Remove-BepInEx.bat", "AgentHost/AgentHost.exe", "Settings/TH1LLMBattlePanel.exe" })
            Assert.True(File.Exists(Path.Combine(root, file)), "published package includes " + file);
        foreach (string component in new[] { "AgentHost", "Settings" })
            Assert.True(!Directory.EnumerateFiles(Path.Combine(root, component), "*", SearchOption.AllDirectories)
                .Any(f => Path.GetFileName(f) is "coreclr.dll" or "hostfxr.dll" or "hostpolicy.dll" || f.EndsWith(".runtimeconfig.json") || f.EndsWith(".pdb") || f.EndsWith(component + ".dll")),
                component + " is a self-contained NativeAOT executable without runtime host files or symbols");
        var context = new System.Runtime.Loader.AssemblyLoadContext("package-test", isCollectible: true);
        try
        {
            var assembly = context.LoadFromAssemblyPath(Path.Combine(root, "mod.dll"));
            string stamp = (string)assembly.GetType("TohotopiaMod.PackageIdentity").GetField("Build", BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue();
            Assert.True(File.ReadAllText(Path.Combine(root, "package.build")).Trim() == stamp, "published entry and package stamp match");
        }
        finally { context.Unload(); }
        Assert.True(!Directory.Exists(Path.Combine(root, "runtime")) && !File.Exists(Path.Combine(root, "install-path.txt")) && !File.Exists(Path.Combine(root, "game-root.txt")), "published package contains no user data or machine path registration");
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
}
