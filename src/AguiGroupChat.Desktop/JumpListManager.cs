using System.Runtime.InteropServices;

namespace AguiGroupChat.Desktop;

/// <summary>
/// Windows 跳转列表（任务栏图标右键 →「最近知聚」）：用 <c>ICustomDestinationList</c> +
/// <c>IShellLinkW</c> 为每个非私密知聚建一个启动项（<c>--group &lt;id&gt;</c>）。
/// 未显式设置 AppUserModelID，沿用系统按 exe 路径分配的默认 AppID；失败时静默降级。
/// </summary>
internal sealed class JumpListManager
{
    private const int MaxItems = 7;

    public bool SetRecentGroups(IReadOnlyList<(string Id, string Name)> groups)
    {
        try
        {
            var list = (ICustomDestinationList)new CDestinationList();
            if (groups.Count == 0)
            {
                list.DeleteList(null);
                return true;
            }

            var iid = typeof(IObjectArray).GUID;
            list.BeginList(out _, ref iid, out _);

            var collection = (IObjectCollection)new CEnumerableObjectCollection();
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) exe = "AguiGroupChat.Desktop.exe";
            foreach (var g in groups.Take(MaxItems))
            {
                if (string.IsNullOrWhiteSpace(g.Id)) continue;
                var link = (IShellLinkW)new CShellLink();
                link.SetPath(exe!);
                link.SetArguments($"--group \"{g.Id.Replace("\"", "")}\"");
                link.SetDescription(Truncate(g.Name, 100));
                link.SetIconLocation(exe!, 0);
                collection.AddObject(link);
            }
            list.AppendCategory(L10n.JumpListRecent, collection);
            list.CommitList();
            return true;
        }
        catch
        {
            // 组策略 / 远程桌面 / 旧系统：静默降级（应用功能不受影响）
            return false;
        }
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max];
    }

    [ComImport]
    [Guid("77f10cf0-3db5-4966-b520-b7c54fd35ed6")]
    [ClassInterface(ClassInterfaceType.None)]
    private class CDestinationList
    {
    }

    [ComImport]
    [Guid("2d3468c1-36a7-43b6-ac24-d3f02fd9607a")]
    [ClassInterface(ClassInterfaceType.None)]
    private class CEnumerableObjectCollection
    {
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    [ClassInterface(ClassInterfaceType.None)]
    private class CShellLink
    {
    }

    [ComImport]
    [Guid("6332debf-87b5-4670-90c0-5e57b408a49e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);
        void BeginList(out uint pcMinSlots, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string pszCategory, IObjectArray poa);
        void AppendKnownCategory(int category);
        void AddUserTasks(IObjectArray poa);
        void CommitList();
        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string? pszAppID);
        void AbortList();
    }

    [ComImport]
    [Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        void GetCount(out uint pcObjects);
        void GetAt(uint uiIndex, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    [ComImport]
    [Guid("5632B1A4-E38A-400A-928A-D4CD63230295")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectCollection : IObjectArray
    {
        void AddObject([MarshalAs(UnmanagedType.Interface)] object pvObject);
        void AddFromArray(IObjectArray poaSource);
        void RemoveObjectAt(uint uiIndex);
        void Clear();
    }

    /// <summary>vtable 顺序必须与 shobjidl.h 一致（只调用 Set*；未用的 getter 用 IntPtr 占位）。</summary>
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(IntPtr pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription(IntPtr pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory(IntPtr pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments(IntPtr pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation(IntPtr pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
