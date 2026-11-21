using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Settings;

// Restores the panel's own desktop shortcut through the shell's IShellLinkW
// COM object. NativeAOT disables built-in COM interop, so the interface is
// source-generated and the object is created via a CoCreateInstance import;
// a real IShellLink is the only shortcut shape the shell reliably launches.
internal static partial class DesktopShortcut
{
    internal const string LinkName = "TOHOTOPIA大模型AI控制面板.lnk";
    static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");
    static readonly StrategyBasedComWrappers ComWrappers = new();

    internal static void Ensure()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !OperatingSystem.IsWindows()) return;
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string link = Path.Combine(desktop, LinkName);
            if (File.Exists(link)) return;
            Write(link, exe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException or ArgumentException)
        {
        }
    }

    static void Write(string link, string exe)
    {
        Guid clsid = ShellLinkClsid;
        Guid iid = typeof(IShellLinkW).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out IntPtr unknown));
        var shellLink = (IShellLinkW)ComWrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
        shellLink.SetPath(exe);
        string? workdir = Path.GetDirectoryName(exe);
        if (workdir is not null) shellLink.SetWorkingDirectory(workdir);
        shellLink.SetIconLocation(exe, 0);
        shellLink.SetDescription("TOHOTOPIA AI 接管设置与账号管理");
        ((IPersistFile)shellLink).Save(link, true);
    }

    const int CLSCTX_INPROC_SERVER = 0x1;

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, int dwClsContext, ref Guid riid, out IntPtr ppv);

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IShellLinkW
    {
        void GetPath(IntPtr file, int len, IntPtr data, int flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription(IntPtr name, int len);
        void SetDescription(string name);
        void GetWorkingDirectory(IntPtr dir, int len);
        void SetWorkingDirectory(string dir);
        void GetArguments(IntPtr args, int len);
        void SetArguments(string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation(IntPtr path, int len, out int index);
        void SetIconLocation(string path, int index);
        void SetRelativePath(string path, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath(string file);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IPersistFile
    {
        void GetClassID(out Guid clsid);
        [PreserveSig] int IsDirty();
        void Load(string file, int mode);
        void Save(string file, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted(string file);
        void GetCurFile(out string file);
    }
}
