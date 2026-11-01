using MsiControl.Desktop.Interop;
using MsiControl.Desktop.Windows;
using MsiControl.Desktop.Windows.Controls;

namespace MsiControl.Desktop.Theme;

/// <summary>
/// 支持按主题换肤的自绘控件。
/// </summary>
/// <remarks>
/// 实现方只需处理自身颜色，父级背景由 <see cref="ThemeApplier"/> 统一设置。
/// </remarks>
public interface IThemeAware
{
    /// <summary>应用调色板。</summary>
    void ApplyPalette(ThemePalette palette);
}

/// <summary>
/// 次要文本标记。
/// </summary>
/// <remarks>
/// 放入 <see cref="Control.Tag"/>，由 <see cref="ThemeApplier"/> 在每次主题应用时
/// 识别并把前景色换成当前主题的 <c>ForeMuted</c>。
/// 用 Tag 而不是颜色值做标记：颜色会被换肤覆盖而失效，Tag 在控件生命周期内稳定存在。
/// </remarks>
public static class MutedText
{
    /// <summary>标记对象。按引用比较，仅由 <see cref="UiFactory.CreateMutedLabel"/> 写入。</summary>
    public static readonly object Marker = new();
}

/// <summary>
/// 主题应用器。
/// </summary>
/// <remarks>
/// <para>
/// WinForms 没有内建深色主题，因此采用「显式遍历 + 逐控件着色」的方式：
/// 只为已知控件类型设置颜色，未识别的控件保持默认，避免误改系统控件内部行为。
/// 标题栏的深色化走 DWM 属性；该 API 在旧系统上不存在时静默跳过（标题栏保持系统外观），
/// 不影响窗口功能。
/// </para>
/// <para>
/// <b>次要文本换肤（2026-10-16 P0）：</b>带 <see cref="MutedText.Marker"/> 标记的标签
/// （由 <see cref="UiFactory.CreateMutedLabel"/> 产出）在每次主题应用时统一换成当前主题的
/// <c>ForeMuted</c>。标记放在 <see cref="Control.Tag"/> 里，跨主题反复切换依然有效——
/// 曾用「系统 GrayText 前景色」作标记，但首次换肤后标记即消失，明暗来回切换时
/// 内联创建的次要文本会停留在旧主题色下（已有回归测试覆盖）。
/// </para>
/// <para>
/// <b>重入保护（必须保留）：</b>窗口自身也是 <see cref="IThemeAware"/>，
/// 而窗口的 <c>ApplyPalette</c> 会回调 <see cref="Apply"/> 以便一次性刷新整棵树。
/// 若不设保护，二者会互相调用形成无限递归——这在实机启动冒烟测试中出现过
/// （表现为 <c>Stack overflow</c>）。参见 <see cref="_themeScopeDepth"/>。
/// </para>
/// </remarks>
public static class ThemeApplier
{
    /// <summary>
    /// 当前线程的主题应用深度。大于 0 表示已在一次主题遍历中，
    /// 嵌套调用直接返回，避免「窗口 ApplyPalette → Apply → 窗口 ApplyPalette」的递归。
    /// </summary>
    [ThreadStatic]
    private static int _themeScopeDepth;

    /// <summary>按钮圆角半径：全应用统一取控件级半径，避免各窗口各写一套。</summary>
    private const int CornerRadius = UiFactory.DesignTokens.Radius.Control;

    /// <summary>把主题应用到窗口及其全部子控件。</summary>
    public static void Apply(Form form, ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(palette);

        if (_themeScopeDepth > 0)
        {
            return;
        }

        _themeScopeDepth++;
        try
        {
            ApplyDarkTitleBar(form, palette.Mode == ThemeMode.Dark);
            ApplyRoundedCorners(form);
            ApplyToControl(form, palette, isRoot: true);
        }
        finally
        {
            _themeScopeDepth--;
        }
    }

    /// <summary>为窗口请求系统圆角（Windows 11；旧系统静默跳过）。</summary>
    /// <remarks>
    /// Win11 22H2 起普通窗口默认即为圆角，此处显式声明 ROUND 保证行为一致、不依赖系统默认；
    /// 属性 33 在旧系统上返回错误码，与深色标题栏一样静默忽略，不影响功能。
    /// </remarks>
    public static void ApplyRoundedCorners(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!form.IsHandleCreated)
        {
            return;
        }

        var preference = NativeMethods.DwmcpRound;
        try
        {
            _ = NativeMethods.DwmSetWindowAttribute(
                form.Handle,
                NativeMethods.DwmwaWindowCornerPreference,
                ref preference,
                sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // 无 dwmapi：保持系统默认外观。
        }
        catch (EntryPointNotFoundException)
        {
            // 无该导出：同上。
        }
    }

    /// <summary>只应用深色标题栏。</summary>
    public static void ApplyDarkTitleBar(Form form, bool dark)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!form.IsHandleCreated)
        {
            return;
        }

        var value = dark ? 1 : 0;
        try
        {
            if (NativeMethods.DwmSetWindowAttribute(
                    form.Handle,
                    NativeMethods.DwmwaUseImmersiveDarkMode,
                    ref value,
                    sizeof(int)) != 0)
            {
                // 旧版本 Windows 使用 19 号属性。
                NativeMethods.DwmSetWindowAttribute(
                    form.Handle,
                    NativeMethods.DwmwaUseImmersiveDarkModeLegacy,
                    ref value,
                    sizeof(int));
            }
        }
        catch (DllNotFoundException)
        {
            // 无 dwmapi：保持系统标题栏外观。
        }
        catch (EntryPointNotFoundException)
        {
            // 无该导出：同上。
        }
    }

    private static void ApplyToControl(Control control, ThemePalette palette, bool isRoot)
    {
        if (control is IThemeAware themeAware)
        {
            themeAware.ApplyPalette(palette);
        }

        switch (control)
        {
            case Form form:
                form.BackColor = palette.Back;
                form.ForeColor = palette.Fore;
                break;

            case TableLayoutPanel or FlowLayoutPanel or Panel:
                // 透明而非画布色：容器可能位于卡片 / 表面条（Surface）内部，
                // 强制画布色会露出与父容器不一致的色块；透明容器会正确透出父容器背景。
                // 工厂产出的容器均为双缓冲子类，透明不会引入可见闪烁。
                //
                // 例外：RoundedField 自己管理底色（圆角填充 + 描边）。设成 Transparent 后，
                // 它内部控件没覆盖到的区域会直接透出卡片底色，看起来像输入框右侧
                // 「凹下去一块更暗的矩形」（实机放大截图确认）。
                if (!isRoot)
                {
                    control.BackColor = control is RoundedField
                        ? palette.SurfaceAlt
                        : Color.Transparent;
                    control.ForeColor = palette.Fore;
                }

                break;

            case PrimaryButton primary:
                // 主按钮：强调色实底 + 与底色反差足够的前景色（深色主题下白色对比度不足，
                // 由 ThemePalette.OnAccent 给出近黑色）。
                primary.CornerRadius = CornerRadius;
                primary.FlatStyle = FlatStyle.Flat;
                primary.FlatAppearance.BorderSize = 0;
                if (primary.Enabled)
                {
                    primary.BackColor = palette.Accent;
                    primary.ForeColor = palette.OnAccent;
                    primary.RoundedBorderColor = null;
                    primary.FlatAppearance.MouseOverBackColor = ThemePalette.Blend(palette.Accent, palette.Fore, 0.14);
                    primary.FlatAppearance.MouseDownBackColor = ThemePalette.Blend(palette.Accent, palette.Back, 0.2);
                }
                else
                {
                    primary.BackColor = palette.SurfaceAlt;
                    primary.ForeColor = palette.ForeFaint;
                    primary.RoundedBorderColor = palette.Border;
                    primary.FlatAppearance.MouseOverBackColor = palette.SurfaceAlt;
                    primary.FlatAppearance.MouseDownBackColor = palette.SurfaceAlt;
                }

                break;

            case GhostButton ghost:
                // 幽灵按钮：与所在表面同色（视觉上「无底色」），悬停才浮出 Hover。
                // 无描边，但仍需裁圆角——悬停底色是 Hover，不裁就会看到方角闪一下。
                ghost.CornerRadius = CornerRadius;
                ghost.BackColor = palette.Surface;
                ghost.ForeColor = ghost.Enabled ? palette.ForeMuted : palette.ForeFaint;
                ghost.FlatStyle = FlatStyle.Flat;
                ghost.FlatAppearance.BorderSize = 0;
                ghost.RoundedBorderColor = null;
                ghost.FlatAppearance.MouseOverBackColor = palette.Hover;
                ghost.FlatAppearance.MouseDownBackColor = palette.Hover;
                break;

            case Button button:
                button.BackColor = palette.SurfaceAlt;
                button.ForeColor = button.Enabled ? palette.Fore : palette.ForeFaint;
                button.FlatStyle = FlatStyle.Flat;
                // 系统边框只能画直角，一律关掉；描边交给 FocusButton 自绘圆角。
                button.FlatAppearance.BorderSize = 0;
                button.FlatAppearance.BorderColor = palette.Border;
                // FlatStyle.Flat + UseVisualStyleBackColor=false 时悬停/按下色由 FlatAppearance 提供，
                // 无需自绘。CreateButton 已设置 UseVisualStyleBackColor=false。
                button.FlatAppearance.MouseOverBackColor = palette.Hover;
                button.FlatAppearance.MouseDownBackColor = palette.Hover;

                if (button is FocusButton roundedButton)
                {
                    roundedButton.CornerRadius = CornerRadius;
                    roundedButton.RoundedBorderColor = palette.Border;
                }

                break;

            case CheckBox or RadioButton:
                // 同上：复选框可能位于卡片内，透明才能透出卡片底色。
                control.BackColor = Color.Transparent;
                control.ForeColor = palette.Fore;
                break;

            case ComboBox combo:
                combo.BackColor = palette.SurfaceAlt;
                combo.ForeColor = palette.Fore;
                combo.FlatStyle = FlatStyle.Flat;
                break;

            case NumericUpDown numeric:
                numeric.BackColor = palette.SurfaceAlt;
                numeric.ForeColor = palette.Fore;

                // 上下箭头由系统绘制（UpDownButtons 内部固定用 SystemColors.Control 铺底），
                // 深色主题下会露出一块纯白按钮，任何 BackColor 都盖不住。这里直接隐藏它：
                // 数值仍可用 ↑/↓ 键、滚轮与直接输入调整，功能不减，视觉先干净。
                // 按类型名精确匹配：Controls 里除了按钮还有一个内部编辑框，
                // 按索引隐藏会把输入区一起藏掉。
                foreach (Control child in numeric.Controls)
                {
                    if (child.GetType().Name.Contains("UpDownButtons", StringComparison.Ordinal))
                    {
                        child.Visible = false;
                    }
                }

                break;

            case TextBox textBox:
                textBox.BackColor = palette.SurfaceAlt;
                textBox.ForeColor = palette.Fore;

                // 复合控件内部的编辑框不能加系统边框：
                // ① NumericUpDown 的内部编辑框加上 FixedSingle 后，会在圆角外框里
                //    画出一个直角矩形（实机放大截图表现「左边圆、右边直角 + 顶上一条浅灰线」）；
                // ② 这个边框用系统浅色，深色主题下比任何色块都跳。
                // 边框由外层 RoundedField 或复合控件自身负责。
                textBox.BorderStyle = textBox.Parent is NumericUpDown or RoundedField
                    ? BorderStyle.None
                    : BorderStyle.FixedSingle;
                break;

            case Label label:
                // 次要文本标记：每次主题应用都刷新，明暗来回切换不会停留在旧主题色。
                if (label.Tag == MutedText.Marker)
                {
                    label.ForeColor = palette.ForeMuted;
                }

                // 背景保持透明：Label 的透明背景受支持，会正确透出父容器颜色
                // （卡片 / 表面条取 Surface，画布取 Back）。曾在此把透明标签强制刷成
                // 画布色，导致 Surface 容器内的标签出现与父底色不一致的色块。
                break;

            case ListBox listBox:
                listBox.BackColor = palette.SurfaceAlt;
                listBox.ForeColor = palette.Fore;
                break;
        }

        foreach (Control child in control.Controls)
        {
            ApplyToControl(child, palette, isRoot: false);
        }
    }
}
