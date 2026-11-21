// ==============================================================================
// 文件: LastResultPresenter.cs
// 命名空间: BlueRandom.Modules.WindowsManager
// 作用: 结果暂留浮窗（LRW）数据呈现与样式映射层。
//       负责将抽取结果数据映射为列表行视图模型（LastResultRow），并处理徽标配色与画刷冻结。
// ==============================================================================

using System;
using System.Collections.Generic;
using System.Windows.Media;
using BlueRandom.Modules.ConfigProvider;

namespace BlueRandom.Modules.WindowsManager;

/// <summary>
/// 结果暂留浮窗（LRW）的数据呈现器与画刷转换器。
/// </summary>
internal static class LastResultPresenter
{
    public static List<LastResultRow> BuildRows(IReadOnlyList<CalledStudent> students, double opacity = 1.0)
    {
        var rows = new List<LastResultRow>(students.Count);
        bool single = students.Count == 1;

        foreach (var student in students)
        {
            var (bgBrush, borderBrush) = PaletteFor(student.LetterColor, opacity);
            double width = single ? 286 : ComputeBlockWidth(student.Name);
            double height = single ? 46 : 40;
            double fontSize = single ? 18 : 15.5;

            rows.Add(new LastResultRow
            {
                Name = student.Name,
                BadgeBrush = BadgeFor(student.LetterColor),
                BackgroundBrush = bgBrush,
                BorderBrush = borderBrush,
                BlockWidth = width,
                BlockHeight = height,
                FontSize = fontSize
            });
        }
        return rows;
    }

    /// <summary>
    /// 根据学生姓名视觉字符长度自适应计算方块卡片宽度（96 ~ 140 DIP），
    /// 两列瀑布流排版下卡片长度依姓名长短自适应伸缩，且每行至多容纳 2 张卡片，绝不挤压成三列。
    /// </summary>
    public static double ComputeBlockWidth(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 96;

        double visualLength = 0;
        foreach (char c in name)
        {
            visualLength += (c > 127) ? 16.0 : 9.5;
        }

        // 基础内外边距约 62 DIP，梯度映射在 [96, 140] 区间内
        return Math.Round(Math.Clamp(visualLength + 62, 96, 140));
    }

    /// <summary>根据信封品阶生成用于姓名左侧圆点的发光渐变颜色。</summary>
    public static Brush BadgeFor(string? letterColor)
    {
        switch ((letterColor ?? "rainbow").ToLowerInvariant())
        {
            case "gold":
                return new LinearGradientBrush(
                    Color.FromRgb(0xFF, 0xD3, 0x6B),
                    Color.FromRgb(0xE8, 0xA9, 0x2E), 45);

            case "blue":
                return new LinearGradientBrush(
                    Color.FromRgb(0x8A, 0xDB, 0xFF),
                    Color.FromRgb(0x3D, 0xA9, 0xE8), 45);

            default:
                // 彩信封（三星）在暂留浮窗中用紫色圆点表示
                return new LinearGradientBrush(
                    Color.FromRgb(0xC9, 0x8B, 0xFF),
                    Color.FromRgb(0x7C, 0x4D, 0xE6), 45);
        }
    }

    /// <summary>
    /// 根据信封品阶生成对应淡色背景画刷与柔和边框画刷，并注入卡片的不透明度。
    /// </summary>
    public static (Brush Background, Brush Border) PaletteFor(string? letterColor, double opacity)
    {
        double alpha = Math.Clamp(opacity, 0.1, 1.0);
        uint bgRgb;
        uint borderRgb;

        switch ((letterColor ?? "rainbow").ToLowerInvariant())
        {
            case "gold":
                // 暖金淡黄
                bgRgb = 0xFFF9E6;
                borderRgb = 0xFFE194;
                break;

            case "blue":
                // 天蓝淡青
                bgRgb = 0xEDF8FF;
                borderRgb = 0xBCE4FF;
                break;

            default:
                // 彩信封：淡雅紫粉
                bgRgb = 0xF6EEFF;
                borderRgb = 0xDFCAFF;
                break;
        }

        uint bgArgb = ToArgb(bgRgb, alpha);
        uint borderArgb = ToArgb(borderRgb, alpha);
        return (BrushOf(bgArgb), BrushOf(borderArgb));
    }

    private static uint ToArgb(uint rgb24, double alpha)
    {
        byte a = (byte)Math.Round(alpha * 255);
        return ((uint)a << 24) | (rgb24 & 0x00FFFFFF);
    }

    /// <summary>把 0xAARRGGBB 转成冻结的纯色画刷。</summary>
    public static SolidColorBrush BrushOf(uint argb)
    {
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF)));
        brush.Freeze();
        return brush;
    }
}

/// <summary>结果暂留浮窗的方块卡片数据。</summary>
public sealed class LastResultRow
{
    public string Name { get; init; } = string.Empty;
    public Brush BadgeBrush { get; init; } = Brushes.Transparent;
    public Brush BackgroundBrush { get; init; } = Brushes.Transparent;
    public Brush BorderBrush { get; init; } = Brushes.Transparent;
    public double BlockWidth { get; init; } = 140;
    public double BlockHeight { get; init; } = 44;
    public double FontSize { get; init; } = 15.5;
}
