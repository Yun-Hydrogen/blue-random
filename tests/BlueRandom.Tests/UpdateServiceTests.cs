using System;
using System.Text.Json;
using BlueRandom.Core;
using BlueRandom.Services;
using Xunit;

namespace BlueRandom.Tests;

public class UpdateServiceTests
{
    [Theory]
    [InlineData("26.10.01", "26.09.01", 1)]
    [InlineData("26.09.01", "26.10.01", -1)]
    [InlineData("26.10.01", "26.10.01", 0)]
    [InlineData("1.0", "1.0.0", 0)]
    [InlineData("2.0.1", "2.0.0", 1)]
    [InlineData("2.0.0", "2.0.1", -1)]
    [InlineData("10.0.0", "9.9.9", 1)]
    public void TestCompareVersions(string v1, string v2, int expected)
    {
        int result = UpdateService.CompareVersions(v1, v2);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Shiroko-v26.10.01", "26.10.01", "Shiroko")]
    [InlineData("Kuroko-v26.11.02", "26.11.02", "Kuroko")]
    [InlineData("v26.09.01", "26.09.01", null)]
    [InlineData("26.08.01", "26.08.01", null)]
    [InlineData("v2.0.0", "2.0.0", null)]
    public void TestParseTag(string tag, string expectedVersion, string? expectedCodename)
    {
        var (version, codename) = UpdateService.ParseTag(tag);
        Assert.Equal(expectedVersion, version);
        Assert.Equal(expectedCodename, codename);
    }

    [Fact]
    public void TestBuildResultWhenUpdateAvailable()
    {
        var res = UpdateService.BuildResultFromTag(
            remoteTag: "Shiroko-v26.11.01",
            releaseUrl: "https://github.com/Yun-Hydrogen/blue-random/releases/tag/Shiroko-v26.11.01",
            body: "Feature: New Gacha Animation",
            localVersion: "26.10.01",
            localCodename: "Shiroko"
        );

        Assert.True(res.Ok);
        Assert.Equal("update", res.Status);
        Assert.Contains("Shiroko-v26.11.01", res.Title);
        Assert.Contains("Feature: New Gacha Animation", res.Detail);
        Assert.Equal("26.11.01", res.RemoteVersion);
    }

    [Fact]
    public void TestBuildResultWhenUpToDate()
    {
        var res = UpdateService.BuildResultFromTag(
            remoteTag: "Shiroko-v26.10.01",
            releaseUrl: "https://github.com/Yun-Hydrogen/blue-random/releases/tag/Shiroko-v26.10.01",
            body: "Current release",
            localVersion: "26.10.01",
            localCodename: "Shiroko"
        );

        Assert.True(res.Ok);
        Assert.Equal("ok", res.Status);
        Assert.Contains("最新版本", res.Title);
    }

    [Fact]
    public void TestBuildResultWhenLocalIsAhead()
    {
        var res = UpdateService.BuildResultFromTag(
            remoteTag: "v26.09.01",
            releaseUrl: "https://github.com/Yun-Hydrogen/blue-random/releases/tag/v26.09.01",
            body: "Past release",
            localVersion: "26.10.01",
            localCodename: "Shiroko"
        );

        Assert.True(res.Ok);
        Assert.Equal("warn", res.Status);
        Assert.Contains("开发预览版本", res.Title);
    }
}
