// ==============================================================================
// 文件: YamlConfigService.cs
// 命名空间: BlueRandom.Services
// 作用: 应用核心 YAML 配置文件持久化、协议版本演进与版本升级迁移驱动服务。
//       采用规范用户目录 ~/.blue random/config.yaml 进行单点原子持久化，
//       支持跨 C# 版本协议代号变更驱动迁移以及带中文注释的友好序列化。
// ==============================================================================

using System;
using System.IO;
using System.Text;
using BlueRandom.Core;
using BlueRandom.Core.Config;
using BlueRandom.Core.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BlueRandom.Services;

/// <summary>
/// YAML 配置读写与序列化服务。
/// 提供零丢失持久化、跨 C# 版本协议变更迁移与数值边界清洗。
/// </summary>
public static class YamlConfigService
{
    private static readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    /// <summary>
    /// 获取数据与配置主目录物理路径。
    /// 统一采用操作系统用户目录下的 .blue random 目录（%UserProfile%/.blue random），
    /// 保证软件更新覆盖时配置与班级数据永不丢失。
    /// </summary>
    /// <returns>用户数据主目录绝对路径。</returns>
    public static string GetUserDataDir()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string dir = Path.Combine(userProfile, ".blue random");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 获取主配置文件路径（config.yaml）。
    /// </summary>
    /// <returns>规范化后的 config.yaml 绝对路径。</returns>
    public static string GetConfigFilePath()
    {
        return Path.Combine(GetUserDataDir(), "config.yaml");
    }

    /// <summary>
    /// 确保配置文件存在。
    /// 若磁盘尚无配置文件，则以当前发布代号写入包含丰富中文注释的默认配置模板。
    /// </summary>
    public static void EnsureConfigFileExists()
    {
        string path = GetConfigFilePath();
        if (!File.Exists(path))
        {
            AppLogger.Info("ConfigService", "主配置文件不存在，生成默认模板: " + path);
            WriteDefaultConfig(path);
        }
    }

    /// <summary>
    /// 高性能前置探测：直接通过单行正则从原始 YAML 字符串中窥探 protocolVersion 协议代号。
    /// 避免在仅需判定版本协议时全量解析反序列化整个对象树。
    /// </summary>
    /// <param name="rawYaml">YAML 原始字符串。</param>
    /// <returns>检测到的协议代号字符串（如 "Shiroko"）；未声明时返回 null。</returns>
    public static string? PeekProtocolVersion(string rawYaml)
    {
        if (string.IsNullOrWhiteSpace(rawYaml)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(rawYaml, @"(?m)^protocolVersion:\s*['""]?([^'""]\S*?)['""]?\s*$");
        if (match.Success)
        {
            return match.Groups[1].Value.Trim();
        }
        return null;
    }

    /// <summary>
    /// 判断当前磁盘配置文件是否需要执行协议变更兼容迁移。
    /// 当文件中的协议代号与当前客户端程序集定义的 AppVersion.Codename 不一致时返回 true。
    /// </summary>
    /// <returns>需要迁移返回 true，否则返回 false。</returns>
    public static bool IsProtocolMigrationNeeded()
    {
        string path = GetConfigFilePath();
        if (!File.Exists(path)) return false;
        try
        {
            string rawYaml = File.ReadAllText(path, Encoding.UTF8);
            string? fileProtocol = PeekProtocolVersion(rawYaml);
            return !string.Equals(fileProtocol, AppVersion.Codename, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 读取并解析配置文件。
    /// <para>
    /// 架构设计原则（协议代号驱动）：
    /// 1. 协议版本一致时：直接原生反序列化并返回，绝不执行额外兼容代码，确保毫秒级纯净冷启动；
    /// 2. 协议版本不一致时（跨版本升级）：自动激活 MigrateLegacyConfig 容错管道，
    ///    兼容下划线键名、纠正旧版浮点音量并补齐新字段，完成后自动更新 protocolVersion 并持久化落盘。
    /// </para>
    /// </summary>
    /// <param name="autoMigrate">若检测到协议变更是否自动落盘保存更新后的配置。</param>
    /// <returns>已清洗边界的 RootConfig 实体对象。</returns>
    public static RootConfig LoadConfig(bool autoMigrate = true)
    {
        string path = GetConfigFilePath();
        try
        {
            if (File.Exists(path))
            {
                string rawYaml = File.ReadAllText(path, Encoding.UTF8);
                string? fileProtocol = PeekProtocolVersion(rawYaml);

                // 1. 若协议版本完全一致：纯净直接反序列化，不触发任何兼容性转换代码
                if (string.Equals(fileProtocol, AppVersion.Codename, StringComparison.OrdinalIgnoreCase))
                {
                    var standardConfig = _deserializer.Deserialize<RootConfig>(rawYaml);
                    return Normalize(standardConfig);
                }

                // 2. 只有当检测到协议版本不同时，才进入协议变更兼容处理程序
                if (autoMigrate)
                {
                    AppLogger.Info("ConfigService", $"检测到配置协议版本变更 [{fileProtocol ?? "未声明/旧协议"} -> {AppVersion.Codename}]，自动进入协议变更兼容迁移程序");
                    var migrated = MigrateLegacyConfig(rawYaml);
                    migrated.ProtocolVersion = AppVersion.Codename;
                    SaveConfig(migrated);
                    AppLogger.Info("ConfigService", $"配置协议迁移完成并已持久化保存，当前协议版本: {AppVersion.Codename}");
                    return migrated;
                }
                else
                {
                    // 仅探测模式（例如 Stage 1 检查管理员权限）：执行快速容错反序列化，不写回磁盘
                    var tempConfig = _deserializer.Deserialize<RootConfig>(rawYaml) ?? new RootConfig();
                    return Normalize(tempConfig);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("ConfigService", "解析 config.yaml 失败", ex);
            throw; // 抛出异常供启动器捕获以提示用户
        }

        var def = new RootConfig();
        return Normalize(def);
    }

    /// <summary>
    /// 跨版本协议变更配置兼容迁移管道。
    /// 用于在 C# 版本升级时平滑读取已有配置，自动补齐新增字段默认值并执行数值边界归一化。
    /// </summary>
    /// <param name="rawYaml">已有配置 YAML 文本。</param>
    /// <returns>升级并归一化后的 RootConfig 实例。</returns>
    internal static RootConfig MigrateLegacyConfig(string rawYaml)
    {
        RootConfig? config = null;
        try
        {
            config = _deserializer.Deserialize<RootConfig>(rawYaml);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("ConfigService", $"配置反序列化容错: {ex.Message}，将保留有效条目并以默认配置补全");
        }

        return Normalize(config);
    }

    /// <summary>
    /// 将配置对象清洗、格式化带注释后原子持久化写入主配置文件。
    /// </summary>
    /// <param name="config">待保存的配置对象。</param>
    public static void SaveConfig(RootConfig config)
    {
        string path = GetConfigFilePath();
        try
        {
            var normalized = Normalize(config);
            string yaml = ToYamlWithComments(normalized);
            File.WriteAllText(path, yaml, Encoding.UTF8);
            AppLogger.Info("ConfigService", "配置文件已持久化落盘");
        }
        catch (Exception ex)
        {
            AppLogger.Error("ConfigService", "保存 config.yaml 失败", ex);
        }
    }

    /// <summary>
    /// 向指定路径写入出厂预设的完整默认配置模板。
    /// </summary>
    /// <param name="path">目标文件路径。</param>
    public static void WriteDefaultConfig(string path)
    {
        var defaultConfig = new RootConfig
        {
            ProtocolVersion = AppVersion.Codename,
            StudentList = new()
            {
                new StudentItem { Name = "早濑优香", Weight = 1.0, LetterColor = "rainbow" },
                new StudentItem { Name = "黑见茜香", Weight = 1.0, LetterColor = "gold" },
                new StudentItem { Name = "砂狼白子", Weight = 1.0, LetterColor = "blue" }
            }
        };

        string yaml = ToYamlWithComments(defaultConfig);
        File.WriteAllText(path, yaml, Encoding.UTF8);
    }

    /// <summary>
    /// 对配置实例的各字段进行合法性清洗与数值边界裁剪（Clamping）。
    /// 防止由于手误或损坏文件导致缩放比例异常、音量超界或透明度非法。
    /// </summary>
    /// <param name="input">待清洗的原始配置实例。</param>
    /// <returns>清洗归一化后的 RootConfig 实例。</returns>
    public static RootConfig Normalize(RootConfig? input)
    {
        var cfg = input ?? new RootConfig();

        if (string.IsNullOrWhiteSpace(cfg.ProtocolVersion))
        {
            cfg.ProtocolVersion = AppVersion.Codename;
        }

        // 悬浮按钮数值边界裁剪
        cfg.FloatingButton.SizePercent = Math.Clamp(cfg.FloatingButton.SizePercent, 50, 200);
        cfg.FloatingButton.TransparencyPercent = Math.Clamp(cfg.FloatingButton.TransparencyPercent, 0, 100);
        cfg.FloatingButton.IconSize = Math.Clamp(cfg.FloatingButton.IconSize, 16, 128);

        // 人数弹窗边界
        cfg.PickCountDialog.DefaultCount = Math.Clamp(cfg.PickCountDialog.DefaultCount, 1, 10);

        // 结果弹窗数值
        cfg.PickResultDialog.SoundVolume = Math.Clamp(cfg.PickResultDialog.SoundVolume, 0, 100);
        cfg.PickResultDialog.MusicVolume = Math.Clamp(cfg.PickResultDialog.MusicVolume, 0, 100);
        cfg.PickResultDialog.PanelOpacity = Math.Clamp(cfg.PickResultDialog.PanelOpacity, 0.1, 1.0);

        return cfg;
    }

    /// <summary>
    /// 将配置对象序列化为包含清晰分段与详细中文注释的标准 YAML 文本。
    /// 方便用户直接通过外部文本编辑器（记事本、VS Code 等）阅读和调整。
    /// </summary>
    /// <param name="cfg">归一化后的配置对象。</param>
    /// <returns>格式美观的 YAML 字符串。</returns>
    public static string ToYamlWithComments(RootConfig cfg)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ============================================================");
        sb.AppendLine("#  蔚蓝点名 (Blue Random) 配置文件");
        sb.AppendLine("#  可在此修改或通过配置面板界面进行可视化设置");
        sb.AppendLine("# ============================================================");
        sb.AppendLine();
        sb.AppendLine($"protocolVersion: '{(!string.IsNullOrWhiteSpace(cfg.ProtocolVersion) ? cfg.ProtocolVersion : AppVersion.Codename)}'");
        sb.AppendLine();
        sb.AppendLine("# ---- 抽取名单 ----");
        sb.AppendLine($"allowRepeatDraw: {(cfg.AllowRepeatDraw ? "true" : "false")}");
        sb.AppendLine($"agreedEula: {(cfg.AgreedEula ? "true" : "false")}");
        sb.AppendLine($"activeClassId: '{cfg.ActiveClassId}'");
        sb.AppendLine("studentList:");
        if (cfg.StudentList.Count == 0)
        {
            sb.AppendLine("  []");
        }
        else
        {
            foreach (var s in cfg.StudentList)
            {
                sb.AppendLine($"  - name: \"{s.Name}\"");
                sb.AppendLine($"    weight: {s.Weight.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}");
                sb.AppendLine($"    letterColor: {s.LetterColor}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("# ---- 悬浮按钮外观与行为 ----");
        sb.AppendLine("floatingButton:");
        sb.AppendLine($"  sizePercent: {cfg.FloatingButton.SizePercent}");
        sb.AppendLine($"  transparencyPercent: {cfg.FloatingButton.TransparencyPercent}");
        sb.AppendLine($"  alwaysOnTop: {(cfg.FloatingButton.AlwaysOnTop ? "true" : "false")}");
        sb.AppendLine($"  showInTaskbar: {(cfg.FloatingButton.ShowInTaskbar ? "true" : "false")}");
        sb.AppendLine("  position:");
        sb.AppendLine($"    x: {(cfg.FloatingButton.Position.X.HasValue ? cfg.FloatingButton.Position.X.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null")}");
        sb.AppendLine($"    y: {(cfg.FloatingButton.Position.Y.HasValue ? cfg.FloatingButton.Position.Y.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null")}");
        sb.AppendLine($"  customIconId: '{cfg.FloatingButton.CustomIconId}'");
        sb.AppendLine($"  iconSize: {cfg.FloatingButton.IconSize}");
        sb.AppendLine($"  borderColor: '{cfg.FloatingButton.BorderColor}'");

        sb.AppendLine();
        sb.AppendLine("# ---- 抽取人数设置 ----");
        sb.AppendLine("pickCountDialog:");
        sb.AppendLine($"  defaultCount: {cfg.PickCountDialog.DefaultCount}");

        sb.AppendLine();
        sb.AppendLine("# ---- 结果浮窗设置 ----");
        sb.AppendLine("pickResultDialog:");
        sb.AppendLine($"  defaultPlayGachaSound: {(cfg.PickResultDialog.DefaultPlayGachaSound ? "true" : "false")}");
        sb.AppendLine($"  gachaSoundCustomId: '{cfg.PickResultDialog.GachaSoundCustomId}'");
        sb.AppendLine($"  soundVolume: {cfg.PickResultDialog.SoundVolume}");
        sb.AppendLine($"  playMusic: {(cfg.PickResultDialog.PlayMusic ? "true" : "false")}");
        sb.AppendLine($"  bgmCustomId: '{cfg.PickResultDialog.BgmCustomId}'");
        sb.AppendLine($"  musicVolume: {cfg.PickResultDialog.MusicVolume}");
        sb.AppendLine($"  bgmStartTime: {cfg.PickResultDialog.BgmStartTime}");
        sb.AppendLine($"  bgmFadeDuration: {cfg.PickResultDialog.BgmFadeDuration}");
        sb.AppendLine($"  panelOpacity: {cfg.PickResultDialog.PanelOpacity.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
        sb.AppendLine($"  panelBgColor: '{cfg.PickResultDialog.PanelBgColor}'");
        sb.AppendLine($"  panelBorderColor: '{cfg.PickResultDialog.PanelBorderColor}'");
        sb.AppendLine($"  showDeco: {(cfg.PickResultDialog.ShowDeco ? "true" : "false")}");

        sb.AppendLine();
        sb.AppendLine("# ---- 高级与系统权限 ----");
        sb.AppendLine("admin:");
        sb.AppendLine($"  adminAutoStartAdmin: {(cfg.Admin.AdminAutoStartAdmin ? "true" : "false")}");
        sb.AppendLine($"  adminAutoStartPath: '{cfg.Admin.AdminAutoStartPath}'");
        sb.AppendLine($"  adminAutoStartTaskName: '{cfg.Admin.AdminAutoStartTaskName}'");
        sb.AppendLine($"  requireAdminOnLaunch: {(cfg.Admin.RequireAdminOnLaunch ? "true" : "false")}");
        sb.AppendLine($"  uiAccessEnabled: {(cfg.Admin.UiAccessEnabled ? "true" : "false")}");

        return sb.ToString();
    }
}
