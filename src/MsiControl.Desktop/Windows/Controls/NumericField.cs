using MsiControl.Desktop.Theme;

namespace MsiControl.Desktop.Windows.Controls;

/// <summary>
/// 圆角数值输入框。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不用 <see cref="NumericUpDown"/>：</b>它是复合控件，内部固定包含一个编辑框和一个
/// 由系统绘制的上下箭头按钮。深色主题下那对箭头永远是浅色方块；隐藏它之后，按钮占位又变成
/// 一块不绘制的空腔，透出卡片底色（实机放大截图里表现为「输入框右半截凹下去」）。
/// 两种状态都不能接受，且都无法通过配色修正。
/// </para>
/// <para>
/// 因此改为「<see cref="RoundedField"/> + 单个无边框 <see cref="TextBox"/>」：
/// 内部控件只有一层，背景铺满整个圆角外框，不存在空腔。
/// 数值语义由本类自己维护——范围收敛、整数位、↑/↓ 与滚轮步进、失焦规范化，
/// 行为与 <see cref="NumericUpDown"/> 保持一致，只是没有那对系统箭头。
/// </para>
/// </remarks>
public sealed class NumericField : RoundedField
{
    private readonly TextBox _edit;
    private decimal _value;
    private bool _syncing;

    /// <summary>构造数值输入框。</summary>
    public NumericField()
        : this(new TextBox
        {
            BorderStyle = BorderStyle.None,
            TextAlign = HorizontalAlignment.Center
        })
    {
    }

    private NumericField(TextBox edit)
        : base(edit)
    {
        _edit = edit;
        _edit.TextChanged += OnTextChanged;
        _edit.Leave += (_, _) => Normalize();
        _edit.PreviewKeyDown += OnPreviewKeyDown;
        _edit.KeyDown += OnEditKeyDown;
        _edit.MouseWheel += OnMouseWheel;

        AccessibleRole = AccessibleRole.SpinButton;

        Minimum = 0m;
        Maximum = 100m;
        Value = 0m;
    }

    /// <summary>最小值。</summary>
    public decimal Minimum { get; set; }

    /// <summary>最大值。</summary>
    public decimal Maximum { get; set; } = 100m;

    /// <summary>小数位数。</summary>
    public int DecimalPlaces { get; set; }

    /// <summary>文本对齐方式（透传到内部编辑框）。</summary>
    public HorizontalAlignment TextAlign
    {
        get => _edit.TextAlign;
        set => _edit.TextAlign = value;
    }

    /// <summary>当前值（自动收敛到范围内并按小数位取整）。</summary>
    public decimal Value
    {
        get => _value;
        set => Commit(value, raiseEvent: true);
    }

    /// <summary>值变化（输入、键盘、滚轮与程序赋值统一走此事件）。</summary>
    public event EventHandler? ValueChanged;

    private void Commit(decimal candidate, bool raiseEvent)
    {
        var clamped = Clamp(candidate);
        if (clamped != _value)
        {
            _value = clamped;
            if (raiseEvent)
            {
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        // 文本始终规范化：哪怕值没变，也要把「050」「5.」「abc」这类中间态修回来。
        var text = Format(_value);
        if (_edit.Text == text)
        {
            return;
        }

        _syncing = true;
        try
        {
            _edit.Text = text;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnTextChanged(object? sender, EventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        // 输入过程中只更新数值，不回写文本：回写会把用户刚敲的「-」或「.」吃掉。
        if (!decimal.TryParse(_edit.Text, out var parsed))
        {
            return;
        }

        var clamped = Clamp(parsed);
        if (clamped == _value)
        {
            return;
        }

        _value = clamped;
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Normalize()
    {
        var text = Format(_value);
        if (_edit.Text == text)
        {
            return;
        }

        _syncing = true;
        try
        {
            _edit.Text = text;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnPreviewKeyDown(object? sender, PreviewKeyDownEventArgs e)
    {
        // 方向键在 TextBox 里默认是移动光标；这里接管为步进，与 NumericUpDown 一致。
        if (e.KeyCode is Keys.Up or Keys.Down)
        {
            e.IsInputKey = true;
        }
    }

    private void OnMouseWheel(object? sender, MouseEventArgs e)
    {
        // 只有拿到焦点才响应滚轮：否则滚过页面时数值会被无意改掉。
        if (!_edit.Focused)
        {
            return;
        }

        var step = e.Delta > 0 ? 1m : -1m;
        Value = _value + step;
    }

    private void OnEditKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Up:
                Value = _value + 1m;
                e.Handled = true;
                e.SuppressKeyPress = true;
                break;
            case Keys.Down:
                Value = _value - 1m;
                e.Handled = true;
                e.SuppressKeyPress = true;
                break;
        }
    }

    private decimal Clamp(decimal candidate)
    {
        var clamped = Math.Clamp(candidate, Minimum, Maximum);
        return DecimalPlaces <= 0
            ? Math.Round(clamped, MidpointRounding.AwayFromZero)
            : Math.Round(clamped, DecimalPlaces);
    }

    private string Format(decimal value) => value.ToString(
        "F" + Math.Max(0, DecimalPlaces),
        System.Globalization.CultureInfo.CurrentCulture);
}
