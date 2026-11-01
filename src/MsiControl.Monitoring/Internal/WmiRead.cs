using System.Collections.Concurrent;
using System.Management;

namespace MsiControl.Monitoring.Internal;

/// <summary>
/// 只读 WMI 访问帮助器（监控模块专用）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么单独写一份（实机教训）：</b><see cref="ManagementBaseObject"/> 的字符串索引器在键不存在时
/// 会**抛 <see cref="ManagementException"/>（<c>0x80041002 WBEM_E_NOT_FOUND</c>）**而不是返回
/// <see langword="null"/>。用索引器「探测属性是否存在」会让单个缺失字段中断整次采样。
/// 因此这里的读取一律**按名遍历**。
/// </para>
/// <para>
/// <b>连接复用：</b><see cref="ManagementScope"/> 按命名空间缓存并只连接一次，避免每秒多次
/// WMI 连接建立（连接开销远大于查询本身）。缓存仅供单一采样循环使用；为保证并发安全，
/// 创建路径加锁。
/// </para>
/// <para><b>本类型只读</b>：不提供任何写操作入口。</para>
/// </remarks>
internal static class WmiRead
{
    /// <summary>标准 WMI 命名空间。</summary>
    public const string CimV2Namespace = @"root\cimv2";

    /// <summary>WMI 硬件/厂商命名空间。</summary>
    public const string WmiNamespace = @"root\WMI";

    private static readonly ConcurrentDictionary<string, ManagementScope> Scopes =
        new(StringComparer.Ordinal);

    private static readonly object ConnectGate = new();

    /// <summary>取得（必要时建立）指定命名空间的连接。</summary>
    public static ManagementScope Scope(string namespacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespacePath);

        var scope = Scopes.GetOrAdd(namespacePath, path => new ManagementScope(path));
        if (scope.IsConnected)
        {
            return scope;
        }

        lock (ConnectGate)
        {
            if (!scope.IsConnected)
            {
                scope.Connect();
            }
        }

        return scope;
    }

    /// <summary>
    /// 执行一次只读查询，对每个结果调用 <paramref name="onItem"/>。
    /// </summary>
    /// <param name="namespacePath">命名空间。</param>
    /// <param name="wql">WQL 查询语句。</param>
    /// <param name="onItem">结果回调。回调结束即释放该 WMI 对象，调用方不应保存其引用。</param>
    /// <param name="error">查询失败时的原因；成功为 <see langword="null"/>。</param>
    /// <param name="returnImmediately">
    /// 是否要求提供程序立即返回缓存值。默认 <see langword="true"/>。
    /// </param>
    /// <returns>成功处理的结果条数。</returns>
    /// <remarks>
    /// <para>
    /// 采用回调而非返回集合：<see cref="ManagementBaseObject"/> 是 COM 包装，
    /// 必须在使用后释放。把生命周期限制在本方法内可以避免调用方忘记释放而泄漏。
    /// </para>
    /// <para>
    /// <b><paramref name="returnImmediately"/> 的实机依据（2026-10-16）：</b>
    /// <c>Win32_PerfFormattedData_*</c> 这类性能计数类默认会**等待提供程序刷新**——
    /// 实机实测每次访问阻塞约 270 ms（本机 26 个处理器实例）。
    /// 置为 <see langword="true"/> 后按提供程序当前缓存值返回，不再阻塞采样循环。
    /// 代价是该数值可能落后于读取时刻（由提供程序的刷新周期决定，实测约 1 秒），
    /// 对 1 Hz 的仪表盘采样没有实际影响，但**必须**在读数口径说明中如实标注。
    /// </para>
    /// </remarks>
    public static int Query(
        string namespacePath,
        string wql,
        Action<ManagementBaseObject> onItem,
        out string? error,
        bool returnImmediately = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wql);
        ArgumentNullException.ThrowIfNull(onItem);

        var count = 0;
        error = null;

        try
        {
            var options = new System.Management.EnumerationOptions { ReturnImmediately = returnImmediately, Rewindable = false };
            using var searcher = new ManagementObjectSearcher(Scope(namespacePath), new ObjectQuery(wql), options);
            using var collection = searcher.Get();
            foreach (var item in collection)
            {
                using var disposable = item as IDisposable;
                onItem(item);
                count++;
            }
        }
        catch (ManagementException ex)
        {
            error = Describe(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            error = $"无权限访问 WMI：{ex.Message}";
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}：{ex.Message}";
        }

        return count;
    }

    /// <summary>按名读取属性值；不存在返回 <see langword="null"/>（不使用字符串索引器）。</summary>
    public static object? PropertyValue(ManagementBaseObject? container, string name)
    {
        if (container is null)
        {
            return null;
        }

        try
        {
            foreach (PropertyData property in container.Properties)
            {
                if (string.Equals(property.Name, name, StringComparison.Ordinal))
                {
                    return property.Value;
                }
            }
        }
        catch (ManagementException)
        {
            // 集合本身不可读：按缺失处理。
        }

        return null;
    }

    /// <summary>读取字符串属性。</summary>
    public static string? String(ManagementBaseObject? container, string name)
        => PropertyValue(container, name) as string;

    /// <summary>读取布尔属性。</summary>
    public static bool? Bool(ManagementBaseObject? container, string name)
        => PropertyValue(container, name) as bool?;

    /// <summary>读取可空整数属性（含 <see cref="ushort"/>、<see cref="uint"/> 等整型）。</summary>
    public static long? Integer(ManagementBaseObject? container, string name)
    {
        var raw = PropertyValue(container, name);
        if (raw is null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>读取可空浮点属性。</summary>
    public static double? Double(ManagementBaseObject? container, string name)
    {
        var raw = PropertyValue(container, name);
        if (raw is null)
        {
            return null;
        }

        try
        {
            return Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>把异常转成可追溯的说明（管理异常带上 HRESULT）。</summary>
    public static string Describe(Exception ex)
        => ex is ManagementException managementException
            ? $"0x{(int)managementException.ErrorCode:X8}：{managementException.Message}"
            : $"{ex.GetType().Name}：{ex.Message}";
}
