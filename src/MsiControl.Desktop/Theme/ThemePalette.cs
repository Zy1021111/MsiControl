using Microsoft.Win32;

namespace MsiControl.Desktop.Theme;

/// <summary>界面主题。</summary>
public enum ThemeMode
{
    /// <summary>浅色。</summary>
    Light = 0,

    /// <summary>深色。</summary>
    Dark = 1
}

/// <summary>
/// 主题调色板。
/// </summary>
/// <remarks>
/// <para>
/// 任务书 §10 / design.md §4.1：跟随系统明暗主题；不使用持续发光与高频动画。
/// 因此配色只提供静态色值，不含动画与渐变。
/// </para>
/// <para>
/// <b>配色令牌（2026-10-16 界面美化 P0）：</b>中性色约 85%、强调色 ≤10%、状态色按语义出现。
/// 背景按「画布 → 卡片 → 输入·抬起 → 悬停」四层拉开明度差，保证卡片与背景可分辨；
/// 文本分主 / 次 / 弱三级，主文本与次文本对所在表面对比度分别 ≥7:1 与 ≥4.5:1（WCAG AA）。
/// </para>
/// <para>
/// <b>视觉方向（2026-10-31 定稿，叶哥要求「蓝 + 黑」）：</b>主色为<b>单一冷蓝</b>
/// （深色 <c>#4C9AFF</c> / 浅色 <c>#1558C0</c>），底色压到接近纯黑
/// （深色画布 <c>#07080A</c>、卡片 <c>#0F1115</c>）。
/// 蓝色只出现在「当前生效 / 可交互」的位置：焦点、活动段、主按钮、数值强调；
/// 其余一律是黑灰中性阶，状态色（绿 / 黄 / 红）按语义出现，不与主色混用。
/// 画布与卡片的明度差保持 ≈7%，让卡片在<b>没有阴影</b>的前提下依然「浮」在画布上。
/// </para>
/// <para>
/// <b>新增两枚辅助令牌：</b><see cref="BorderStrong"/> 用于需要明确分界的容器（分段轨道、
/// 侧栏边缘）；<see cref="Highlight"/> 用于卡片 / 瓷砖顶边的一道 1px 内高光——
/// GDI 下画不出柔和投影，这道高光是「抬升感」唯一可靠的替代物。
/// </para>
/// </remarks>
/// <param name="Mode">所属主题。</param>
/// <param name="Back">窗口背景（画布）。</param>
/// <param name="Surface">区块背景（卡片）。</param>
/// <param name="SurfaceAlt">次级区块背景（读数卡片、输入框）。</param>
/// <param name="Hover">悬停增量色（按钮 / 列表项悬停，纯色不使用透明度叠加）。</param>
/// <param name="Fore">主文本。</param>
/// <param name="ForeMuted">次要文本（来源、口径说明）。</param>
/// <param name="ForeFaint">弱文本（禁用态、占位符）。</param>
/// <param name="Border">边框（发丝分隔线、卡片描边）。</param>
/// <param name="BorderStrong">强边框（分段轨道、需要明确分界的容器）。</param>
/// <param name="Highlight">内高光（卡片 / 瓷砖顶边 1px）。</param>
/// <param name="Accent">交互强调色（焦点、活动态、主按钮）。</param>
/// <param name="AccentSoft">强调色淡底（活动态容器、提示底色）。</param>
/// <param name="Brand">品牌色（MSI 红）：仅用于标识与「强冷」语义动作，不作通用强调。</param>
/// <param name="Ok">正常状态。</param>
/// <param name="Warn">降级/过期状态。</param>
/// <param name="Danger">故障状态。</param>
public sealed record ThemePalette(
    ThemeMode Mode,
    Color Back,
    Color Surface,
    Color SurfaceAlt,
    Color Hover,
    Color Fore,
    Color ForeMuted,
    Color ForeFaint,
    Color Border,
    Color BorderStrong,
    Color Highlight,
    Color Accent,
    Color AccentSoft,
    Color Brand,
    Color Ok,
    Color Warn,
    Color Danger)
{
    /// <summary>浅色主题。</summary>
    public static ThemePalette Light { get; } = new(
        ThemeMode.Light,
        Back: Color.FromArgb(0xF3, 0xF5, 0xF9),
        Surface: Color.FromArgb(0xFF, 0xFF, 0xFF),
        SurfaceAlt: Color.FromArgb(0xEC, 0xF0, 0xF7),
        Hover: Color.FromArgb(0xE0, 0xE6, 0xF0),
        Fore: Color.FromArgb(0x0B, 0x12, 0x20),
        ForeMuted: Color.FromArgb(0x52, 0x5C, 0x6B),
        ForeFaint: Color.FromArgb(0x88, 0x92, 0xA2),
        Border: Color.FromArgb(0xDA, 0xE1, 0xEB),
        BorderStrong: Color.FromArgb(0xBF, 0xC8, 0xD6),
        Highlight: Color.FromArgb(0xE8, 0xEE, 0xF7),
        Accent: Color.FromArgb(0x15, 0x58, 0xC0),
        AccentSoft: Color.FromArgb(0xE1, 0xEC, 0xFC),
        Brand: Color.FromArgb(0xE4, 0x00, 0x2B),
        Ok: Color.FromArgb(0x10, 0x7C, 0x4F),
        Warn: Color.FromArgb(0xA5, 0x6A, 0x0B),
        Danger: Color.FromArgb(0xC4, 0x2B, 0x1C));

    /// <summary>深色主题。</summary>
    public static ThemePalette Dark { get; } = new(
        ThemeMode.Dark,
        Back: Color.FromArgb(0x06, 0x07, 0x0A),
        Surface: Color.FromArgb(0x0F, 0x11, 0x15),
        SurfaceAlt: Color.FromArgb(0x17, 0x1A, 0x21),
        Hover: Color.FromArgb(0x21, 0x25, 0x2E),
        Fore: Color.FromArgb(0xEE, 0xF2, 0xF8),
        ForeMuted: Color.FromArgb(0x97, 0xA2, 0xB4),
        ForeFaint: Color.FromArgb(0x62, 0x6C, 0x7C),
        Border: Color.FromArgb(0x23, 0x28, 0x33),
        BorderStrong: Color.FromArgb(0x33, 0x3A, 0x49),
        Highlight: Color.FromArgb(0x1B, 0x20, 0x29),
        Accent: Color.FromArgb(0x4C, 0x9A, 0xFF),
        AccentSoft: Color.FromArgb(0x10, 0x1F, 0x33),
        Brand: Color.FromArgb(0xE4, 0x00, 0x2B),
        Ok: Color.FromArgb(0x4A, 0xDE, 0x80),
        Warn: Color.FromArgb(0xF5, 0xB8, 0x3D),
        Danger: Color.FromArgb(0xFF, 0x6B, 0x6B));

    /// <summary>按主题取调色板。</summary>
    public static ThemePalette For(ThemeMode mode) => mode == ThemeMode.Dark ? Dark : Light;

    /// <summary>
    /// 强调色上的前景色（主按钮文字）。
    /// </summary>
    /// <remarks>
    /// 浅色主题的强调色是深青，白字对比度足够；深色主题的强调色是亮青，
    /// 白字对比度只有 ≈1.6:1——必须改用近黑字，否则主按钮上的文字等于看不见。
    /// 曾把主按钮固定写成 <c>Color.White</c>，在深色主题下成了亮底白字。
    /// </remarks>
    public Color OnAccent => Mode == ThemeMode.Dark
        ? Color.FromArgb(0x04, 0x09, 0x12)
        : Color.FromArgb(0xFF, 0xFF, 0xFF);

    /// <summary>按比例把 <paramref name="overlay"/> 混入 <paramref name="baseColor"/>。</summary>
    /// <param name="baseColor">底色。</param>
    /// <param name="overlay">叠加色。</param>
    /// <param name="amount">叠加比例，0–1。</param>
    /// <remarks>GDI 无真正的 alpha 合成（控件底色不吃透明度），混色必须手工算，才能保持纯色语义。</remarks>
    public static Color Blend(Color baseColor, Color overlay, double amount)
    {
        var ratio = Math.Clamp(amount, 0d, 1d);
        static int Mix(int from, int to, double r) => (int)Math.Round(from + ((to - from) * r));

        return Color.FromArgb(
            Mix(baseColor.R, overlay.R, ratio),
            Mix(baseColor.G, overlay.G, ratio),
            Mix(baseColor.B, overlay.B, ratio));
    }
}

/// <summary>
/// 读取并监听 Windows 应用主题。
/// </summary>
/// <remarks>
/// 依据为 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme</c>：
/// 该值控制「应用」而非「系统」的明暗设置，正是本程序应当跟随的项。
/// 读取失败时按浅色处理，并把原因交给调用方记录，而不是静默猜测。
/// </remarks>
public static class WindowsTheme
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string ValueName = "AppsUseLightTheme";

    /// <summary>读取当前应用主题。</summary>
    /// <param name="note">读取失败时的原因说明。</param>
    public static ThemeMode Read(out string? note)
    {
        note = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath, writable: false);
            if (key?.GetValue(ValueName) is int value)
            {
                return value == 0 ? ThemeMode.Dark : ThemeMode.Light;
            }

            note = "未找到 AppsUseLightTheme 值，按浅色主题处理。";
            return ThemeMode.Light;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            note = $"读取系统主题失败：{ex.Message}。按浅色主题处理。";
            return ThemeMode.Light;
        }
    }

    /// <summary>
    /// 订阅系统主题变化。
    /// </summary>
    /// <param name="handler">变化回调。注意：可能在非 UI 线程触发，调用方须自行封送。</param>
    /// <returns>用于取消订阅的令牌。</returns>
    public static IDisposable Subscribe(EventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return new ThemeWatcher(handler);
    }

    private sealed class ThemeWatcher : IDisposable
    {
        private readonly EventHandler _handler;
        private bool _disposed;

        internal ThemeWatcher(EventHandler handler)
        {
            _handler = handler;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => _handler(sender, e);
    }
}
