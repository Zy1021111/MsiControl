using System.Drawing.Drawing2D;

namespace MsiControl.Desktop.Windows.Controls;

/// <summary>
/// 圆角几何工具。
/// </summary>
/// <remarks>
/// <para>
/// GDI 没有原生圆角容器与圆角填充，卡片、瓷砖、徽标、按钮轨道各自复制一份
/// <see cref="GraphicsPath"/> 拼装代码的话，半径与抗锯齿策略迟早会分叉
/// （历史实现里卡片的裁剪半径与描边半径就不一致，肉眼可见边缘发毛）。
/// 这里集中一份实现，半径一律来自 <see cref="UiFactory.DesignTokens.Radius"/>。
/// </para>
/// <para>
/// <b>释放约定：</b>所有返回 <see cref="GraphicsPath"/> 的方法都由调用方 <c>using</c> 释放。
/// 曾漏掉一次导致 GDI+ 句柄持续增长，长时间运行后绘制开始掉帧。
/// </para>
/// </remarks>
internal static class RoundedGeometry
{
    /// <summary>构造圆角矩形路径。</summary>
    /// <param name="bounds">矩形范围。</param>
    /// <param name="radius">圆角半径；超出高度一半时自动收敛。</param>
    /// <returns>调用方负责释放。</returns>
    internal static GraphicsPath Path(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return path;
        }

        var r = Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2);
        if (r <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var diameter = r * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>把控件裁剪为圆角（区域随尺寸变化重建）。</summary>
    /// <param name="control">目标控件。</param>
    /// <param name="radius">圆角半径。</param>
    internal static void Clip(Control control, int radius)
    {
        ArgumentNullException.ThrowIfNull(control);

        if (control.Width <= 1 || control.Height <= 1)
        {
            // 尺寸尚未确定时清空区域：给一个 1×1 的 Region 会让控件整块消失。
            control.Region = null;
            return;
        }

        using var path = Path(new Rectangle(Point.Empty, control.Size), radius);
        var previous = control.Region;
        control.Region = new Region(path);

        // 旧区域必须释放：Region 是 GDI 对象，替换时不会自动回收。
        previous?.Dispose();
    }

    /// <summary>填充圆角矩形（自带抗锯齿设置，不改变调用方的绘图状态）。</summary>
    /// <param name="graphics">绘图面。</param>
    /// <param name="bounds">矩形范围。</param>
    /// <param name="radius">圆角半径。</param>
    /// <param name="color">填充色。</param>
    internal static void Fill(System.Drawing.Graphics graphics, Rectangle bounds, int radius, Color color)
    {
        ArgumentNullException.ThrowIfNull(graphics);

        var previous = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            using var path = Path(bounds, radius);
            using var brush = new SolidBrush(color);
            graphics.FillPath(brush, path);
        }
        finally
        {
            graphics.SmoothingMode = previous;
        }
    }

    /// <summary>描边圆角矩形。</summary>
    /// <param name="graphics">绘图面。</param>
    /// <param name="bounds">矩形范围。</param>
    /// <param name="radius">圆角半径。</param>
    /// <param name="color">线色。</param>
    internal static void Stroke(System.Drawing.Graphics graphics, Rectangle bounds, int radius, Color color)
    {
        ArgumentNullException.ThrowIfNull(graphics);

        var previous = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            using var path = Path(bounds, radius);
            using var pen = new Pen(color);
            graphics.DrawPath(pen, path);
        }
        finally
        {
            graphics.SmoothingMode = previous;
        }
    }
}
