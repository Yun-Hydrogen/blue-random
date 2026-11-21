// ============================================================================
// 文件名称: LastResultPresenterTests.cs
// 项目名称: BlueRandom.Tests
// 文件说明: 结果暂留浮窗（LRW）数据呈现与方块磁贴样式的单元测试
// ============================================================================

using System;
using System.Collections.Generic;
using System.Windows.Media;
using BlueRandom.Modules.ConfigProvider;
using BlueRandom.Modules.WindowsManager;
using Xunit;

namespace BlueRandom.Tests;

public class LastResultPresenterTests
{
    [Fact]
    public void BuildRows_SingleStudent_GeneratesFullWidthBlock()
    {
        var students = new List<CalledStudent>
        {
            new() { Name = "砂狼白子", LetterColor = "rainbow" }
        };

        var rows = LastResultPresenter.BuildRows(students, 1.0);

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal("砂狼白子", row.Name);
        Assert.Equal(286, row.BlockWidth);
        Assert.Equal(46, row.BlockHeight);
        Assert.Equal(18, row.FontSize);
    }

    [Fact]
    public void BuildRows_MultipleStudents_ScalesBlockWidthByNameLength()
    {
        var students = new List<CalledStudent>
        {
            new() { Name = "白子", LetterColor = "rainbow" },
            new() { Name = "阿罗娜", LetterColor = "gold" },
            new() { Name = "早濑优香", LetterColor = "blue" },
            new() { Name = "浅黄无月酱", LetterColor = "gold" }
        };

        var rows = LastResultPresenter.BuildRows(students, 0.8);

        Assert.Equal(4, rows.Count);
        Assert.Equal(96, rows[0].BlockWidth);
        Assert.Equal(110, rows[1].BlockWidth);
        Assert.Equal(126, rows[2].BlockWidth);
        Assert.Equal(140, rows[3].BlockWidth);

        // 验证卡片长度随名字递增
        Assert.True(rows[0].BlockWidth < rows[1].BlockWidth);
        Assert.True(rows[1].BlockWidth < rows[2].BlockWidth);
        Assert.True(rows[2].BlockWidth < rows[3].BlockWidth);

        foreach (var row in rows)
        {
            Assert.Equal(40, row.BlockHeight);
            Assert.Equal(15.5, row.FontSize);
            Assert.InRange(row.BlockWidth, 96, 140);
        }
    }

    [Theory]
    [InlineData("白子", 96)]
    [InlineData("阿罗娜", 110)]
    [InlineData("早濑优香", 126)]
    [InlineData("浅黄无月酱", 140)]
    [InlineData("超级无敌大聪明名字", 140)]
    public void ComputeBlockWidth_MapsNameLengthToBoundedWidth(string name, double expectedWidth)
    {
        double w = LastResultPresenter.ComputeBlockWidth(name);
        Assert.Equal(expectedWidth, w);
    }

    [Theory]
    [InlineData("rainbow", 0xF6, 0xEE, 0xFF, 0xDF, 0xCA, 0xFF)]
    [InlineData("gold", 0xFF, 0xF9, 0xE6, 0xFF, 0xE1, 0x94)]
    [InlineData("blue", 0xED, 0xF8, 0xFF, 0xBC, 0xE4, 0xFF)]
    public void PaletteFor_GeneratesCorrectLetterColorsAndOpacity(
        string letterColor, byte expBgR, byte expBgG, byte expBgB, byte expBdR, byte expBdG, byte expBdB)
    {
        double opacity = 0.75;
        byte expectedAlpha = (byte)Math.Round(opacity * 255);

        var (bg, border) = LastResultPresenter.PaletteFor(letterColor, opacity);

        var solidBg = Assert.IsType<SolidColorBrush>(bg);
        var solidBd = Assert.IsType<SolidColorBrush>(border);

        Assert.Equal(expectedAlpha, solidBg.Color.A);
        Assert.Equal(expBgR, solidBg.Color.R);
        Assert.Equal(expBgG, solidBg.Color.G);
        Assert.Equal(expBgB, solidBg.Color.B);

        Assert.Equal(expectedAlpha, solidBd.Color.A);
        Assert.Equal(expBdR, solidBd.Color.R);
        Assert.Equal(expBdG, solidBd.Color.G);
        Assert.Equal(expBdB, solidBd.Color.B);
    }
}
