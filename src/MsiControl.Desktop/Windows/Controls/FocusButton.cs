using MsiControl.Desktop.Theme;

namespace MsiControl.Desktop.Windows.Controls;

/// <summary>
/// 带键盘焦点强调描边的按钮。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FlatStyle.Flat"/> 按钮的系统焦点框（虚线）在深色主题下辨识度低；
/// 本按钮在持有键盘焦点且系统允许显示焦点提示（<see cref="Control.ShowFocusCues"/>，
/// 即键盘触发而非鼠标点击）时，于边框内侧再画一圈强调色描边。
/// </para>
/// <para>
/// 基础配色仍由 <see cref="ThemeApplier"/> 统一管理：本类只在主题应用时记录调色板，
/// 焦点 / 失焦时请求重绘；<see cref="SegmentedGroup"/> 的分段样式在其后叠加，互不冲突。
/// </para>
/// </remarks>
public class FocusButton : Button, IThemeAware
{
    private ThemePalette? _palette;
    private Color? _roundedBorderColor;
    private int _cornerRadius;

    /// <summary>
    /// 圆角半径。0 表示保持系统直角外观。
    /// </summary>
    /// <remarks>
    /// <para>
    /// WinForms 没有圆角按钮，<see cref="FlatAppearance"/> 也只能画直角边框。
    /// 这里用两条腿拼出真圆角：半径大于 0 时把 <see cref="Control.Region"/> 裁成圆角矩形
    /// （底色、悬停色一起变圆），同时关掉系统边框、由 <see cref="RoundedBorderColor"/>
    /// 在裁剪区内部自绘圆角描边。
    /// </para>
    /// <para>
    /// 缺一不可：只裁 Region 会得到一块没有边框的圆角色块；只画描边则直角边框的四角
    /// 会从圆角处戳出来。早先只有分段轨道里的按钮这么处理，同一屏里两种圆角语言并存，
    /// 观感很散（叶哥 2026-10-31 指出「把这些都改成圆角的」）。
    /// </para>
    /// </remarks>
    internal int CornerRadius
    {
        get => _cornerRadius;
        set
        {
            var radius = Math.Max(0, value);
            if (_cornerRadius == radius)
            {
                return;
            }

            _cornerRadius = radius;
            ApplyClip();
            Invalidate();
        }
    }

    /// <summary>
    /// 自绘圆角描边色。为 <see langword="null"/> 时不画描边（实底主按钮、幽灵按钮）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="FlatAppearance"/> 只能画直角边框；放进圆角轨道（<see cref="SegmentedRail"/>）后，
    /// 若靠 <see cref="Control.Region"/> 裁圆角，直角边框的四个角会被裁掉而<b>圆弧本身不存在</b>，
    /// 结果是边框在四角断成四段带缺口的直线（首版放大截图可见）。
    /// </para>
    /// <para>
    /// 因此轨道内的段按钮改为：关掉系统边框（BorderSize=0），由本类在裁剪区域内
    /// 按半径 <c>Control-1</c> 自绘一圈圆角描边——圆弧落在裁剪区内部，才不会被切掉。
    /// </para>
    /// </remarks>
    internal Color? RoundedBorderColor
    {
        get => _roundedBorderColor;
        set
        {
            if (_roundedBorderColor == value)
            {
                return;
            }

            _roundedBorderColor = value;
            Invalidate();
        }
    }

    /// <inheritdoc />
    public void ApplyPalette(ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        _palette = palette;
        Invalidate();
    }

    /// <inheritdoc />
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyClip();
    }

    /// <inheritdoc />
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyClip();
    }

    /// <inheritdoc />
    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    /// <inheritdoc />
    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    /// <inheritdoc />
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (_palette is null || Width <= 4 || Height <= 4)
        {
            return;
        }

        if (_cornerRadius > 0 && _roundedBorderColor is { } borderColor)
        {
            // 描边半径与裁剪半径一致：圆弧正好落在裁剪边界上，既不被切掉也不露出直角。
            RoundedGeometry.Stroke(
                e.Graphics,
                new Rectangle(0, 0, Width - 1, Height - 1),
                _cornerRadius,
                borderColor);
        }

        if (!Focused || !ShowFocusCues)
        {
            return;
        }

        // 焦点环画在圆角内侧 2px 处：与按钮本体的圆角保持一致，
        // 否则方形描边会从圆角处「切」出来，看起来像是两张皮。
        var bounds = new Rectangle(2, 2, Width - 5, Height - 5);
        RoundedGeometry.Stroke(e.Graphics, bounds, Math.Max(1, _cornerRadius - 2), _palette.Accent);
    }

    /// <summary>按当前圆角半径重设裁剪区域。</summary>
    private void ApplyClip()
    {
        if (_cornerRadius <= 0)
        {
            var previous = Region;
            Region = null;
            previous?.Dispose();
            return;
        }

        RoundedGeometry.Clip(this, _cornerRadius);
    }
}

/// <summary>
/// 主按钮：强调色实底。
/// </summary>
/// <remarks>
/// <para>
/// 每个界面只允许出现一到两个主按钮——它是这一屏最重要的动作（「应用曲线」「保存设置」）。
/// 与次按钮的差别不靠边框深浅，而靠<b>实底强调色</b>，这样在深色主题下仍能一眼分出主次。
/// </para>
/// <para>
/// 配色不能走 <see cref="ThemeApplier"/> 的通用按钮分支（那里统一给次按钮配色），
/// 因此 <see cref="ThemeApplier"/> 对 <see cref="PrimaryButton"/> 单开一条分支；
/// 早期实现是在窗口换肤<b>之后</b>再手动刷一次色，任何新增界面漏掉这一步就会得到一个
/// 「长得像次按钮的主按钮」，属于靠调用顺序维持正确性的脆弱写法。
/// </para>
/// </remarks>
public sealed class PrimaryButton : FocusButton
{
}

/// <summary>
/// 幽灵按钮：无底色、无边框，仅文字。
/// </summary>
/// <remarks>
/// 用于容器标题行里的辅助动作（展开 / 收起）。这类动作属于<b>第三层级</b>：
/// 它不该和卡片内容区的操作抢注意力，常驻边框反而让标题行显得笨重。
/// 悬停时给一层 <see cref="ThemePalette.Hover"/> 底色即可确认可点。
/// </remarks>
public sealed class GhostButton : FocusButton
{
}
