using System;
using System.IO;
using BlueRandom.Core.Config;
using BlueRandom.Services;
using Xunit;

namespace BlueRandom.Tests;

public class YamlConfigServiceTests
{
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

    [Fact]
    public void GetUserDataDir_ReturnsDotBlueRandomInUserProfile()
    {
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".blue random");
        string actual = YamlConfigService.GetUserDataDir();

        Assert.Equal(expected, actual);
        Assert.True(Directory.Exists(actual));
    }

    [Fact]
    public void AppVersion_LoadsVersionAndCodenameFromPackageJson()
    {
        Assert.Equal("26.10.02", BlueRandom.Core.AppVersion.Version);
        Assert.False(string.IsNullOrWhiteSpace(BlueRandom.Core.AppVersion.Codename));
    }

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

    [Fact]
    public void LegacyConfig_MigratesOnProtocolMismatch()
    {
        string legacyYaml = @"
student_list:
  - name: '阿罗娜'
    weight: 1.5
    letter_color: 'rainbow'
allow_repeat_draw: false
floating_button:
  size_percent: 120
  transparency_percent: 30
  custom_icon_id: 'custom_001'
pick_result_dialog:
  gachaSoundVolume: 0.75
";
        string? peeked = YamlConfigService.PeekProtocolVersion(legacyYaml);
        Assert.Null(peeked);
        Assert.NotEqual(BlueRandom.Core.AppVersion.Codename, peeked);

        var migrated = YamlConfigService.MigrateLegacyConfig(legacyYaml);
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
        Assert.True(migrated.PickResultDialog.ShowDeco);
        Assert.Equal(BlueRandom.Core.AppVersion.Codename, migrated.ProtocolVersion);
    }
}
