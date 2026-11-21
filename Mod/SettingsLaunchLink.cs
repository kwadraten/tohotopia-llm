using System;
using System.IO;
using System.Text;

namespace TohotopiaMod;

// MS-SHLLINK, written without COM or new native imports: HybridCLR hotfix
// assemblies cannot rely on either. Unity's AOT OpenURL opens the shortcut.
internal static class SettingsLaunchLink
{
    internal static string Write(string executable, string dataDirectory, int ownerPid)
    {
        executable = Path.GetFullPath(executable);
        dataDirectory = Path.GetFullPath(dataDirectory);
        if (ownerPid <= 0) throw new ArgumentOutOfRangeException(nameof(ownerPid));
        Directory.CreateDirectory(dataDirectory);
        string link = Path.Combine(dataDirectory, "settings-launch.lnk");
        string temporary = link + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, Create(executable, Path.GetDirectoryName(executable) ?? throw new ArgumentException("Settings executable has no parent directory", nameof(executable)),
                "--show --data-root " + Quote(dataDirectory) + " --owner-pid " + ownerPid));
            if (File.Exists(link)) File.Replace(temporary, link, null);
            else File.Move(temporary, link);
            return link;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    static byte[] Create(string target, string workingDirectory, string arguments)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode);
        writer.Write(0x4cU);
        writer.Write(new Guid("00021401-0000-0000-c000-000000000046").ToByteArray());
        writer.Write(0x020002b0U); // PreferEnvironmentPath, HasExpString, WorkingDir, Arguments, Unicode.
        writer.Write(0x20U); // Archive file.
        writer.Write(0L); writer.Write(0L); writer.Write(0L);
        writer.Write(0U); writer.Write(0); writer.Write(1U); // SW_SHOWNORMAL.
        writer.Write((ushort)0); writer.Write((ushort)0);
        writer.Write(0U); writer.Write(0U);

        WriteString(workingDirectory);
        WriteString(arguments);
        // EnvironmentVariableDataBlock is also valid for a literal absolute
        // path. PreferEnvironmentPath lets Shell resolve it without a PIDL.
        if (target.Length >= 260) throw new PathTooLongException("Settings shortcut target exceeds 259 characters");
        writer.Write(0x314U); writer.Write(0xa0000001U);
        byte[] ansi = new byte[260], unicode = new byte[520];
        Encoding.ASCII.GetBytes(target, 0, target.Length, ansi, 0);
        Encoding.Unicode.GetBytes(target, 0, target.Length, unicode, 0);
        writer.Write(ansi); writer.Write(unicode);
        writer.Write(0U); // ExtraData terminal block.
        return stream.ToArray();

        void WriteString(string value)
        {
            if (value.Length > ushort.MaxValue) throw new ArgumentException("Shortcut argument is too long");
            writer.Write((ushort)value.Length);
            writer.Write(Encoding.Unicode.GetBytes(value));
        }
    }
}
