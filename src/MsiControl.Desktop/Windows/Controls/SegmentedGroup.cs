using MsiControl.Desktop.Theme;

namespace MsiControl.Desktop.Windows.Controls;

/// <summary>
/// 分段按钮组：一组扁平按钮共享「活动 / 非活动」样式。
/// </summary>
/// <remarks>
/// <para>
/// 2026-10-16 界面美化 P1：模式选择从一排同权重按钮改为分段组，
/// 活动段用强调色描边 + 强调色文字 + Surface 底表达。
/// </para>
/// <para>
/// <b>语义约束：</b>活动段只表达「请求目标」——有待生效档位时高亮待生效项，
/// 否则高亮实际档位；实际生效状态一律以硬件读回为准（任务书 §6.1）。
/// 点击按钮本身不改变高亮，避免请求失败后留下虚假的高亮状态。
/// 禁用段永不呈现活动样式（能力门控与样式不得互相矛盾）。
/// <para>
/// <b>轨道模式（<see cref="TrackColor"/>）：</b>放进 <see cref="SegmentedRail"/> 时，
/// 非活动段的底色与描边都取槽底颜色，段与段之间因此不再有缝线，
/// 整条轨道读起来是一个控件；活动段以更亮的 Surface 底色「浮」在槽里。
/// 不在轨道里（例如风扇面板的 CPU / GPU 两颗独立按钮）时保持 <see langword="null"/>，
/// 沿用「次按钮」外观。
/// </para>
/// </para>
/// </remarks>
public sealed class SegmentedGroup
{
    private readonly List<Button> _buttons = new();
    private Button? _active;
    private ThemePalette _palette = ThemePalette.Light;

    /// <summary>组内按钮（按加入顺序）。</summary>
    public IReadOnlyList<Button> Buttons => _buttons;

    /// <summary>当前活动段；无活动段时为 <see langword="null"/>。</summary>
    public Button? Active => _active;

    /// <summary>
    /// 轨道底色。非空时，非活动段的底色与描边都改为该色（与槽底融合）。
    /// </summary>
    /// <remarks>由 <see cref="SegmentedRail"/> 在换肤后提供；不设置则保持独立按钮外观。</remarks>
    public Color? TrackColor { get; set; }

    /// <summary>加入一个按钮并立即按当前样式刷新。</summary>
    public void Add(Button button)
    {
        ArgumentNullException.ThrowIfNull(button);
        _buttons.Add(button);
        Style(button);
    }

    /// <summary>设置活动段。</summary>
    /// <param name="button">活动段；传 <see langword="null"/> 表示无活动段。</param>
    /// <exception cref="ArgumentException">按钮不属于本组。</exception>
    /// <remarks>
    /// 调用方每次读回硬件状态都会调用本方法（默认 1 秒一次）。样式写入必须是幂等的：
    /// 活动段没变时不得再碰按钮的任何属性——<see cref="FlatButtonAppearance"/> 与
    /// <see cref="FocusButton.RoundedBorderColor"/> 的 setter 都会无条件
    /// <see cref="Control.Invalidate()"/>，重复写入会让整排按钮每秒重绘一次（实测
    /// 每秒 16 次按钮 WM_PAINT、界面可见闪动），因此 <see cref="Style"/> 逐项比较后再赋值。
    /// </remarks>
    public void SetActive(Button? button)
    {
        if (button is not null && !_buttons.Contains(button))
        {
            throw new ArgumentException("按钮不属于本分段组。", nameof(button));
        }

        // 活动段未变时同样要走一遍 Style：按钮的 Enabled 可能因能力门控单独变化，
        // 样式必须跟上；Style 内部逐项比较，取值相同不会触发重绘。
        _active = button;
        foreach (var item in _buttons)
        {
            Style(item);
        }
    }

    /// <summary>主题切换后重刷全部按钮样式（保持活动段状态）。</summary>
    public void ApplyPalette(ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        _palette = palette;
        foreach (var item in _buttons)
        {
            Style(item);
        }
    }

    private void Style(Button button)
    {
        var inRail = TrackColor is not null;

        // 下面每一步都先比较再赋值：这些属性的 setter 会触发 Invalidate，
        // 重复写入等于每秒把按钮重绘一遍（见 SetActive 的说明）。
        if (button.FlatStyle != FlatStyle.Flat)
        {
            button.FlatStyle = FlatStyle.Flat;
        }

        // 系统边框只能画直角，一律关掉；圆角描边交给 FocusButton 自绘。
        if (button.FlatAppearance.BorderSize != 0)
        {
            button.FlatAppearance.BorderSize = 0;
        }

        var restingFill = TrackColor ?? _palette.SurfaceAlt;
        var restingBorder = TrackColor ?? _palette.Border;

        // 段按钮也裁圆角：轨道是圆角的，段若是直角会在槽内「戳」出四个尖角。
        if (button is FocusButton focus)
        {
            focus.CornerRadius = UiFactory.DesignTokens.Radius.Control;
            focus.RoundedBorderColor = null;
        }

        if (!button.Enabled)
        {
            // 禁用段：弱化文本 + 中性样式。能力门控与样式语义不得互相矛盾——
            // 禁用的「活动段」若仍呈强调色，会被读成可用且已选中。
            Apply(button, restingFill, _palette.ForeFaint, restingBorder);
            ApplyHover(button, inRail);
            return;
        }

        var isActive = ReferenceEquals(button, _active);
        Apply(
            button,
            isActive ? _palette.Surface : restingFill,
            isActive ? _palette.Accent : _palette.Fore,
            isActive ? _palette.Accent : restingBorder);

        if (button is FocusButton styled)
        {
            // 活动段一定描边；非活动段只有在「不在轨道里」时才描边
            // （在轨道里时它的底色就是槽底，再描边会把轨道切成六个方块）。
            styled.RoundedBorderColor = isActive
                ? _palette.Accent
                : (inRail ? null : _palette.Border);
        }

        // 悬停色必须最后写：ButtonBase 在 BackColor 变化时会把 FlatAppearance 的
        // MouseOver/MouseDown 颜色清空；先写悬停色再改底色，等于每秒重设一次悬停色，
        // 每次都会 Invalidate 一次（实测表现为按钮每秒重绘一遍）。
        ApplyHover(button, inRail);
    }

    /// <summary>写入悬停/按下底色（必须在底色之后调用）。</summary>
    private void ApplyHover(Button button, bool inRail)
    {
        var hover = inRail
            ? ThemePalette.Blend(TrackColor!.Value, _palette.Fore, 0.08)
            : _palette.Hover;

        if (button.FlatAppearance.MouseOverBackColor != hover)
        {
            button.FlatAppearance.MouseOverBackColor = hover;
        }

        if (button.FlatAppearance.MouseDownBackColor != hover)
        {
            button.FlatAppearance.MouseDownBackColor = hover;
        }
    }

    /// <summary>按「背景 / 文字 / 描边」三项写入段样式，取值相同则跳过。</summary>
    private static void Apply(Button button, Color fill, Color fore, Color border)
    {
        if (button.BackColor != fill)
        {
            button.BackColor = fill;
        }

        if (button.ForeColor != fore)
        {
            button.ForeColor = fore;
        }

        if (button.FlatAppearance.BorderColor != border)
        {
            button.FlatAppearance.BorderColor = border;
        }
    }
}
