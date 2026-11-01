using System.Runtime.InteropServices;
using MsiControl.Desktop.Interop;
using MsiControl.Persistence.Settings;

namespace MsiControl.Desktop.Hotkeys;

/// <summary>
/// 一个全局快捷键定义。
/// </summary>
/// <param name="Action">动作标识，用于回传触发事件。</param>
/// <param name="DisplayName">界面显示名。</param>
/// <param name="Binding">按键绑定。</param>
public sealed record HotkeyDefinition(string Action, string DisplayName, HotkeyBinding Binding);

/// <summary>
/// 单个快捷键的注册结果。
/// </summary>
/// <param name="Action">动作标识。</param>
/// <param name="DisplayName">显示名。</param>
/// <param name="Binding">按键绑定。</param>
/// <param name="Registered">是否注册成功。</param>
/// <param name="Error">失败原因。成功时为空。</param>
public sealed record HotkeyRegistrationReport(
    string Action,
    string DisplayName,
    HotkeyBinding Binding,
    bool Registered,
    string? Error);

/// <summary>
/// 全局快捷键管理器。
/// </summary>
/// <remarks>
/// <para>
/// 任务书 §10 / design.md §11：全局快捷键可配置、可关闭；<b>冲突时提示，不强制占用。</b>
/// 因此注册失败被表达为一条报告，而不是异常——程序继续运行，由界面告知用户哪一项没生效。
/// </para>
/// <para>
/// 注册不使用 <c>MOD_NOREPEAT</c> 以外的特殊手段，也不尝试抢占已被占用的组合。
/// </para>
/// </remarks>
public sealed class HotkeyManager : IDisposable
{
    private const int BaseIdentifier = 0xA700;

    private readonly MessageWindow _window = new();
    private readonly Dictionary<int, string> _identifierToAction = new();
    private readonly List<HotkeyRegistrationReport> _reports = new();
    private bool _disposed;

    /// <summary>构造管理器。</summary>
    public HotkeyManager()
    {
        _window.HotkeyPressed += OnHotkeyPressed;
    }

    /// <summary>快捷键被触发。参数为动作标识。可能在 UI 线程触发。</summary>
    public event EventHandler<string>? Triggered;

    /// <summary>最近一次注册的逐项报告。</summary>
    public IReadOnlyList<HotkeyRegistrationReport> Reports => _reports;

    /// <summary>
    /// 应用一组快捷键定义。
    /// </summary>
    /// <returns>逐项注册结果，供界面提示冲突项。</returns>
    public IReadOnlyList<HotkeyRegistrationReport> Apply(IEnumerable<HotkeyDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ObjectDisposedException.ThrowIf(_disposed, this);

        UnregisterAll();
        _reports.Clear();

        var index = 0;
        foreach (var definition in definitions)
        {
            var report = Register(definition, BaseIdentifier + index);
            _reports.Add(report);
            index++;
        }

        return _reports;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterAll();
        _window.HotkeyPressed -= OnHotkeyPressed;
        _window.DestroyHandle();
    }

    private HotkeyRegistrationReport Register(HotkeyDefinition definition, int identifier)
    {
        var binding = definition.Binding;

        if (!binding.Enabled)
        {
            return new HotkeyRegistrationReport(definition.Action, definition.DisplayName, binding, false, "已关闭");
        }

        var problems = binding.Validate();
        if (problems.Count > 0)
        {
            return new HotkeyRegistrationReport(
                definition.Action,
                definition.DisplayName,
                binding,
                false,
                string.Join(' ', problems));
        }

        if (!TryParseKey(binding.KeyName, out var key))
        {
            return new HotkeyRegistrationReport(
                definition.Action,
                definition.DisplayName,
                binding,
                false,
                $"无法识别的按键名「{binding.KeyName}」。");
        }

        var modifiers = BuildModifiers(binding);

        if (!NativeMethods.RegisterHotKey(_window.Handle, identifier, modifiers, (uint)key))
        {
            var error = Marshal.GetLastWin32Error();
            var message = error == NativeMethods.ErrorHotkeyAlreadyRegistered
                ? $"组合 {binding.Describe()} 已被其他程序占用，未强制占用；可在设置中改为其他组合。"
                : $"注册失败（Win32 错误 {error}）。";

            return new HotkeyRegistrationReport(definition.Action, definition.DisplayName, binding, false, message);
        }

        _identifierToAction[identifier] = definition.Action;
        return new HotkeyRegistrationReport(definition.Action, definition.DisplayName, binding, true, null);
    }

    private void UnregisterAll()
    {
        foreach (var identifier in _identifierToAction.Keys.ToArray())
        {
            NativeMethods.UnregisterHotKey(_window.Handle, identifier);
        }

        _identifierToAction.Clear();
    }

    private void OnHotkeyPressed(object? sender, int identifier)
    {
        if (_identifierToAction.TryGetValue(identifier, out var action))
        {
            Triggered?.Invoke(this, action);
        }
    }

    private static uint BuildModifiers(HotkeyBinding binding)
    {
        uint modifiers = NativeMethods.ModNoRepeat;

        if (binding.Control)
        {
            modifiers |= NativeMethods.ModControl;
        }

        if (binding.Alt)
        {
            modifiers |= NativeMethods.ModAlt;
        }

        if (binding.Shift)
        {
            modifiers |= NativeMethods.ModShift;
        }

        if (binding.Windows)
        {
            modifiers |= NativeMethods.ModWin;
        }

        return modifiers;
    }

    /// <summary>
    /// 把配置中的按键名解析为 <see cref="Keys"/>。
    /// </summary>
    /// <remarks>配置层不依赖 WinForms，故键名以字符串保存，在此处完成映射。</remarks>
    public static bool TryParseKey(string? keyName, out Keys key)
    {
        key = Keys.None;

        if (string.IsNullOrWhiteSpace(keyName))
        {
            return false;
        }

        if (!Enum.TryParse(keyName.Trim(), ignoreCase: true, out key))
        {
            return false;
        }

        return key != Keys.None;
    }
}

/// <summary>
/// 仅用于接收 <c>WM_HOTKEY</c> 的隐藏窗口。
/// </summary>
internal sealed class MessageWindow : NativeWindow
{
    internal MessageWindow()
    {
        CreateHandle(new CreateParams { Caption = "MsiControl.HotkeySink" });
    }

    internal event EventHandler<int>? HotkeyPressed;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WmHotkey)
        {
            HotkeyPressed?.Invoke(this, m.WParam.ToInt32());
        }

        base.WndProc(ref m);
    }
}
