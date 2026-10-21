using System.Runtime.InteropServices;

namespace AguiGroupChat.Desktop;

/// <summary>
/// Windows 任务栏缩略图工具栏（<c>ITaskbarList3</c>）：悬停任务栏图标时显示「打开 / 静音」按钮，
/// 并用覆盖图标显示未读数。仅在 Windows 7+ 桌面会话可用；任何失败都静默降级（托盘菜单仍可用）。
/// </summary>
internal sealed class TaskbarThumbnailBar : IDisposable
{
    public const int OpenButtonId = 1;
    public const int MuteButtonId = 2;

    private ITaskbarList3? _taskbar;
    private IntPtr _hwnd;
    private bool _buttonsAdded;

    /// <summary>绑定窗口句柄（在 <c>OnSourceInitialized</c> 调用）。失败时本类退化为空操作。</summary>
    public void Initialize(IntPtr hwnd)
    {
        if (_taskbar is not null || hwnd == IntPtr.Zero) return;
        _hwnd = hwnd;
        _taskbar = (ITaskbarList3)new CTaskbarList();
        _taskbar.HrInit();
    }

    /// <summary>首次添加 / 后续更新两个按钮（图标由调用方持有，调用期间必须有效）。</summary>
    public void SetButtons(
        System.Drawing.Icon openIcon, string openTip,
        System.Drawing.Icon muteIcon, string muteTip)
    {
        if (_taskbar is null || _hwnd == IntPtr.Zero) return;
        var buttons = new[]
        {
            CreateButton(OpenButtonId, openIcon, openTip),
            CreateButton(MuteButtonId, muteIcon, muteTip),
        };
        if (_buttonsAdded)
        {
            _taskbar.ThumbBarUpdateButtons(_hwnd, (uint)buttons.Length, buttons);
        }
        else
        {
            _taskbar.ThumbBarAddButtons(_hwnd, (uint)buttons.Length, buttons);
            _buttonsAdded = true;
        }
    }

    /// <summary>设置任务栏覆盖图标（未读徽标）；<paramref name="icon"/> 为 null 时清除。</summary>
    public void SetOverlay(System.Drawing.Icon? icon, string description)
    {
        if (_taskbar is null || _hwnd == IntPtr.Zero) return;
        _taskbar.SetOverlayIcon(_hwnd, icon?.Handle ?? IntPtr.Zero, description ?? "");
    }

    /// <summary>任务栏进度（1.0.306）：none / indeterminate / normal / paused / error。</summary>
    public void SetProgress(string state, ulong completed, ulong total)
    {
        if (_taskbar is null || _hwnd == IntPtr.Zero) return;
        var flag = state switch
        {
            "indeterminate" => 1, // TBPF_INDETERMINATE
            "normal" => 2,        // TBPF_NORMAL
            "paused" => 8,        // TBPF_PAUSED
            "error" => 4,         // TBPF_ERROR
            _ => 0,               // TBPF_NOPROGRESS
        };
        _taskbar.SetProgressState(_hwnd, flag);
        if (flag is 2 or 8)
            _taskbar.SetProgressValue(_hwnd, completed, total == 0 ? 1 : total);
    }

    public void Dispose() => _taskbar = null;

    private static THUMBBUTTON CreateButton(uint id, System.Drawing.Icon icon, string tip) => new()
    {
        dwMask = THUMBBUTTONMASK.THB_ICON | THUMBBUTTONMASK.THB_TOOLTIP | THUMBBUTTONMASK.THB_FLAGS,
        iId = id,
        hIcon = icon.Handle,
        szTip = tip,
        dwFlags = THUMBBUTTONFLAGS.THBF_ENABLED,
    };

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class CTaskbarList
    {
    }

    /// <summary>完整 vtable 顺序必须与 shobjidl.h 一致（未用到的方法也不能缺）。</summary>
    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);

        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

        // ITaskbarList3
        void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(IntPtr hwnd, int tbpFlags);
        void RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
        void UnregisterTab(IntPtr hwndTab);
        void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        void SetTabActive(IntPtr hwndTab, IntPtr hwndInsertBefore, uint dwReserved);
        void ThumbBarAddButtons(IntPtr hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] THUMBBUTTON[] pButtons);
        void ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] THUMBBUTTON[] pButtons);
        void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
        void SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string pszDescription);
        void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string pszTip);
        void SetThumbnailClip(IntPtr hwnd, IntPtr prcClip);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct THUMBBUTTON
    {
        public THUMBBUTTONMASK dwMask;
        public uint iId;
        public uint iBitmap;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szTip;
        public THUMBBUTTONFLAGS dwFlags;
    }

    [Flags]
    private enum THUMBBUTTONMASK : uint
    {
        THB_BITMAP = 0x1,
        THB_ICON = 0x2,
        THB_TOOLTIP = 0x4,
        THB_FLAGS = 0x8,
    }

    [Flags]
    private enum THUMBBUTTONFLAGS : uint
    {
        THBF_ENABLED = 0x0,
        THBF_DISABLED = 0x1,
        THBF_DISMISSONCLICK = 0x2,
        THBF_NOBACKGROUND = 0x4,
        THBF_HIDDEN = 0x8,
        THBF_NONINTERACTIVE = 0x10,
    }
}
