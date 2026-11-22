// ============================================================================
// 文件名称: UpdateServiceTests.cs
// 项目名称: BlueRandom.Tests
// 文件说明: 自动更新与版本比较服务 (UpdateService) 的单元测试
// 核心职责: 验证三段式/二段式版本号对比、GitHub Tag 代号解析与发布状态构造
// ============================================================================

using System;
using System.IO;
using BlueRandom.Modules.ApiBridge;
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
    public void CompareVersions_ReturnsExpectedOrder(string v1, string v2, int expected)
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
    public void ParseTag_SplitsVersionAndCodename(string tag, string expectedVersion, string? expectedCodename)
    {
        var (version, codename) = UpdateService.ParseTag(tag);
        Assert.Equal(expectedVersion, version);
        Assert.Equal(expectedCodename, codename);
    }

    /// <summary>
    /// 验证当远端版本高于本地版本时，正确构建状态为 "update" 的升级提示载荷
    /// </summary>
    [Fact]
    public void BuildResult_UpdateAvailable_SetsFlags()
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
    public void BuildResult_UpToDate_SetsFlags()
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
    public void BuildResult_LocalAhead_SetsFlags()
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

    /// <summary>
    /// 验证 GitHub Release API JSON 解析能正确提取 BlueRandom.exe 与 changelog.md 资产信息
    /// </summary>
    [Fact]
    public void ParseReleaseApiJson_ExtractsChangelogAndBlueRandomExeAsset()
    {
        string json = """
        {
          "tag_name": "Momoi-v26.11.05",
          "html_url": "https://github.com/Yun-Hydrogen/blue-random/releases/tag/Momoi-v26.11.05",
          "body": "Release body",
          "assets": [
            {
              "name": "changelog.md",
              "size": 512,
              "browser_download_url": "https://github.com/Yun-Hydrogen/blue-random/releases/download/Momoi-v26.11.05/changelog.md"
            },
            {
              "name": "BlueRandom.exe",
              "size": 67108864,
              "browser_download_url": "https://github.com/Yun-Hydrogen/blue-random/releases/download/Momoi-v26.11.05/BlueRandom.exe"
            }
          ]
        }
        """;

        var parsed = UpdateService.ParseReleaseApiJson(json);
        Assert.NotNull(parsed);
        Assert.Equal("Momoi-v26.11.05", parsed!.TagName);
        Assert.Equal("https://github.com/Yun-Hydrogen/blue-random/releases/download/Momoi-v26.11.05/changelog.md", parsed.ChangelogAssetUrl);
        Assert.Equal("https://github.com/Yun-Hydrogen/blue-random/releases/download/Momoi-v26.11.05/BlueRandom.exe", parsed.DownloadUrl);
        Assert.Equal("BlueRandom.exe", parsed.AssetName);
        Assert.Equal(67108864, parsed.AssetSize);
    }

    /// <summary>
    /// 验证兼容旧版带版本号命名的 exe 资产回退提取
    /// </summary>
    [Fact]
    public void ParseReleaseApiJson_FallsBackToVersionedExeAsset()
    {
        string json = """
        {
          "tag_name": "Momoi-v26.11.04",
          "html_url": "https://github.com/Yun-Hydrogen/blue-random/releases/tag/Momoi-v26.11.04",
          "body": "",
          "assets": [
            {
              "name": "BlueRandom-Momoi-v26.11.04-win-x64.exe",
              "size": 123456,
              "browser_download_url": "https://example.com/BlueRandom-Momoi-v26.11.04-win-x64.exe"
            }
          ]
        }
        """;

        var parsed = UpdateService.ParseReleaseApiJson(json);
        Assert.NotNull(parsed);
        Assert.Equal("BlueRandom-Momoi-v26.11.04-win-x64.exe", parsed!.AssetName);
        Assert.Equal("https://example.com/BlueRandom-Momoi-v26.11.04-win-x64.exe", parsed.DownloadUrl);
    }

    /// <summary>
    /// 验证发现新版本时正确填充下载直链与资产元数据
    /// </summary>
    [Fact]
    public void BuildResult_WithDownloadAsset_PopulatesDownloadFields()
    {
        var res = UpdateService.BuildResultFromTag(
            remoteTag: "Momoi-v26.11.05",
            releaseUrl: "https://github.com/Yun-Hydrogen/blue-random/releases/tag/Momoi-v26.11.05",
            body: "## 更新日志\n- 新增自动更新",
            localVersion: "26.11.04",
            localCodename: "Momoi",
            downloadUrl: "https://github.com/Yun-Hydrogen/blue-random/releases/download/Momoi-v26.11.05/BlueRandom.exe",
            assetName: "BlueRandom.exe",
            assetSize: 1024000);

        Assert.Equal("update", res.Status);
        Assert.Equal("https://github.com/Yun-Hydrogen/blue-random/releases/download/Momoi-v26.11.05/BlueRandom.exe", res.DownloadUrl);
        Assert.Equal("BlueRandom.exe", res.AssetName);
        Assert.Equal(1024000, res.AssetSize);
        Assert.Equal("## 更新日志\n- 新增自动更新", res.Detail);
    }

    /// <summary>
    /// 验证 PE 可执行文件头与最小体积校验逻辑
    /// </summary>
    [Fact]
    public void IsValidExecutableFile_ValidatesPeHeaderAndSize()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "BlueRandom_UpdateTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string validExe = Path.Combine(tempDir, "valid.exe");
            byte[] peBytes = new byte[2048];
            peBytes[0] = (byte)'M';
            peBytes[1] = (byte)'Z';
            File.WriteAllBytes(validExe, peBytes);
            Assert.True(UpdateService.IsValidExecutableFile(validExe));

            string htmlFile = Path.Combine(tempDir, "error.exe");
            File.WriteAllText(htmlFile, new string('A', 2048));
            Assert.False(UpdateService.IsValidExecutableFile(htmlFile));

            string tinyFile = Path.Combine(tempDir, "tiny.exe");
            File.WriteAllBytes(tinyFile, new byte[] { (byte)'M', (byte)'Z', 0, 0 });
            Assert.False(UpdateService.IsValidExecutableFile(tinyFile));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    /// <summary>
    /// 验证原地自替换更新对 dotnet.exe 调试宿主与缺失路径的拦截
    /// </summary>
    [Fact]
    public void CanSelfUpdate_RejectsDotnetHostAndMissingPath()
    {
        Assert.False(UpdateService.CanSelfUpdate(null, out _));
        Assert.False(UpdateService.CanSelfUpdate(@"C:\nonexistent\BlueRandom.exe", out _));

        string tempDir = Path.Combine(Path.GetTempPath(), "BlueRandom_SelfUpdateTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string dotnetExe = Path.Combine(tempDir, "dotnet.exe");
            File.WriteAllBytes(dotnetExe, new byte[] { 1, 2 });
            Assert.False(UpdateService.CanSelfUpdate(dotnetExe, out _));

            string appExe = Path.Combine(tempDir, "BlueRandom.exe");
            File.WriteAllBytes(appExe, new byte[] { 1, 2 });
            Assert.True(UpdateService.CanSelfUpdate(appExe, out _));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
