// ============================================================================
// 文件名称: LotteryEngineTests.cs
// 项目名称: BlueRandom.Tests
// 文件说明: 抽卡核心引擎 (LotteryEngine) 轮盘赌加权随机抽样算法单元测试
// 核心职责: 验证空池防御、放回抽样、不放回去重采样、超量保护与信封色彩映射
// ============================================================================

using System.Collections.Generic;
using System.Linq;
using BlueRandom.Core.Config;
using BlueRandom.Core.Lottery;
using Xunit;

namespace BlueRandom.Tests;

/// <summary>
/// 抽卡核心随机数与加权轮盘赌算法测试集
/// </summary>
public class LotteryEngineTests
{
    /// <summary>
    /// 验证当学生名单为空时抽取返回空列表，防止越界异常
    /// </summary>
    [Fact]
    public void Pick_EmptyPool_ReturnsEmpty()
    {
        var result = LotteryEngine.Pick(new List<StudentItem>(), 5, true);
        Assert.Empty(result);
    }

    /// <summary>
    /// 验证在允许重复模式下，抽取数量可以超出候选池大小并正确返回指定条数
    /// </summary>
    [Fact]
    public void Pick_AllowDuplicate_ReturnsRequestedCount()
    {
        var pool = new List<StudentItem>
        {
            new() { Name = "优香", Weight = 1.0, LetterColor = "rainbow" },
            new() { Name = "茜香", Weight = 1.0, LetterColor = "gold" }
        };

        var result = LotteryEngine.Pick(pool, 10, allowDuplicate: true);
        Assert.Equal(10, result.Count);
        Assert.All(result, s => Assert.True(s.Name == "优香" || s.Name == "茜香"));
    }

    /// <summary>
    /// 验证在不允许重复模式下，结果集合中绝不包含相同学员
    /// </summary>
    [Fact]
    public void Pick_WithoutDuplicate_NeverReturnsDuplicates()
    {
        var pool = new List<StudentItem>
        {
            new() { Name = "优香", Weight = 1.0, LetterColor = "rainbow" },
            new() { Name = "茜香", Weight = 1.0, LetterColor = "gold" },
            new() { Name = "白子", Weight = 1.0, LetterColor = "blue" },
            new() { Name = "日奈", Weight = 1.0, LetterColor = "rainbow" },
            new() { Name = "星野", Weight = 1.0, LetterColor = "rainbow" }
        };

        var result = LotteryEngine.Pick(pool, 5, allowDuplicate: false);
        Assert.Equal(5, result.Count);
        Assert.Equal(5, result.Select(s => s.Name).Distinct().Count());
    }

    /// <summary>
    /// 验证在不放回抽样时，当请求数量大于候选池容量时自动截断为候选池实际总数
    /// </summary>
    [Fact]
    public void Pick_WithoutDuplicate_CountExceedsPool_ReturnsAllStudents()
    {
        var pool = new List<StudentItem>
        {
            new() { Name = "优香", Weight = 1.0 },
            new() { Name = "茜香", Weight = 1.0 }
        };

        var result = LotteryEngine.Pick(pool, 10, allowDuplicate: false);
        Assert.Equal(2, result.Count);
    }

    /// <summary>
    /// 验证开箱信封品质颜色（rainbow / gold / blue）在抽取结果中的完整传递
    /// </summary>
    [Fact]
    public void Pick_MapsEnvelopeColorsCorrectly()
    {
        var pool = new List<StudentItem>
        {
            new() { Name = "三星", Weight = 1.0, LetterColor = "rainbow" },
            new() { Name = "二星", Weight = 1.0, LetterColor = "gold" },
            new() { Name = "一星", Weight = 1.0, LetterColor = "blue" }
        };

        var result = LotteryEngine.Pick(pool, 3, allowDuplicate: false);
        Assert.Equal(3, result.Count);

        var rainbow = result.First(s => s.Name == "三星");
        var gold = result.First(s => s.Name == "二星");
        var blue = result.First(s => s.Name == "一星");

        Assert.Equal("rainbow", rainbow.LetterColor);
        Assert.Equal("gold", gold.LetterColor);
        Assert.Equal("blue", blue.LetterColor);
    }
}
