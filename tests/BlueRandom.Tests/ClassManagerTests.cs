// ============================================================================
// 文件名称: ClassManagerTests.cs
// 项目名称: BlueRandom.Tests
// 文件说明: 多班级管理服务 (ClassManagerService) 与序列化模型的单元测试
// 核心职责: 验证班级创建、读取、重命名、删除 (CRUD) 与 CamelCase JSON 序列化规范
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BlueRandom.Core.Config;
using BlueRandom.Services;
using Xunit;

namespace BlueRandom.Tests;

/// <summary>
/// 班级配置管理器与花名册管理单元测试集（沙箱隔离，杜绝污染真实用户配置）
/// </summary>
public class ClassManagerTests : IDisposable
{
    private readonly string _sandboxDir;

    public ClassManagerTests()
    {
        _sandboxDir = Path.Combine(Path.GetTempPath(), "BlueRandom_ClassTests_" + Guid.NewGuid().ToString("N"));
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
    /// 验证班级实体与学员数据结构向前端序列化时遵循严格的小驼峰命名约定
    /// </summary>
    [Fact]
    public void ClassConfig_And_StudentItem_Serialize_To_CamelCase_Json()
    {
        var cls = new ClassConfig
        {
            ClassId = "classes_test_001",
            Name = "测试班级",
            StudentList = new List<StudentItem>
            {
                new() { Name = "优香", Weight = 1.5, LetterColor = "gold" }
            }
        };

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        string json = JsonSerializer.Serialize(cls, options);

        Assert.Contains("\"classId\":", json);
        Assert.Contains("\"classes_test_001\"", json);
        Assert.Contains("\"name\":", json);
        Assert.Contains("\"测试班级\"", json);
        Assert.Contains("\"studentList\":", json);
        Assert.Contains("\"weight\":", json);
        Assert.Contains("\"letterColor\":", json);
        Assert.DoesNotContain("\"ClassId\":", json);
        Assert.DoesNotContain("\"StudentList\":", json);
    }

    /// <summary>
    /// 验证班级增删改查全生命周期管理（创建 -> 读取 -> 重命名 -> 删除）的完整逻辑闭环
    /// </summary>
    [Fact]
    public void ClassManagerService_Crud_Operations()
    {
        string testName = "单元测试班级_" + Guid.NewGuid().ToString("N")[..6];
        var created = ClassManagerService.CreateClass(testName);

        Assert.NotNull(created);
        Assert.StartsWith("classes_", created.ClassId);
        Assert.Equal(testName, created.Name);

        // Load
        var loaded = ClassManagerService.LoadClass(created.ClassId);
        Assert.NotNull(loaded);
        Assert.Equal(testName, loaded.Name);

        // Rename
        string updatedName = testName + "_更新";
        bool renamed = ClassManagerService.RenameClass(created.ClassId, updatedName);
        Assert.True(renamed);

        var reloaded = ClassManagerService.LoadClass(created.ClassId);
        Assert.NotNull(reloaded);
        Assert.Equal(updatedName, reloaded.Name);

        // Delete
        bool deleted = ClassManagerService.DeleteClass(created.ClassId);
        Assert.True(deleted);

        var afterDelete = ClassManagerService.LoadClass(created.ClassId);
        Assert.Null(afterDelete);
    }

    /// <summary>
    /// 验证初次启动时默认班级自动补全、激活以及全局配置花名册映射的有效性
    /// </summary>
    [Fact]
    public void EnsureInitialized_Sets_ActiveClassId_And_Roster()
    {
        var config = new RootConfig
        {
            ActiveClassId = "",
            StudentList = new List<StudentItem>
            {
                new() { Name = "初始学员", Weight = 1.0, LetterColor = "blue" }
            }
        };

        ClassManagerService.EnsureInitialized(config);

        Assert.False(string.IsNullOrWhiteSpace(config.ActiveClassId));
        Assert.NotEmpty(config.StudentList);

        var activeClass = ClassManagerService.LoadClass(config.ActiveClassId);
        Assert.NotNull(activeClass);
    }
}

