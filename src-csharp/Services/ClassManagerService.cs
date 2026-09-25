using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using BlueRandom.Core.Config;
using BlueRandom.Core.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BlueRandom.Services;

/// <summary>
/// 班级数据结构
/// </summary>
public class ClassConfig
{
    [JsonPropertyName("classId")]
    [YamlMember(Alias = "classId")]
    public string ClassId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "未命名班级";

    [JsonPropertyName("studentList")]
    [YamlMember(Alias = "studentList")]
    public List<StudentItem> StudentList { get; set; } = new();
}

/// <summary>
/// 多班级管理服务 (classes/ 目录)
/// </summary>
public static class ClassManagerService
{
    private static readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public static string GetClassesDir()
    {
        string dir = Path.Combine(YamlConfigService.GetUserDataDir(), "classes");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string GetClassFilePath(string classId)
    {
        string dir = GetClassesDir();
        string yaml = Path.Combine(dir, $"{classId}.yaml");
        string yml = Path.Combine(dir, $"{classId}.yml");
        bool hasYaml = File.Exists(yaml);
        bool hasYml = File.Exists(yml);

        if (hasYaml && hasYml)
        {
            return File.GetLastWriteTimeUtc(yaml) >= File.GetLastWriteTimeUtc(yml) ? yaml : yml;
        }
        if (hasYaml) return yaml;
        if (hasYml) return yml;
        return yaml;
    }

    /// <summary>
    /// 确保 classes 目录与默认班级正确初始化并与主配置关联
    /// </summary>
    public static void EnsureInitialized(RootConfig config)
    {
        try
        {
            string dir = GetClassesDir();
            var existingClasses = ListClasses();

            if (existingClasses.Count == 0)
            {
                // 无任何班级，根据当前配置创建默认班级
                var defaultStudents = (config.StudentList != null && config.StudentList.Count > 0)
                    ? new List<StudentItem>(config.StudentList)
                    : new List<StudentItem>
                    {
                        new() { Name = "早濑优香", Weight = 1.0, LetterColor = "rainbow" },
                        new() { Name = "生盐诺亚", Weight = 1.0, LetterColor = "gold" },
                        new() { Name = "黑见茜香", Weight = 1.0, LetterColor = "blue" }
                    };

                var defaultClass = new ClassConfig
                {
                    ClassId = "classes_default",
                    Name = "默认班级",
                    StudentList = defaultStudents
                };

                SaveClass(defaultClass);
                config.ActiveClassId = defaultClass.ClassId;
                config.StudentList = new List<StudentItem>(defaultClass.StudentList);
                YamlConfigService.SaveConfig(config);
                AppLogger.Info("ClassManager", "已自动初始化「默认班级」并关联到主配置");
            }
            else
            {
                // 存在班级，检查 activeClassId 是否有效
                var active = existingClasses.FirstOrDefault(c => c.ClassId == config.ActiveClassId);
                if (active == null)
                {
                    // 若指向不存在的班级或为空，自动指向第一个班级
                    active = existingClasses[0];
                    config.ActiveClassId = active.ClassId;
                    config.StudentList = new List<StudentItem>(active.StudentList);
                    YamlConfigService.SaveConfig(config);
                    AppLogger.Info("ClassManager", $"校准当前激活班级为: {active.Name} ({active.ClassId})");
                }
                else
                {
                    // 激活班级合法：若主配置名单为空而班级有名单，做单向同步
                    if ((config.StudentList == null || config.StudentList.Count == 0) && active.StudentList.Count > 0)
                    {
                        config.StudentList = new List<StudentItem>(active.StudentList);
                        YamlConfigService.SaveConfig(config);
                    }
                    // 反之若班级名单为空而主配置有名单，同步进班级
                    else if (active.StudentList.Count == 0 && (config.StudentList != null && config.StudentList.Count > 0))
                    {
                        active.StudentList = new List<StudentItem>(config.StudentList);
                        SaveClass(active);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("ClassManager", "初始化班级配置异常", ex);
        }
    }

    /// <summary>
    /// 列出所有班级
    /// </summary>
    public static List<ClassConfig> ListClasses()
    {
        var result = new List<ClassConfig>();
        string dir = GetClassesDir();
        if (!Directory.Exists(dir)) return result;

        var files = Directory.GetFiles(dir, "classes_*.yaml")
            .Concat(Directory.GetFiles(dir, "classes_*.yml"))
            .Distinct();

        foreach (var file in files)
        {
            try
            {
                string yaml = File.ReadAllText(file, Encoding.UTF8);
                var cls = _deserializer.Deserialize<ClassConfig>(yaml);
                if (cls != null)
                {
                    if (string.IsNullOrEmpty(cls.ClassId))
                    {
                        cls.ClassId = Path.GetFileNameWithoutExtension(file);
                    }
                    cls.StudentList ??= new();
                    result.Add(cls);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("ClassManager", $"读取班级失败: {file}", ex);
            }
        }

        return result.OrderBy(c => c.ClassId == "classes_default" ? 0 : 1).ThenBy(c => c.Name).ToList();
    }

    /// <summary>
    /// 创建新班级
    /// </summary>
    public static ClassConfig CreateClass(string name)
    {
        string id = $"classes_{DateTime.UtcNow.Ticks % 1000000000:D9}";
        var cls = new ClassConfig
        {
            ClassId = id,
            Name = string.IsNullOrWhiteSpace(name) ? "未命名班级" : name.Trim(),
            StudentList = new()
        };

        SaveClass(cls);
        AppLogger.Info("ClassManager", $"已创建新班级: {cls.Name} ({id})");
        return cls;
    }

    /// <summary>
    /// 保存班级数据
    /// </summary>
    public static bool SaveClass(ClassConfig cls)
    {
        if (string.IsNullOrEmpty(cls.ClassId)) return false;
        try
        {
            string path = GetClassFilePath(cls.ClassId);
            string yaml = _serializer.Serialize(cls);
            File.WriteAllText(path, yaml, Encoding.UTF8);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("ClassManager", $"保存班级异常: {cls.ClassId}", ex);
            return false;
        }
    }

    /// <summary>
    /// 重命名班级
    /// </summary>
    public static bool RenameClass(string classId, string newName)
    {
        var cls = LoadClass(classId);
        if (cls == null) return false;

        cls.Name = string.IsNullOrWhiteSpace(newName) ? "未命名班级" : newName.Trim();
        return SaveClass(cls);
    }

    /// <summary>
    /// 删除班级
    /// </summary>
    public static bool DeleteClass(string classId)
    {
        try
        {
            string path1 = Path.Combine(GetClassesDir(), $"{classId}.yaml");
            string path2 = Path.Combine(GetClassesDir(), $"{classId}.yml");
            if (File.Exists(path1)) File.Delete(path1);
            if (File.Exists(path2)) File.Delete(path2);
            AppLogger.Info("ClassManager", $"已删除班级: {classId}");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("ClassManager", $"删除班级失败: {classId}", ex);
            return false;
        }
    }

    /// <summary>
    /// 加载单个班级
    /// </summary>
    public static ClassConfig? LoadClass(string classId)
    {
        if (string.IsNullOrEmpty(classId)) return null;

        string path1 = Path.Combine(GetClassesDir(), $"{classId}.yaml");
        string path2 = Path.Combine(GetClassesDir(), $"{classId}.yml");
        string targetPath = File.Exists(path1) ? path1 : (File.Exists(path2) ? path2 : "");
        if (string.IsNullOrEmpty(targetPath)) return null;

        try
        {
            string yaml = File.ReadAllText(targetPath, Encoding.UTF8);
            var cls = _deserializer.Deserialize<ClassConfig>(yaml);
            if (cls != null)
            {
                if (string.IsNullOrEmpty(cls.ClassId))
                {
                    cls.ClassId = Path.GetFileNameWithoutExtension(targetPath);
                }
                cls.StudentList ??= new();
            }
            return cls;
        }
        catch (Exception ex)
        {
            AppLogger.Error("ClassManager", $"加载班级失败: {classId}", ex);
            return null;
        }
    }
}
