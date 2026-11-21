using TohotopiaMod;
using Xunit;

[Collection("Loader")]
public class PackageLayoutTests
{
    [Fact]
    public void Workshop_package_lifecycle_and_data_migration()
    {
        string root = Path.Combine(Path.GetTempPath(), "llm-workshop-test-" + Guid.NewGuid().ToString("N"));
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
            Assert.True(PackageLayout.Find(new[] { old, workshop }, PackageIdentity.Build) == workshop, "Workshop package is located by the executing build, without path registration");
            Assert.True(!File.Exists(Path.Combine(workshop, "install-path.txt")), "subscription package requires no installer registration");
            Assert.Throws<DirectoryNotFoundException>(() => PackageLayout.Find(new[] { old }, PackageIdentity.Build));
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"maxDecisionsPerTurn\":19}");
            Directory.CreateDirectory(Path.Combine(legacy, "accounts"));
            File.WriteAllText(Path.Combine(legacy, "accounts", "token.bin"), "encrypted-fixture");
            RuntimeStorage.Prepare(workshop, data, new[] { legacy });
            Assert.True(File.ReadAllText(Path.Combine(data, "settings.json")).Contains("19") && File.Exists(Path.Combine(data, "accounts", "token.bin")), "first startup migrates legacy settings and account files outside Workshop content");
            File.WriteAllText(Path.Combine(data, "settings.json"), "{\"maxDecisionsPerTurn\":23}");
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");
            string moved = Path.Combine(root, "another-library", "1234567890");
            Directory.CreateDirectory(Path.GetDirectoryName(moved)); Directory.Move(workshop, moved);
            RuntimeStorage.Prepare(moved, data, new[] { legacy });
            Assert.True(File.ReadAllText(Path.Combine(data, "settings.json")).Contains("23"), "Workshop update or library move preserves current user configuration");
            Assert.True(!Directory.Exists(Path.Combine(moved, "runtime")), "startup never writes private runtime files into the published package");
            string fresh = Path.Combine(root, "fresh-user"); RuntimeStorage.Prepare(moved, fresh, Array.Empty<string>());
            Assert.True(File.ReadAllText(Path.Combine(fresh, "settings.json")) == "{}", "fresh subscription initializes bundled defaults automatically");
            File.Delete(Path.Combine(moved, "Settings", "TH1LLMBattlePanel.exe"));
            Assert.Throws<FileNotFoundException>(() => PackageLayout.Find(new[] { moved }, PackageIdentity.Build));
        }
        finally { Directory.Delete(root, true); }
    }
}
