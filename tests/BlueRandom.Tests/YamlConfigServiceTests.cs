// ============================================================================
// 文件名称: YamlConfigServiceTests.cs
// 项目名称: BlueRandom.Tests
// 文件说明: YAML 配置服务 (YamlConfigService) 与迁移引擎的单元测试
// 核心职责: 验证边界钳位归一化、带注释 YAML 生成、用户目录探测、协议嗅探与旧版配置平滑迁移
// ============================================================================

using System;
using System.IO;
using BlueRandom.Core.Config;
using BlueRandom.Services;
using Xunit;

namespace BlueRandom.Tests;

/// <summary>
/// YAML 配置持久化与平滑迁移单元测试集
/// </summary>
public class YamlConfigServiceTests
{
    /// <summary>
    /// 验证数值越界边界归一化保护（如缩放比、透明度、默认点名人数边界钳位）
    /// </summary>
    [Fact]
    public void Normalize_ClampsOutOfBoundsValues()
    {
        var raw = new RootConfig
        {
            FloatingButton = new FloatingButtonConfig
            {
                SizePercent = 500, // 超过 200
                TransparencyPercent = -50 // 低于 0
            },
            PickCountDialog = new PickCountDialogConfig
            {
                DefaultCount = 99 // 超过 10
            }
        };

        var normalized = YamlConfigService.Normalize(raw);

        Assert.Equal(200, normalized.FloatingButton.SizePercent);
        Assert.Equal(0, normalized.FloatingButton.TransparencyPercent);
        Assert.Equal(10, normalized.PickCountDialog.DefaultCount);
    }

    /// <summary>
    /// 验证生成的 YAML 包含标准的中文业务提示注释与核心字段
    /// </summary>
    [Fact]
    public void ToYamlWithComments_ContainsExpectedFieldsAndComments()
    {
        var cfg = new RootConfig
        {
            StudentList = new()
            {
                new StudentItem { Name = "测试学员1", Weight = 2.5, LetterColor = "rainbow" }
            }
        };

        string yaml = YamlConfigService.ToYamlWithComments(cfg);

        Assert.Contains("蔚蓝点名 (Blue Random) 配置文件", yaml);
        Assert.Contains("测试学员1", yaml);
        Assert.Contains("weight: 2.5", yaml);
        Assert.Contains("rainbow", yaml);
        Assert.Contains("floatingButton:", yaml);
        Assert.Contains("admin:", yaml);
    }

    /// <summary>
    /// 验证数据目录解析定位在当前用户个人主文件夹下的 `.blue random` 目录
    /// </summary>
    [Fact]
    public void GetUserDataDir_ReturnsDotBlueRandomInUserProfile()
    {
        YamlConfigService.OverrideUserDataDir = null;
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".blue random");
        string actual = YamlConfigService.GetUserDataDir();

        Assert.Equal(expected, actual);
        Assert.True(Directory.Exists(actual));
    }

    /// <summary>
    /// 验证 AppVersion 能够正确自内嵌 package.json 解析版本号与版本代号
    /// </summary>
    [Fact]
    public void AppVersion_LoadsVersionAndCodenameFromPackageJson()
    {
        Assert.False(string.IsNullOrWhiteSpace(BlueRandom.Core.AppVersion.Version));
        Assert.False(string.IsNullOrWhiteSpace(BlueRandom.Core.AppVersion.Codename));
    }

    /// <summary>
    /// 验证协议版本嗅探器能够在未完整反序列化 YAML 的情况下快速提取 protocolVersion
    /// </summary>
    [Fact]
    public void PeekProtocolVersion_ExtractsCorrectly()
    {
        string yaml1 = "protocolVersion: 'Schale'\nallowRepeatDraw: true";
        string yaml2 = "protocolVersion: \"Millennium\"\nallowRepeatDraw: false";
        string yaml3 = "allowRepeatDraw: true\nstudentList: []";

        Assert.Equal("Schale", YamlConfigService.PeekProtocolVersion(yaml1));
        Assert.Equal("Millennium", YamlConfigService.PeekProtocolVersion(yaml2));
        Assert.Null(YamlConfigService.PeekProtocolVersion(yaml3));
    }

    /// <summary>
    /// 验证导出带有协议版本字段的注释 YAML
    /// </summary>
    [Fact]
    public void ToYamlWithComments_IncludesProtocolVersion()
    {
        var cfg = new RootConfig
        {
            ProtocolVersion = "Schale"
        };
        string yaml = YamlConfigService.ToYamlWithComments(cfg);
        Assert.Contains("protocolVersion: 'Schale'", yaml);
    }

    /// <summary>
    /// 验证跨 C# 版本升级时协议代号变更（如从历史版本升级至当前版本），配置能平滑反序列化、补齐默认字段并更新代号
    /// </summary>
    [Fact]
    public void VersionUpgrade_MigratesOnProtocolMismatch()
    {
        string previousYaml = @"
protocolVersion: 'Shiroko'
allowRepeatDraw: false
studentList:
  - name: '阿罗娜'
    weight: 1.5
    letterColor: 'rainbow'
floatingButton:
  sizePercent: 120
  transparencyPercent: 30
  customIconId: 'custom_001'
pickResultDialog:
  soundVolume: 75
";
        string? peeked = YamlConfigService.PeekProtocolVersion(previousYaml);
        Assert.Equal("Shiroko", peeked);

        var migrated = YamlConfigService.MigrateLegacyConfig(previousYaml);
        migrated.ProtocolVersion = BlueRandom.Core.AppVersion.Codename;

        Assert.Single(migrated.StudentList);
        Assert.Equal("阿罗娜", migrated.StudentList[0].Name);
        Assert.Equal(1.5, migrated.StudentList[0].Weight);
        Assert.Equal("rainbow", migrated.StudentList[0].LetterColor);
        Assert.False(migrated.AllowRepeatDraw);
        Assert.Equal(120, migrated.FloatingButton.SizePercent);
        Assert.Equal(30, migrated.FloatingButton.TransparencyPercent);
        Assert.Equal("custom_001", migrated.FloatingButton.CustomIconId);
        Assert.Equal(75, migrated.PickResultDialog.SoundVolume);

        Assert.Equal(1, migrated.PickCountDialog.DefaultCount);
        Assert.Equal(BlueRandom.Core.AppVersion.Codename, migrated.ProtocolVersion);
    }

    /// <summary>
    /// 验证"结果暂留浮窗"开关默认关闭，且能以 showLastResult 为键完成 YAML 双向往返。
    /// </summary>
    [Fact]
    public void ShowLastResult_DefaultsFalseAndRoundTrips()
    {
        var defaults = new RootConfig().PickResultDialog;
        Assert.False(defaults.ShowLastResult);
        Assert.Equal(0.9, defaults.LastResultOpacity);
        Assert.Equal(0, defaults.LastResultAutoCloseSeconds);
        Assert.Equal("#ffffff", defaults.LastResultBgColor);
        Assert.Equal("#66ccff", defaults.LastResultBorderColor);

        var cfg = new RootConfig();
        cfg.PickResultDialog.ShowLastResult = true;
        cfg.PickResultDialog.LastResultBgColor = "#112233";
        cfg.PickResultDialog.LastResultBorderColor = "#445566";
        cfg.PickResultDialog.LastResultOpacity = 0.55;
        cfg.PickResultDialog.LastResultAutoCloseSeconds = 30;

        string yaml = YamlConfigService.ToYamlWithComments(cfg);
        Assert.Contains("showLastResult: true", yaml);
        Assert.Contains("lastResultBgColor: '#112233'", yaml);
        Assert.Contains("lastResultBorderColor: '#445566'", yaml);
        Assert.Contains("lastResultOpacity: 0.55", yaml);
        Assert.Contains("lastResultAutoCloseSeconds: 30", yaml);

        var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var parsed = deserializer.Deserialize<RootConfig>(yaml);
        Assert.True(parsed.PickResultDialog.ShowLastResult);
        Assert.Equal("#112233", parsed.PickResultDialog.LastResultBgColor);
        Assert.Equal("#445566", parsed.PickResultDialog.LastResultBorderColor);
        Assert.Equal(0.55, parsed.PickResultDialog.LastResultOpacity);
        Assert.Equal(30, parsed.PickResultDialog.LastResultAutoCloseSeconds);
    }

    /// <summary>
    /// 验证结果暂留浮窗的透明度与自动关闭时长会被钳制到安全区间。
    /// </summary>
    [Fact]
    public void Normalize_ClampsLastResultOptions()
    {
        var lower = new RootConfig();
        lower.PickResultDialog.LastResultOpacity = 0.01;
        lower.PickResultDialog.LastResultAutoCloseSeconds = -5;

        var normalizedLower = YamlConfigService.Normalize(lower);
        Assert.Equal(0.1, normalizedLower.PickResultDialog.LastResultOpacity);
        Assert.Equal(0, normalizedLower.PickResultDialog.LastResultAutoCloseSeconds);

        var upper = new RootConfig();
        upper.PickResultDialog.LastResultOpacity = 9.0;
        upper.PickResultDialog.LastResultAutoCloseSeconds = 99999;

        var normalizedUpper = YamlConfigService.Normalize(upper);
        Assert.Equal(1.0, normalizedUpper.PickResultDialog.LastResultOpacity);
        Assert.Equal(3600, normalizedUpper.PickResultDialog.LastResultAutoCloseSeconds);
    }
}
