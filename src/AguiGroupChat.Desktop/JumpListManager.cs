using System.Runtime.InteropServices;

namespace AguiGroupChat.Desktop;

/// <summary>
/// Windows 跳转列表（任务栏图标右键 →「最近知聚」）：用 <c>ICustomDestinationList</c> +
/// <c>IShellLinkW</c> 为每个非私密知聚建一个启动项（<c>--group &lt;id&gt;</c>）。
/// 未显式设置 AppUserModelID，沿用系统按 exe 路径分配的默认 AppID；失败时静默降级。
///
/// <para>
/// <b>为什么这里不用接口继承</b>（1.0.346 修复闪退）：<c>[ComImport]</c> 接口再继承另一个
/// <c>[ComImport]</c> 接口时，.NET 10 的 vtable 布局并不把基接口方法排在前面，于是
/// <c>IObjectCollection.AddObject</c> 实际调到了 <c>IObjectArray.GetCount</c>——把 0 写进
/// shell link 的 vtable 指针，随后任何一次调用都触发 <see cref="AccessViolationException"/>
/// （不可捕获，进程直接消失）。因此三个 COM 接口全部**独立声明**，按 shobjidl_core.h 的
/// 原始顺序逐方法列出。
/// </para>
///
/// <para>
/// <b>标题必须写 PKEY_Title</b>：跳转列表项的显示名取自 <c>System.Title</c> 属性，
/// 只调 <c>SetDescription</c> 不算——<c>AddUserTasks</c> / <c>AppendCategory</c> 会直接返回
/// <c>E_INVALIDARG</c>（表现为 ArgumentException），列表永远不会出现。
/// </para>
/// </summary>
internal sealed class JumpListManager
{
    private const int MaxItems = 7;

    /// <summary>PKEY_Title（System.Title，FMTID_SummaryInformation / pid 2）。</summary>
    private static readonly PropertyKey TitleKey = new()
    {
        Fmtid = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"),
        Pid = 2,
    };

    public bool SetRecentGroups(IReadOnlyList<(string Id, string Name)> groups)
    {
        try
        {
            var list = (ICustomDestinationList)new CDestinationList();
            var iid = typeof(IObjectCollection).GUID;
            list.BeginList(out _, ref iid, out _);

            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) exe = "AguiGroupChat.Desktop.exe";

            // 常用任务（1.0.307）：无最近知聚时也保留
            var tasks = (IObjectCollection)new CEnumerableObjectCollection();
            AddLink(tasks, exe!, "--new-group", L10n.JumpListNewGroup);
            AddLink(tasks, exe!, "--admin", L10n.JumpListAdmin);
            list.AddUserTasks(tasks);

            if (groups.Count > 0)
            {
                var collection = (IObjectCollection)new CEnumerableObjectCollection();
                foreach (var g in groups.Take(MaxItems))
                {
                    if (string.IsNullOrWhiteSpace(g.Id)) continue;
                    AddLink(collection, exe!, $"--group \"{g.Id.Replace("\"", "")}\"", Truncate(g.Name, 100));
                }
                list.AppendCategory(L10n.JumpListRecent, collection);
            }

            list.CommitList();
            return true;
        }
        catch
        {
            // 组策略 / 远程桌面 / 旧系统：静默降级（应用功能不受影响）
            return false;
        }
    }

    private static void AddLink(IObjectCollection collection, string exe, string args, string title)
    {
        var link = (IShellLinkW)new CShellLink();
        link.SetPath(exe);
        link.SetArguments(args);
        link.SetDescription(title);
        link.SetIconLocation(exe, 0);
        SetTitle(link, title); // 显示名（System.Title），缺了会被跳转列表拒绝
        collection.AddObject(link);
    }

    /// <summary>给 shell link 写入 <c>System.Title</c>（跳转列表项的显示名）。</summary>
    private static void SetTitle(object link, string title)
    {
        var store = (IPropertyStore)link; // CShellLink 实现 IPropertyStore，QI 即得
        var pv = new PropVariant { Vt = 31 /* VT_LPWSTR */, PointerValue = Marshal.StringToCoTaskMemUni(title) };
        try
        {
            var key = TitleKey;
            store.SetValue(ref key, ref pv);
            store.Commit();
        }
        finally
        {
            Marshal.FreeCoTaskMem(pv.PointerValue);
        }
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid Fmtid;
        public int Pid;
    }

    /// <summary>
    /// PROPVARIANT 的最小布局：vt（2 字节）+ 对齐 + 联合体（x64 偏移 8）。
    /// 只用于传 VT_LPWSTR，SetValue 会拷贝字符串，调用返回后即可释放。
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public IntPtr PointerValue;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PropertyKey pkey);
        void GetValue(ref PropertyKey key, out PropVariant pv);
        void SetValue(ref PropertyKey key, ref PropVariant pv);
        void Commit();
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
        void AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string pszCategory, IObjectCollection poa);
        void AppendKnownCategory(int category);
        void AddUserTasks(IObjectCollection poa);
        void CommitList();
        void GetRemovedDestinations(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
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

    /// <summary>独立声明（不继承 <see cref="IObjectArray"/>），原因见类型注释。</summary>
    [ComImport]
    [Guid("5632B1A4-E38A-400A-928A-D4CD63230295")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectCollection
    {
        void GetCount(out uint pcObjects);
        void GetAt(uint uiIndex, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
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
