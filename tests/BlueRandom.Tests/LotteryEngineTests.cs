using System.Collections.Generic;
using System.Linq;
using BlueRandom.Core.Config;
using BlueRandom.Core.Lottery;
using Xunit;

namespace BlueRandom.Tests;

public class LotteryEngineTests
{
    [Fact]
    public void Pick_EmptyPool_ReturnsEmpty()
    {
        var result = LotteryEngine.Pick(new List<StudentItem>(), 5, true);
        Assert.Empty(result);
    }

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
