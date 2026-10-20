// ==============================================================================
// 文件: ClassManagerService.cs
// 命名空间: BlueRandom.Services
// 作用: 多班级（Roster）花名册持久化管理服务。
//       在 ~/.blue random/classes 目录下为各班级独立维护 YAML 配置文件，
//       实现班级增删改查与双向名单智能校准（EnsureInitialized）。
// ==============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using BlueRandom.Core.Config;
using BlueRandom.Core.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BlueRandom.Services;

/// <summary>
/// 班级数据结构实体模型。
/// 支持 YAML 与 JSON 双向 CamelCase 序列化。
/// </summary>
public class ClassConfig
{
    /// <summary>班级全局唯一标识符（格式如 classes_xxxxxxxxx 或 classes_default）。</summary>
    [JsonPropertyName("classId")]
    [YamlMember(Alias = "classId")]
    public string ClassId { get; set; } = string.Empty;

    /// <summary>班级展示名称（如 "一班"、"阿拜多斯对策委员会"）。</summary>
    [JsonPropertyName("name")]
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "未命名班级";

    /// <summary>该班级下属候选学生名单列表。</summary>
    [JsonPropertyName("studentList")]
    [YamlMember(Alias = "studentList")]
    public List<StudentItem> StudentList { get; set; } = new();
}

/// <summary>
/// 多班级管理服务 (classes/ 目录)。
/// 提供班级文件的磁盘独立存储与全套 CRUD 业务操作。
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

    /// <summary>
    /// 获取并确保存在班级名单存储主目录（~/.blue random/classes）。
    /// </summary>
    /// <returns>班级目录的绝对物理路径。</returns>
    public static string GetClassesDir()
    {
        string dir = Path.Combine(YamlConfigService.GetUserDataDir(), "classes");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 解析指定班级 ID 对应的物理文件路径（classes_*.yaml）。
    /// </summary>
    /// <param name="classId">班级唯一 ID。</param>
    /// <returns>班级物理文件绝对路径。</returns>
    private static string GetClassFilePath(string classId)
    {
        return Path.Combine(GetClassesDir(), $"{classId}.yaml");
    }

    /// <summary>
    /// 确保 classes 目录与默认班级正确初始化并与全局配置进行双向校准。
    /// <para>
    /// 校准规则：
    /// 1. 若本地没有任何班级文件，以当前配置中的学生名单（若为空则使用默认三一/阿拜多斯新手引导名单）自动构建 classes_default 班级文件，并绑定 activeClassId；
    /// 2. 若存在班级但配置中的 activeClassId 无效或为空，自动收敛指向现有首个班级；
    /// 3. 若 activeClassId 合法，且主配置或班级文件任意一方名单为空时，执行单向数据补齐同步，确保名单不因初次升级丢失。
    /// </para>
    /// </summary>
    /// <param name="config">当前运行态 RootConfig 实例。</param>
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
    /// 扫描并列出 classes/ 目录下所有已保存的班级列表（classes_*.yaml）。
    /// 按默认班级在前、其余按名称升序排列。
    /// </summary>
    /// <returns>班级实体列表。</returns>
    public static List<ClassConfig> ListClasses()
    {
        var result = new List<ClassConfig>();
        string dir = GetClassesDir();
        if (!Directory.Exists(dir)) return result;

        var files = Directory.GetFiles(dir, "classes_*.yaml");

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
    /// 根据名称新建一个空的班级并落盘。
    /// 生成格式为 classes_xxxxxxxxx（基于时间戳刻度）的全局唯一 ID。
    /// </summary>
    /// <param name="name">班级显示名称。</param>
    /// <returns>新建并已保存的班级实体对象。</returns>
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
    /// 将班级实体序列化为标准 YAML 并持久化保存到磁盘。
    /// </summary>
    /// <param name="cls">待持久化的班级实体。</param>
    /// <returns>保存成功返回 true，发生异常或 classId 为空返回 false。</returns>
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
    /// 重命名指定的现有班级并同步更新磁盘文件。
    /// </summary>
    /// <param name="classId">目标班级 ID。</param>
    /// <param name="newName">新班级名称。</param>
    /// <returns>重命名并落盘成功返回 true；未找到班级或保存失败返回 false。</returns>
    public static bool RenameClass(string classId, string newName)
    {
        var cls = LoadClass(classId);
        if (cls == null) return false;

        cls.Name = string.IsNullOrWhiteSpace(newName) ? "未命名班级" : newName.Trim();
        return SaveClass(cls);
    }

    /// <summary>
    /// 彻底删除指定的班级文件（classes_*.yaml）。
    /// </summary>
    /// <param name="classId">待删除的目标班级 ID。</param>
    /// <returns>删除成功返回 true，出现 IO 异常返回 false。</returns>
    public static bool DeleteClass(string classId)
    {
        try
        {
            string path = GetClassFilePath(classId);
            if (File.Exists(path)) File.Delete(path);
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
    /// 按 classId 从磁盘检索并加载指定的班级详情。
    /// 支持智能补全缺失的 classId 与空名单。
    /// </summary>
    /// <param name="classId">班级唯一标识符。</param>
    /// <returns>解析成功的 ClassConfig 实例；文件不存在或损坏返回 null。</returns>
    public static ClassConfig? LoadClass(string classId)
    {
        if (string.IsNullOrEmpty(classId)) return null;

        string targetPath = GetClassFilePath(classId);
        if (!File.Exists(targetPath)) return null;

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
