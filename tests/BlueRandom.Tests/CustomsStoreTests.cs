// ============================================================================
// 文件名称: CustomsStoreTests.cs
// 项目名称: BlueRandom.Tests
// 文件说明: 自定义素材仓储服务 (CustomsStore) 的单元测试
// 核心职责: 验证 Base64 清洗、素材读写、启动时相同用途资源单例化去重与最新绑定
// ============================================================================

using System;
using System.IO;
using System.Threading;
using BlueRandom.Core.Config;
using BlueRandom.Services;
using Xunit;

namespace BlueRandom.Tests;

/// <summary>
/// 自定义多媒体素材仓储与去重绑定单例化测试集（沙箱隔离）
/// </summary>
public class CustomsStoreTests : IDisposable
{
    private readonly string _sandboxDir;

    public CustomsStoreTests()
    {
        _sandboxDir = Path.Combine(Path.GetTempPath(), "BlueRandom_CustomsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandboxDir);
        YamlConfigService.OverrideUserDataDir = _sandboxDir;
    }

    public void Dispose()
    {
        YamlConfigService.OverrideUserDataDir = null;
        try
        {
            if (Directory.Exists(_sandboxDir))
            {
                Directory.Delete(_sandboxDir, true);
            }
        }
        catch
        {
            // 忽略临时文件释放延迟
        }
    }

    /// <summary>
    /// 验证 CleanBase64 能够正确剥离各种 DataURL 前缀，保留纯 Base64 数据串
    /// </summary>
    [Fact]
    public void CleanBase64_StripsDataUrlHeader()
    {
        string rawWithHeader = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAA";
        string cleaned = CustomsStore.CleanBase64(rawWithHeader);
        Assert.Equal("iVBORw0KGgoAAAANSUhEUgAA", cleaned);

        string pure = "aGVsbG8gd29ybGQ=";
        Assert.Equal("aGVsbG8gd29ybGQ=", CustomsStore.CleanBase64(pure));
    }

    /// <summary>
    /// 验证 NormalizePurpose 能够将前端各组件传来的用途字符串统一归一化
    /// </summary>
    [Theory]
    [InlineData("floating-icon", "icon")]
    [InlineData("icon", "icon")]
    [InlineData("Floating_Icon", "icon")]
    [InlineData("gacha-sound", "gachasound")]
    [InlineData("gachaSound", "gachasound")]
    [InlineData("sound", "gachasound")]
    [InlineData("bgm", "bgm")]
    [InlineData("music", "bgm")]
    [InlineData("BGM", "bgm")]
    public void NormalizePurpose_NormalizesVariants(string input, string expected)
    {
        Assert.Equal(expected, CustomsStore.NormalizePurpose(input));
    }

    /// <summary>
    /// 验证 SaveCustom 与 LoadCustom 能够正常进行磁盘序列化与反序列化
    /// </summary>
    [Fact]
    public void SaveCustom_And_LoadCustom_RoundTrip()
    {
        var item = CustomsStore.SaveCustom(
            fileName: "test_icon.png",
            mimeType: "image/png",
            purpose: "icon",
            base64: "data:image/png;base64,iVBORw0KGgoAAAANSUhEUg=="
        );

        Assert.NotNull(item);
        Assert.False(string.IsNullOrEmpty(item.Id));

        var loaded = CustomsStore.LoadCustom(item.Id);
        Assert.NotNull(loaded);
        Assert.Equal("test_icon.png", loaded.FileName);
        Assert.Equal("image/png", loaded.MimeType);
        Assert.Equal("icon", loaded.Purpose);
        Assert.Equal("iVBORw0KGgoAAAANSUhEUg==", loaded.Base64);
    }

    /// <summary>
    /// 验证启动时自动检查相同用途（purpose）的资源：
    /// 存在多个相同用途时只保留最新的一个并删除旧文件，且自动将最新资源绑定到配置中
    /// </summary>
    [Fact]
    public void ConsolidateAndBindLatestResources_DeduplicatesAndBindsLatest()
    {
        // 1. 创建 3 个相同用途为 "icon" 的资源（含前端 floating-icon 与 icon 变体，模拟用户多次上传新图标）
        var icon1 = CustomsStore.SaveCustom("old_icon1.png", "image/png", "floating-icon", "base64_1");
        Assert.NotNull(icon1);
        Thread.Sleep(20); // 确保时间戳与 LastWriteTime 严格递增

        var icon2 = CustomsStore.SaveCustom("old_icon2.png", "image/png", "icon", "base64_2");
        Assert.NotNull(icon2);
        Thread.Sleep(20);

        var icon3 = CustomsStore.SaveCustom("latest_icon3.png", "image/png", "floating-icon", "base64_3");
        Assert.NotNull(icon3);

        // 2. 创建 2 个相同用途为 "bgm" 的资源
        var bgm1 = CustomsStore.SaveCustom("old_bgm.mp3", "audio/mpeg", "bgm", "base64_bgm1", 120.0);
        Assert.NotNull(bgm1);
        Thread.Sleep(20);

        var bgm2 = CustomsStore.SaveCustom("latest_bgm.mp3", "audio/mpeg", "bgm", "base64_bgm2", 180.0);
        Assert.NotNull(bgm2);

        // 3. 创建 1 个用途为 "gacha-sound" 的资源（前端 TabResult 传入格式）
        var sound1 = CustomsStore.SaveCustom("gacha.wav", "audio/wav", "gacha-sound", "base64_sound1", 3.0);
        Assert.NotNull(sound1);

        // 初始配置为空绑定
        var config = new RootConfig
        {
            FloatingButton = new FloatingButtonConfig { CustomIconId = "" },
            PickResultDialog = new PickResultDialogConfig
            {
                BgmCustomId = "",
                GachaSoundCustomId = ""
            }
        };

        // 4. 执行单例化去重与自动绑定
        bool changed = CustomsStore.ConsolidateAndBindLatestResources(config);
        Assert.True(changed);

        // 5. 校验最新资源已被绑定到主配置
        Assert.Equal(icon3.Id, config.FloatingButton.CustomIconId);
        Assert.Equal(bgm2.Id, config.PickResultDialog.BgmCustomId);
        Assert.Equal(sound1.Id, config.PickResultDialog.GachaSoundCustomId);

        // 6. 校验物理磁盘上的旧资源已被删除，最新资源保留
        string customsDir = CustomsStore.GetCustomsDir();
        Assert.False(File.Exists(Path.Combine(customsDir, $"{icon1.Id}.yaml")), "旧图标 icon1 物理文件应当被清理");
        Assert.False(File.Exists(Path.Combine(customsDir, $"{icon2.Id}.yaml")), "旧图标 icon2 物理文件应当被清理");
        Assert.True(File.Exists(Path.Combine(customsDir, $"{icon3.Id}.yaml")), "最新图标 icon3 必须保留");

        Assert.False(File.Exists(Path.Combine(customsDir, $"{bgm1.Id}.yaml")), "旧 BGM bgm1 物理文件应当被清理");
        Assert.True(File.Exists(Path.Combine(customsDir, $"{bgm2.Id}.yaml")), "最新 BGM bgm2 必须保留");

        Assert.True(File.Exists(Path.Combine(customsDir, $"{sound1.Id}.yaml")), "单例抽卡音效 sound1 必须保留");
    }

    /// <summary>
    /// 验证若主配置中引用的自定义资源 ID 对应文件在磁盘上已不存在，自动清理为空并回退
    /// </summary>
    [Fact]
    public void ConsolidateAndBindLatestResources_CleansUpDeletedCustomIds()
    {
        var config = new RootConfig
        {
            FloatingButton = new FloatingButtonConfig { CustomIconId = "custom_ghost_icon" },
            PickResultDialog = new PickResultDialogConfig
            {
                BgmCustomId = "custom_ghost_bgm",
                GachaSoundCustomId = "custom_ghost_sound"
            }
        };

        // 确保 customs 目录存在
        CustomsStore.GetCustomsDir();

        // 没有任何资源文件存在时，应自动清空无效引用
        bool changed = CustomsStore.ConsolidateAndBindLatestResources(config);
        Assert.True(changed);

        Assert.Equal(string.Empty, config.FloatingButton.CustomIconId);
        Assert.Equal(string.Empty, config.PickResultDialog.BgmCustomId);
        Assert.Equal(string.Empty, config.PickResultDialog.GachaSoundCustomId);
    }
}
