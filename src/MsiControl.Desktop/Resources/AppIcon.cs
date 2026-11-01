using System.Drawing.Imaging;
using System.Reflection;

namespace MsiControl.Desktop.Resources;

/// <summary>
/// 应用图标（多尺寸 ICO，嵌入程序集）。
/// </summary>
/// <remarks>
/// <para>
/// 图标源文件 <c>Resources/app.ico</c>（16/20/24/32/40/48/64/128/256 九档，由
/// <c>scripts/make_icon.py</c> 以矢量几何渲染后逐尺寸超采样生成）。同一份文件同时作为
/// 可执行文件图标（csproj 的 <c>ApplicationIcon</c>）与运行期图标源，二者不会走样。
/// </para>
/// <para>
/// <b>为什么每次新建 <see cref="Icon"/>：</b><see cref="Form.Icon"/> 的生命周期由窗体
/// 管理（关闭时可能释放），共享一个实例会让第二个窗体拿到已释放的 GDI 句柄。
/// 原始字节只读一次并缓存，按需构造各自实例，代价可忽略。
/// </para>
/// <para>
/// <b>位图转换不能用 <see cref="Icon.ToBitmap"/>：</b>它经 <c>Bitmap.FromHicon</c>
/// 转换，会把透明通道压成黑色（圆角瓦片的四角变黑块）。这里用
/// <see cref="Graphics.DrawIcon(Icon, Rectangle)"/> 绘制，保留 alpha。
/// </para>
/// </remarks>
internal static class AppIcon
{
    private const string ResourceName = "MsiControl.Desktop.Resources.app.ico";

    /// <summary>按目标尺寸创建图标实例（取 ICO 中匹配度最高的一帧）。</summary>
    internal static Icon Create(int size)
    {
        using var stream = OpenStream();
        return new Icon(stream, size, size);
    }

    /// <summary>窗口图标（标题栏 / 任务栏 / Alt-Tab）。</summary>
    internal static Icon CreateWindowIcon() => Create(SystemInformation.IconSize.Width);

    /// <summary>托盘图标（通知区域按小图标尺寸显示）。</summary>
    internal static Icon CreateTrayIcon()
        => Create(SystemInformation.SmallIconSize.Width);

    /// <summary>把图标栅格化为指定位图（用于界面上需要 <see cref="Image"/> 的位置）。</summary>
    internal static Bitmap CreateBitmap(int size)
    {
        using var icon = Create(size);
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        // 全限定：本程序集的命名空间里有 MsiControl.Graphics 工程，直接写 Graphics 会被优先解析成命名空间。
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawIcon(icon, new Rectangle(0, 0, size, size));
        return bitmap;
    }

    private static Stream OpenStream()
        => typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"嵌入资源 {ResourceName} 不存在：图标未随程序集发布。");
}
