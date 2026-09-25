using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BlueRandom.Core.Config;
using BlueRandom.Core.Scheduler;
using BlueRandom.Services;
using Xunit;

namespace BlueRandom.Tests;

public class ClassManagerTests
{
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

