// ============================================================================
// 文件名称: WindowsSystemTests.cs
// 项目名称: BlueRandom.Tests
// 文件说明: 底层系统探测与 NativeBridge 原生桥接能力单元测试
// 核心职责: 验证 uiaccess.dll 动态链接库定位、运行时应用信息与日志 JSON 格式序列化
// ============================================================================

using System;
using System.IO;
using System.Text.Json;
using BlueRandom.Services;
using BlueRandom.UI.CP;
using Xunit;

namespace BlueRandom.Tests;

/// <summary>
/// Windows 原生系统服务与桥接接口单元测试集
/// </summary>
public class WindowsSystemTests
{
    /// <summary>
    /// 验证 uiaccess.dll 路径获取与物理存在性检测，确保提权与反黑屏能力正常就绪
    /// </summary>
    [Fact]
    public void TestUiAccessDllDetection()
    {
        // 验证 uiaccess.dll 路径获取与存在性检测
        string path = WindowsSystemService.GetUiAccessDllPath();
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.True(File.Exists(path), $"uiaccess.dll 应被正确探测到，实际路径: {path}");
        Assert.True(WindowsSystemService.DoesUiAccessDllExist());
    }

    /// <summary>
    /// 验证 NativeBridge 向上层前端导出的应用宿主环境元数据（版本、代号、UiAccess 状态）
    /// </summary>
    [Fact]
    public void TestNativeBridgeAppInfoJson()
    {
        // 构造 NativeBridge (传入 null hostWindow 仅测试 GetAppInfoJson)
        var bridge = new NativeBridge(null!);
        string json = bridge.GetAppInfoJson();
        Assert.False(string.IsNullOrWhiteSpace(json));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("version", out var versionProp));
        Assert.Equal(BlueRandom.Core.AppVersion.Version, versionProp.GetString());

        Assert.True(root.TryGetProperty("codename", out var codenameProp));
        Assert.Equal(BlueRandom.Core.AppVersion.Codename, codenameProp.GetString());

        Assert.True(root.TryGetProperty("uiAccessDllExists", out var uiaProp));
        Assert.True(uiaProp.GetBoolean(), "uiAccessDllExists 字段必须存在且返回 true");

        Assert.True(root.TryGetProperty("isAdmin", out _));
        Assert.True(root.TryGetProperty("isUiAccess", out _));
        Assert.True(root.TryGetProperty("configPath", out _));
    }

    /// <summary>
    /// 验证日志历史读取与 JSON 数组序列化结构规范
    /// </summary>
    [Fact]
    public void TestNativeBridgeLogsJsonOrdering()
    {
        var bridge = new NativeBridge(null!);
        string json = bridge.GetLogsJson(10);
        Assert.NotNull(json);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
    }
}

