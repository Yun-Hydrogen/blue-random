using System;
using System.IO;
using System.Text.Json;
using BlueRandom.Services;
using BlueRandom.UI.CP;
using Xunit;

namespace BlueRandom.Tests;

public class WindowsSystemTests
{
    [Fact]
    public void TestUiAccessDllDetection()
    {
        // 验证 uiaccess.dll 路径获取与存在性检测
        string path = WindowsSystemService.GetUiAccessDllPath();
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.True(File.Exists(path), $"uiaccess.dll 应被正确探测到，实际路径: {path}");
        Assert.True(WindowsSystemService.DoesUiAccessDllExist());
    }

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
        Assert.Equal("26.10.02", versionProp.GetString());

        Assert.True(root.TryGetProperty("codename", out var codenameProp));
        Assert.Equal("Shiroko", codenameProp.GetString());

        Assert.True(root.TryGetProperty("uiAccessDllExists", out var uiaProp));
        Assert.True(uiaProp.GetBoolean(), "uiAccessDllExists 字段必须存在且返回 true");

        Assert.True(root.TryGetProperty("isAdmin", out _));
        Assert.True(root.TryGetProperty("isUiAccess", out _));
        Assert.True(root.TryGetProperty("configPath", out _));
    }

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

