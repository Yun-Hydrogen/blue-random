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
    public static List<LastResultRow> BuildRows(IReadOnlyList<CalledStudent> students)
    {
        var rows = new List<LastResultRow>(students.Count);
        foreach (var student in students)
        {
            rows.Add(new LastResultRow
            {
                Name = student.Name,
                BadgeBrush = BadgeFor(student.LetterColor)
            });
        }
        return rows;
    }

    /// <summary>根据信封品阶生成用于姓名左侧圆点的颜色/渐变。</summary>
    private static Brush BadgeFor(string? letterColor)
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

/// <summary>结果暂留浮窗的列表行数据。</summary>
public sealed class LastResultRow
{
    public string Name { get; init; } = string.Empty;
    public Brush BadgeBrush { get; init; } = Brushes.Transparent;
}
