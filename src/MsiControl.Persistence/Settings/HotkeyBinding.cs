using System.Globalization;
using System.Text.Json.Serialization;

namespace MsiControl.Persistence.Settings;

/// <summary>
/// 全局快捷键绑定。
/// </summary>
/// <remarks>
/// <para>
/// 本类型刻意**不引用 WinForms 的 <c>Keys</c> 枚举**：配置层必须能被非 UI 宿主复用，
/// 因此键名以字符串保存（如 <c>F9</c>、<c>M</c>、<c>F12</c>），由 UI 层解析。
/// </para>
/// <para>
/// 任务书 §10 / design.md §11：快捷键可配置、可关闭；冲突时提示，<b>不强制占用</b>。
/// 因此「无法注册」不是错误，而是需要向用户报告的一种状态。
/// </para>
/// </remarks>
/// <param name="Enabled">是否启用。</param>
/// <param name="Control">是否要求 Ctrl。</param>
/// <param name="Alt">是否要求 Alt。</param>
/// <param name="Shift">是否要求 Shift。</param>
/// <param name="Windows">是否要求 Win 键。</param>
/// <param name="KeyName">主键名，例如 <c>F9</c>。启用时不得为空。</param>
public sealed record HotkeyBinding(
    bool Enabled,
    bool Control,
    bool Alt,
    bool Shift,
    bool Windows,
    string KeyName)
{
    /// <summary>创建一个已关闭的绑定。</summary>
    public static HotkeyBinding Disabled(string keyName = "") => new(false, false, false, false, false, keyName);

    /// <summary>是否含有至少一个修饰键。</summary>
    /// <remarks>
    /// 派生属性一律不序列化：写进配置文件后，用户可能手工改它并以为会生效，
    /// 而它实际由 <see cref="Control"/> 等字段推导得出。
    /// </remarks>
    [JsonIgnore]
    public bool HasModifier => Control || Alt || Shift || Windows;

    /// <summary>是否已启用且配置完整。</summary>
    [JsonIgnore]
    public bool IsComplete => Enabled && !string.IsNullOrWhiteSpace(KeyName) && HasModifier;

    /// <summary>
    /// 校验绑定是否可用。
    /// </summary>
    /// <returns>问题列表。为空表示配置完整（启用时）或合法关闭。</returns>
    /// <remarks>
    /// 规则：启用时必须指定主键且至少有一个修饰键。裸功能键（如单独的 F9）会抢占全局输入，
    /// 与「不强制占用」的原则不符，因此一律要求修饰键。
    /// </remarks>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (!Enabled)
        {
            return problems;
        }

        if (string.IsNullOrWhiteSpace(KeyName))
        {
            problems.Add("快捷键已启用但未指定主键。");
        }

        if (!HasModifier)
        {
            problems.Add("快捷键至少需要一个修饰键（Ctrl / Alt / Shift / Win），避免抢占全局输入。");
        }

        return problems;
    }

    /// <summary>返回用于界面显示的快捷键文本，例如 <c>Ctrl+Alt+M</c>。</summary>
    public string Describe()
    {
        if (!Enabled)
        {
            return "已关闭";
        }

        if (string.IsNullOrWhiteSpace(KeyName))
        {
            return "未指定";
        }

        var parts = new List<string>(5);
        if (Control)
        {
            parts.Add("Ctrl");
        }

        if (Alt)
        {
            parts.Add("Alt");
        }

        if (Shift)
        {
            parts.Add("Shift");
        }

        if (Windows)
        {
            parts.Add("Win");
        }

        parts.Add(DisplayKeyName(KeyName));
        return string.Join('+', parts);
    }

    /// <summary>
    /// 把主键枚举名转换成用户看得懂的写法。
    /// </summary>
    /// <remarks>
    /// 持久层保存的是 <c>Keys</c> 枚举名（<c>D1</c>、<c>NumPad1</c> 等），
    /// 直接显示会让用户看到 <c>Ctrl+Alt+D1</c> 这种不像按键的名字。
    /// 只做显示层映射，不改存储格式，因此不影响已有设置文件。
    /// </remarks>
    private static string DisplayKeyName(string keyName)
    {
        if (keyName.Length == 2 && keyName[0] == 'D' && char.IsAsciiDigit(keyName[1]))
        {
            return keyName[1].ToString();
        }

        if (keyName.StartsWith("NumPad", StringComparison.Ordinal) && keyName.Length == 7)
        {
            return "小键盘 " + keyName[6];
        }

        return keyName;
    }

    /// <summary>规范化主键名：去空白并转为大写不变式形式。</summary>
    public HotkeyBinding Normalized()
        => this with { KeyName = KeyName.Trim().ToUpperInvariant() };

    /// <inheritdoc />
    public override string ToString() => Describe();
}

/// <summary>
/// 窗口位置与尺寸。
/// </summary>
/// <remarks>
/// design.md §4.4 要求「自动记住窗口位置和尺寸」。
/// 本类型不含 <c>System.Drawing</c> 依赖，越界校正由调用方传入屏幕边界（见 <see cref="ClampTo"/>）。
/// </remarks>
/// <param name="X">左上角 X。</param>
/// <param name="Y">左上角 Y。</param>
/// <param name="Width">宽。</param>
/// <param name="Height">高。</param>
/// <param name="Maximized">是否最大化。</param>
public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized = false)
{
    /// <summary>允许的最小宽度。</summary>
    public const int MinWidth = 360;

    /// <summary>允许的最小高度。</summary>
    public const int MinHeight = 240;

    /// <summary>尺寸是否满足最小值要求。</summary>
    /// <remarks>派生属性不序列化，理由同 <see cref="HotkeyBinding.HasModifier"/>。</remarks>
    [JsonIgnore]
    public bool IsUsable => Width >= MinWidth && Height >= MinHeight;

    /// <summary>
    /// 把窗口位置校正到给定屏幕范围内，保证标题栏可见。
    /// </summary>
    /// <param name="screenLeft">屏幕工作区左边界。</param>
    /// <param name="screenTop">屏幕工作区上边界。</param>
    /// <param name="screenWidth">屏幕工作区宽。</param>
    /// <param name="screenHeight">屏幕工作区高。</param>
    /// <returns>校正后的位置。尺寸至少为最小值，且左上角至少保留一个可抓取区域。</returns>
    /// <remarks>
    /// 场景：显示器数量或分辨率变化后，旧坐标可能落在屏幕外，导致窗口「打开即消失」。
    /// 此处只做安全校正，不改变用户意图的最大化状态。
    /// </remarks>
    public WindowPlacement ClampTo(int screenLeft, int screenTop, int screenWidth, int screenHeight)
    {
        var width = Math.Clamp(Width, MinWidth, Math.Max(MinWidth, screenWidth));
        var height = Math.Clamp(Height, MinHeight, Math.Max(MinHeight, screenHeight));

        // 保证左上角至少露出一部分标题栏，否则用户无法拖动回来。
        const int visibleMargin = 80;
        var maxX = screenLeft + Math.Max(0, screenWidth - visibleMargin);
        var maxY = screenTop + Math.Max(0, screenHeight - visibleMargin);
        var minX = screenLeft - width + visibleMargin;
        var minY = screenTop;

        return this with
        {
            X = Math.Clamp(X, minX, maxX),
            Y = Math.Clamp(Y, minY, maxY),
            Width = width,
            Height = height
        };
    }

    /// <inheritdoc />
    public override string ToString()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{Width}×{Height} @ ({X},{Y}){(Maximized ? " 最大化" : string.Empty)}");
}
