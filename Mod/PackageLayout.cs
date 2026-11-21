using System;
using System.Collections.Generic;
using System.IO;

namespace TohotopiaMod;

internal static class PackageLayout
{
    internal static string Find(IEnumerable<string> candidates, string build)
    {
        foreach (string candidate in candidates)
        {
            string stamp = Path.Combine(candidate, "package.build");
            if (!File.Exists(stamp) || File.ReadAllText(stamp).Trim() != build) continue;
            if (!File.Exists(Path.Combine(candidate, "mod_loader.txt")) ||
                File.ReadAllText(Path.Combine(candidate, "mod_loader.txt")).Trim() != "TH1_CODE_MOD_V1") continue;
            foreach (string file in new[] { "mod.dll", "settings.example.json", "AgentHost/AgentHost.exe", "Settings/TH1LLMBattlePanel.exe" })
                if (!File.Exists(Path.Combine(candidate, file))) throw new FileNotFoundException("Incomplete LLM Battle Workshop download: " + file, Path.Combine(candidate, file));
            return Path.GetFullPath(candidate);
        }
        throw new DirectoryNotFoundException("LLM Battle package not found in the loader's enabled mod folders. Verify the Workshop download and enable LLM Battle in TH1 Mod Manager.");
    }
}

internal static class RuntimeStorage
{
    internal static string DefaultRoot => TohotopiaShared.RuntimePaths.DefaultRoot;
    internal static string Prepare(string package, string data, IEnumerable<string> legacyRoots)
    {
        Directory.CreateDirectory(data);
        string marker = Path.Combine(data, ".migration-complete");
        if (!File.Exists(marker))
        {
            foreach (string legacy in legacyRoots)
            {
                if (!Directory.Exists(legacy) || Path.GetFullPath(legacy).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(data).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) continue;
                // First existing installation wins; never replace current user files.
                CopyMissing(legacy, data);
                break;
            }
            File.WriteAllText(marker, "1");
        }
        string settings = Path.Combine(data, "settings.json");
        if (!File.Exists(settings)) File.Copy(Path.Combine(package, "settings.example.json"), settings);
        return data;
    }
    static void CopyMissing(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
        {
            string target = Path.Combine(destination, Path.GetFileName(file));
            if (!File.Exists(target)) File.Copy(file, target);
        }
        foreach (string directory in Directory.GetDirectories(source))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            CopyMissing(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
