// ============================================================================
// 文件名称: UpdateServiceTests.cs
// 项目名称: BlueRandom.Tests
// 文件说明: 自动更新与版本比较服务 (UpdateService) 的单元测试
// 核心职责: 验证三段式/二段式版本号对比、GitHub Tag 代号解析与发布状态构造
// ============================================================================

using System;
using BlueRandom.Services;
using Xunit;

namespace BlueRandom.Tests;

/// <summary>
/// 客户端在线更新与语义化版本号解析单元测试集
/// </summary>
public class UpdateServiceTests
{
    /// <summary>
    /// 验证各类标准与非标准语义化版本号（如 26.10.01 与 26.09.01）的大小比较逻辑
    /// </summary>
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

    /// <summary>
    /// 验证从 Release Tag 字符串（如 Shiroko-v26.10.01）中正确提取语义版本号与发布代号
    /// </summary>
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

    /// <summary>
    /// 验证当远端版本高于本地版本时，正确构建状态为 "update" 的升级提示载荷
    /// </summary>
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

    /// <summary>
    /// 验证当本地版本与远端版本一致时，正确构建状态为 "ok" 的已是最新载荷
    /// </summary>
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

    /// <summary>
    /// 验证当本地版本超前于远端版本（本地开发/未发布预览构建）时，正确构建状态为 "warn" 的预览版本载荷
    /// </summary>
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
