using System.Runtime.InteropServices;

namespace MsiControl.Desktop.Interop;

/// <summary>
/// 平台互操作声明。
/// </summary>
/// <remarks>
/// 只声明本任务真正用到的 API：深色标题栏、全局快捷键。
/// 任何新增声明都必须有明确用途，避免把界面层变成杂项 P/Invoke 集散地。
/// </remarks>
internal static class NativeMethods
{
    /// <summary>热键消息。</summary>
    internal const int WmHotkey = 0x0312;

    /// <summary>Windows 10 1809 及以上的沉浸式深色标题栏属性。</summary>
    internal const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>Windows 10 1803–1809 使用的旧属性编号。</summary>
    internal const int DwmwaUseImmersiveDarkModeLegacy = 19;

    /// <summary>Windows 11 的窗口圆角属性。</summary>
    internal const int DwmwaWindowCornerPreference = 33;

    /// <summary>圆角偏好值：DWMWCP_ROUND（让系统绘制圆角）。</summary>
    internal const int DwmcpRound = 2;

    /// <summary>Win32 错误码：热键已被注册。</summary>
    internal const int ErrorHotkeyAlreadyRegistered = 1409;

    internal const uint ModAlt = 0x0001;
    internal const uint ModControl = 0x0002;
    internal const uint ModShift = 0x0004;
    internal const uint ModWin = 0x0008;

    /// <summary>按住组合键时不重复触发。</summary>
    internal const uint ModNoRepeat = 0x4000;

    /// <summary>设置窗口的沉浸式深色模式。</summary>
    [DllImport("dwmapi.dll", PreserveSig = true)]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// 给控件套用 UxTheme 子主题；用于把滚动条切到深色变体（"DarkMode_Explorer"）。
    /// </summary>
    /// <remarks>主题名在旧系统上不存在时返回错误码，调用方静默忽略即可。</remarks>
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    internal static extern int SetWindowTheme(IntPtr hWnd, string? subAppName, string? subAppIdList);

    /// <summary>注册全局热键。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    /// <summary>注销全局热键。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    /// <summary>纵向滚动条标识（<c>SB_VERT</c>）。</summary>
    internal const int SB_VERT = 1;

    /// <summary>滚动信息掩码：滚动范围（<c>SIF_RANGE</c>）。</summary>
    internal const int SIF_RANGE = 0x0001;

    /// <summary>滚动信息掩码：页大小（<c>SIF_PAGE</c>）。</summary>
    internal const int SIF_PAGE = 0x0002;

    /// <summary>滚动信息掩码：当前位置（<c>SIF_POS</c>）。</summary>
    internal const int SIF_POS = 0x0004;

    /// <summary>滚动信息掩码：全部字段（<c>SIF_ALL</c>）。</summary>
    internal const int SIF_ALL = SIF_RANGE | SIF_PAGE | SIF_POS | 0x0008 | 0x0010;

    /// <summary><c>SCROLLINFO</c>：滚动条的范围、页大小与位置。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ScrollInfo
    {
        public uint cbSize;
        public int fMask;
        public int nMin;
        public int nMax;
        public uint nPage;
        public int nPos;
        public int nTrackPos;
    }

    /// <summary>读取滚动条信息。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref ScrollInfo info);

    /// <summary>设置滚动条信息。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetScrollInfo(IntPtr hwnd, int bar, ref ScrollInfo info, bool redraw);
}
