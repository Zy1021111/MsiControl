using MsiControl.Desktop.Resources;
using MsiControl.Desktop.Theme;

namespace MsiControl.Desktop.Windows;

/// <summary>切换显卡模式后，用户对「是否立即重启」的选择。</summary>
public enum RestartChoice
{
    /// <summary>立即重启系统。</summary>
    RestartNow = 1,

    /// <summary>稍后自行重启。</summary>
    Later = 2
}

/// <summary>
/// 「显卡模式已提交，是否立即重启」询问框。
/// </summary>
/// <remarks>
/// <para>
/// 只在显卡模式切换**真的写入并待生效**（<see cref="MsiControl.Core.OperationStatus.PendingRestart"/>）
/// 之后弹出：这时重启是唯一让它生效的动作，问一句能省掉「改了但没生效」的困惑。
/// 「已是目标模式、无需切换」不弹——没有东西需要重启才生效。
/// </para>
/// <para>
/// <b>默认按钮是「稍后重启」</b>：重启是破坏性操作，键盘回车/Esc 都不应误触发它；
/// 立即重启必须由用户明确按下 1 号按钮或数字键 1。
/// </para>
/// </remarks>
public sealed class RestartPromptForm : Form, IThemeAware
{
    private readonly TableLayoutPanel _stack = UiFactory.CreateStack();
    // 默认动作是「稍后重启」，因此它才是主按钮；立即重启是破坏性动作，保持次按钮形态。
    // 主次与默认行为不一致（把破坏性动作做成最醒目的那颗）是这类对话框最常见的误导。
    private readonly Button _btnRestart = UiFactory.CreateButton("1 立即重启");
    private readonly Button _btnLater = UiFactory.CreatePrimaryButton("2 稍后重启");
    private ThemePalette _palette = ThemePalette.Dark;

    private RestartChoice _choice = RestartChoice.Later;

    /// <summary>构造询问框。</summary>
    /// <param name="targetModeText">已提交的目标模式中文名（用于标题与正文）。</param>
    /// <param name="detail">补充说明（写入与读回结果、生效方式）。</param>
    /// <param name="palette">当前主题调色板。</param>
    public RestartPromptForm(string targetModeText, string detail, ThemePalette palette)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetModeText);

        _palette = palette;

        Text = "显卡模式切换已提交";
        Icon = AppIcon.CreateWindowIcon();
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = UiFactory.BaseFont;
        ClientSize = new Size(560, 236);

        BuildLayout(targetModeText, detail);

        AcceptButton = _btnLater;
        CancelButton = _btnLater;
    }

    /// <summary>模态显示并返回用户选择。</summary>
    /// <param name="owner">父窗口；为 <see langword="null"/> 时以当前活动窗口为父。</param>
    /// <param name="targetModeText">目标模式中文名。</param>
    /// <param name="detail">补充说明。</param>
    /// <param name="palette">当前主题。</param>
    public static RestartChoice Ask(
        IWin32Window? owner,
        string targetModeText,
        string detail,
        ThemePalette palette)
    {
        using var form = new RestartPromptForm(targetModeText, detail, palette);
        form.ApplyPalette(palette);

        if (owner is null)
        {
            _ = form.ShowDialog();
        }
        else
        {
            _ = form.ShowDialog(owner);
        }

        return form._choice;
    }

    /// <inheritdoc />
    public void ApplyPalette(ThemePalette palette)
    {
        _palette = palette;
        ThemeApplier.Apply(this, palette);
        _stack.BackColor = palette.Back;
    }

    private void BuildLayout(string targetModeText, string detail)
    {
        var heading = new Label
        {
            // 字号与正文拉开一档：这是「要你做一个决定」的标题，不是普通说明。
            Font = UiFactory.Scale(UiFactory.BaseFont, UiFactory.DesignTokens.Type.Title, FontStyle.Bold),
            Text = $"已切换到「{targetModeText}」，需要重启才生效",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, UiFactory.DesignTokens.Space.Sm)
        };

        var body = new Label
        {
            Text = string.IsNullOrWhiteSpace(detail)
                ? "现在重启会立即按新的显卡模式启动；选择稍后则由你在方便时重启。"
                : detail,
            AutoSize = true,
            MaximumSize = new Size(ClientSize.Width - UiFactory.DesignTokens.Space.Lg * 2, 0),
            Margin = new Padding(0, 0, 0, UiFactory.DesignTokens.Space.Lg)
        };
        body.Tag = MutedText.Marker;

        var buttons = UiFactory.CreateButtonRow(2);
        _btnRestart.Click += (_, _) => Choose(RestartChoice.RestartNow);
        _btnLater.Click += (_, _) => Choose(RestartChoice.Later);
        UiFactory.PlaceButton(buttons, _btnRestart, 0);
        UiFactory.PlaceButton(buttons, _btnLater, 1);

        // 两个按钮等高：主按钮默认 34px，次按钮 30px，并排时会差 4px。
        // 这一行里两者是同等重要的两个选择，高度差会读成「上面那个更大」的暗示。
        _btnRestart.Height = UiFactory.DesignTokens.Height.ButtonPrimary;
        _btnLater.Height = UiFactory.DesignTokens.Height.ButtonPrimary;

        _stack.Padding = new Padding(
            UiFactory.DesignTokens.Space.Lg,
            UiFactory.DesignTokens.Space.Lg,
            UiFactory.DesignTokens.Space.Lg,
            UiFactory.DesignTokens.Space.Lg);

        UiFactory.AddRow(_stack, heading);
        UiFactory.AddRow(_stack, body);
        UiFactory.AddRow(_stack, buttons);

        Controls.Add(_stack);
        _stack.Dock = DockStyle.Fill;
    }

    /// <inheritdoc />
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // 数字键直接对应两个选项（叶哥要求的「1 立即重启 / 2 稍后重启」可不看鼠标完成）。
        switch (keyData)
        {
            case Keys.D1 or Keys.NumPad1:
                Choose(RestartChoice.RestartNow);
                return true;
            case Keys.D2 or Keys.NumPad2 or Keys.Escape:
                Choose(RestartChoice.Later);
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void Choose(RestartChoice choice)
    {
        _choice = choice;
        DialogResult = choice == RestartChoice.RestartNow ? DialogResult.Yes : DialogResult.No;
        Close();
    }
}
