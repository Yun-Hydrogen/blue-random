// ============================================================================
// 文件名称: LrwVisualThemeTests.cs
// 项目名称: BlueRandom.Tests
// 文件说明: 结果暂留浮窗（LRW）配色推导逻辑的单元测试
// 核心职责: 验证不透明度联动、层级色混合与非法颜色回退
// ============================================================================

using BlueRandom.UI.LRW;
using Xunit;

namespace BlueRandom.Tests;

/// <summary>结果暂留浮窗配色推导测试。</summary>
public class LrwVisualThemeTests
{
    /// <summary>透明卡片及其内部元素必须共用同一 Alpha，避免半透明卡片里残留实心白块。</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.9)]
    [InlineData(0.55)]
    [InlineData(0.1)]
    public void Compute_AppliesSameAlphaToCardAndChildren(double opacity)
    {
        var palette = LrwVisualTheme.Compute("#ffffff", "#66ccff", opacity);

        byte expected = (byte)System.Math.Round(opacity * 255);
        Assert.Equal(expected, LrwPalette.AlphaOf(palette.CardBackground));
        Assert.Equal(expected, LrwPalette.AlphaOf(palette.Accent));
        Assert.Equal(expected, LrwPalette.AlphaOf(palette.RowBackground));
        Assert.Equal(expected, LrwPalette.AlphaOf(palette.BadgeBackground));
        Assert.Equal(expected, LrwPalette.AlphaOf(palette.CloseHover));
        Assert.Equal(expected, LrwPalette.AlphaOf(palette.ProgressTrack));
    }

    /// <summary>不透明度超出配置允许范围时按 [0.1, 1.0] 夹取。</summary>
    [Theory]
    [InlineData(0.0, 0.1)]
    [InlineData(-1.0, 0.1)]
    [InlineData(2.0, 1.0)]
    public void Compute_ClampsOpacity(double input, double expected)
    {
        var palette = LrwVisualTheme.Compute(null, null, input);
        Assert.Equal((byte)System.Math.Round(expected * 255), LrwPalette.AlphaOf(palette.CardBackground));
    }

    /// <summary>不透明时卡片底色应等于配置的 RGB 值。</summary>
    [Fact]
    public void Compute_OpaqueBackgroundKeepsRgb()
    {
        var palette = LrwVisualTheme.Compute("#112233", "#66ccff", 1.0);
        Assert.Equal(0xFF112233u, palette.CardBackground);
        Assert.Equal(0xFF66CCFFu, palette.Accent);
    }

    /// <summary>层级色必须介于底色与强调色之间，且姓名片比徽标更接近底色。</summary>
    [Fact]
    public void Compute_BlendsChildrenTowardAccent()
    {
        var palette = LrwVisualTheme.Compute("#ffffff", "#000000", 1.0);

        byte row = (byte)(palette.RowBackground & 0xFF);
        byte badge = (byte)(palette.BadgeBackground & 0xFF);

        Assert.InRange(row, 1, 254);
        Assert.True(row > badge, "姓名片应比徽标更接近卡片底色");
    }

    /// <summary>颜色字符串非法时回退到默认白色与天蓝，不抛异常。</summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not-a-color")]
    [InlineData("#12")]
    public void Compute_FallsBackOnInvalidColors(string? value)
    {
        var palette = LrwVisualTheme.Compute(value, value, 1.0);
        Assert.Equal(0xFFFFFFFFu, palette.CardBackground);
        Assert.Equal(0xFF66CCFFu, palette.Accent);
    }

    /// <summary>带 Alpha 的 #AARRGGBB 只取 RGB 部分，最终透明度仍由配置决定。</summary>
    [Fact]
    public void Compute_IgnoresEmbeddedAlphaChannel()
    {
        var palette = LrwVisualTheme.Compute("#80ff0000", "#66ccff", 0.5);
        Assert.Equal(0x80FF0000u, palette.CardBackground);
    }

    /// <summary>无 # 前缀的颜色字符串同样可解析。</summary>
    [Fact]
    public void Compute_AcceptsColorWithoutHash()
    {
        var palette = LrwVisualTheme.Compute("ff8800", "00ff00", 1.0);
        Assert.Equal(0xFFFF8800u, palette.CardBackground);
        Assert.Equal(0xFF00FF00u, palette.Accent);
    }
}
