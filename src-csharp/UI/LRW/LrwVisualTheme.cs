// ==============================================================================
// 文件: LrwVisualTheme.cs
// 命名空间: BlueRandom.UI.LRW
// 作用: 结果暂留浮窗的配色推导（纯函数，无 WPF 依赖）。
//       把「底色 + 边框色 + 不透明度」换算成卡片与各子元素的实际 ARGB 颜色，
//       保证半透明卡片内的姓名片、徽标、进度条不再残留实心白块，并便于单元测试。
// ==============================================================================

using System;

namespace BlueRandom.UI.LRW;

/// <summary>结果暂留浮窗一次渲染所需的全部颜色快照。</summary>
public readonly record struct LrwPalette(
    uint CardBackground,
    uint Accent,
    uint RowBackground,
    uint BadgeBackground,
    uint CloseHover,
    uint ProgressTrack)
{
    /// <summary>取指定颜色的 8 位 Alpha 通道。</summary>
    public static byte AlphaOf(uint argb) => (byte)((argb >> 24) & 0xFF);
}

/// <summary>把配置里的颜色字符串与不透明度换算为 <see cref="LrwPalette"/>。</summary>
public static class LrwVisualTheme
{
    /// <summary>默认卡片底色（白）。</summary>
    public const string DefaultBackground = "#ffffff";

    /// <summary>默认强调色（天蓝，同时用于边框、进度条与标题装饰）。</summary>
    public const string DefaultAccent = "#66ccff";

    /// <summary>不透明度下限，与配置模型的下限保持一致。</summary>
    public const double MinOpacity = 0.1;

    /// <summary>
    /// 由配置值推导配色。<paramref name="bg"/> / <paramref name="accent"/>
    /// 解析失败时回退到默认色；不透明度会被夹取到 [<see cref="MinOpacity"/>, 1]。
    /// </summary>
    public static LrwPalette Compute(string? bg, string? accent, double opacity)
    {
        var backgroundColor = Parse(bg, DefaultBackground);
        var accentColor = Parse(accent, DefaultAccent);
        double alpha = Math.Clamp(opacity, MinOpacity, 1.0);

        return new LrwPalette(
            CardBackground: ToArgb(backgroundColor, alpha),
            Accent: ToArgb(accentColor, alpha),
            RowBackground: ToArgb(Blend(backgroundColor, accentColor, 0.08), alpha),
            BadgeBackground: ToArgb(Blend(backgroundColor, accentColor, 0.22), alpha),
            CloseHover: ToArgb(Blend(backgroundColor, accentColor, 0.26), alpha),
            ProgressTrack: ToArgb(Blend(backgroundColor, accentColor, 0.18), alpha));
    }

    /// <summary>把 "#RRGGBB" / "#AARRGGBB" 解析为 RGB（丢弃原有 Alpha），失败时回退到 <paramref name="fallback"/>。</summary>
    public static (byte R, byte G, byte B) Parse(string? value, string fallback)
    {
        if (TryParse(value, out var rgb)) return rgb;
        if (TryParse(fallback, out rgb)) return rgb;
        return (0xFF, 0xFF, 0xFF);
    }

    private static bool TryParse(string? value, out (byte R, byte G, byte B) rgb)
    {
        rgb = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        string text = value.Trim().TrimStart('#');
        if (text.Length == 8) text = text[2..];   // 忽略调用方自带的 Alpha
        if (text.Length != 6) return false;

        if (!byte.TryParse(text[..2], System.Globalization.NumberStyles.HexNumber, null, out byte r)) return false;
        if (!byte.TryParse(text[2..4], System.Globalization.NumberStyles.HexNumber, null, out byte g)) return false;
        if (!byte.TryParse(text[4..6], System.Globalization.NumberStyles.HexNumber, null, out byte b)) return false;

        rgb = (r, g, b);
        return true;
    }

    /// <summary>把底色向强调色混合（0 = 底色，1 = 强调色）。</summary>
    private static (byte R, byte G, byte B) Blend(
        (byte R, byte G, byte B) baseColor, (byte R, byte G, byte B) tint, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return (
            (byte)Math.Round(baseColor.R + (tint.R - baseColor.R) * amount),
            (byte)Math.Round(baseColor.G + (tint.G - baseColor.G) * amount),
            (byte)Math.Round(baseColor.B + (tint.B - baseColor.B) * amount));
    }

    /// <summary>合成 0xAARRGGBB。</summary>
    private static uint ToArgb((byte R, byte G, byte B) rgb, double alpha)
        => ((uint)Math.Round(Math.Clamp(alpha, 0, 1) * 255) << 24)
           | ((uint)rgb.R << 16)
           | ((uint)rgb.G << 8)
           | rgb.B;
}
