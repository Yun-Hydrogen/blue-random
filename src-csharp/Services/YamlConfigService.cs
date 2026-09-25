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
/// YAML 配置读写与序列化服务
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
    /// 获取数据与配置主目录路径
    /// 默认采用用户根目录下的 .blue random 目录 (~/.blue random)，与系统深度融合且程序升级时永不丢失配置；
    /// 仅在可执行文件同级已显式存在 configs 或 config 文件夹时作为便携模式加载，默认绝不在程序同级生成配置文件夹。
    /// </summary>
    public static string GetUserDataDir()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string localConfigs = Path.Combine(baseDir, "configs");
        string localConfig = Path.Combine(baseDir, "config");

        string dir;
        if (Directory.Exists(localConfigs))
        {
            dir = localConfigs;
        }
        else if (Directory.Exists(localConfig))
        {
            dir = localConfig;
        }
        else
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            dir = Path.Combine(userProfile, ".blue random");
        }

        Directory.CreateDirectory(dir);

        // 平滑无损自愈迁移：若 ~/.blue random 尚无主配置，且历史目录 LocalAppData\BlueRandom 存在配置，则自动迁移
        try
        {
            string cfgYaml = Path.Combine(dir, "config.yaml");
            string cfgYml = Path.Combine(dir, "config.yml");
            if (!File.Exists(cfgYaml) && !File.Exists(cfgYml))
            {
                string oldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueRandom");
                if (Directory.Exists(oldDir))
                {
                    string oldYaml = Path.Combine(oldDir, "config.yaml");
                    string oldYml = Path.Combine(oldDir, "config.yml");
                    if (File.Exists(oldYaml)) File.Copy(oldYaml, cfgYaml, true);
                    else if (File.Exists(oldYml)) File.Copy(oldYml, cfgYml, true);

                    // 迁移 classes 目录
                    string oldClasses = Path.Combine(oldDir, "classes");
                    string newClasses = Path.Combine(dir, "classes");
                    if (Directory.Exists(oldClasses) && !Directory.Exists(newClasses))
                    {
                        CopyDirectory(oldClasses, newClasses);
                    }

                    // 迁移 customs 目录
                    string oldCustoms = Path.Combine(oldDir, "customs");
                    string newCustoms = Path.Combine(dir, "customs");
                    if (Directory.Exists(oldCustoms) && !Directory.Exists(newCustoms))
                    {
                        CopyDirectory(oldCustoms, newCustoms);
                    }

                    // 迁移 .SHA256
                    string oldSha = Path.Combine(oldDir, ".SHA256");
                    string newSha = Path.Combine(dir, ".SHA256");
                    if (File.Exists(oldSha) && !File.Exists(newSha))
                    {
                        File.Copy(oldSha, newSha, true);
                    }

                    AppLogger.Info("ConfigService", "已自动从 LocalAppData 迁移历史配置与数据到 ~/.blue random 目录");
                }
            }
        }
        catch
        {
            // 忽略迁移异常
        }

        return dir;
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        var dir = new DirectoryInfo(sourceDir);
        if (!dir.Exists) return;

        Directory.CreateDirectory(destinationDir);

        foreach (var file in dir.GetFiles())
        {
            string targetFilePath = Path.Combine(destinationDir, file.Name);
            file.CopyTo(targetFilePath, true);
        }

        foreach (var subDir in dir.GetDirectories())
        {
            string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
            CopyDirectory(subDir.FullName, newDestinationDir);
        }
    }

    /// <summary>
    /// 获取主配置文件路径 (自动归一化合并 config.yml 与 config.yaml，消除双文件时间戳竞态引发的读取旧配置问题)
    /// </summary>
    public static string GetConfigFilePath()
    {
        string dir = GetUserDataDir();
        string yaml = Path.Combine(dir, "config.yaml");
        string yml = Path.Combine(dir, "config.yml");

        try
        {
            if (File.Exists(yml))
            {
                if (!File.Exists(yaml))
                {
                    File.Move(yml, yaml, true);
                    AppLogger.Info("ConfigService", "已自动归一化旧版 config.yml -> config.yaml");
                }
                else
                {
                    // 若两者同时存在，保留修改时间较新者，并彻底删除过期的旧文件
                    if (File.GetLastWriteTimeUtc(yml) > File.GetLastWriteTimeUtc(yaml))
                    {
                        File.Copy(yml, yaml, true);
                    }
                    File.Delete(yml);
                    AppLogger.Info("ConfigService", "已消除 config.yml 与 config.yaml 双文件冲突");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("ConfigService", $"归一化配置文件提示: {ex.Message}");
        }

        return yaml;
    }

    /// <summary>
    /// 确保配置文件存在（如果不存在则写入带注释的默认配置模板）
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
    /// 从原始 YAML 文本中窥探 protocolVersion 字段值
    /// </summary>
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
    /// 判断当前配置文件是否需要执行协议变更兼容迁移（协议代号不同或未包含协议字段时返回 true）
    /// </summary>
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
    /// 核心设计：仅当检测到配置协议版本不同时，才自动进入协议变更处理（触发兼容性迁移并落盘保存）；
    /// 协议版本一致时直接原生反序列化，绝不触发兼容程序，保证高性能与配置纯净。
    /// </summary>
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
    /// 协议变更兼容处理程序：仅在检测到协议版本不同时调用。
    /// 自动匹配已有条目并迁移配置，缺失的属性按模型定义补全默认值。
    /// </summary>
    internal static RootConfig MigrateLegacyConfig(string rawYaml)
    {
        // 1. 容错转换下划线 (snake_case) 或中划线 (kebab-case) 命名为标准 camelCase
        string normalizedYaml = NormalizeYamlKeys(rawYaml);

        // 2. 兼容历史遗留的旧版音量格式（例如 0.0-1.0 浮点转换为 0-100 整数）
        if (normalizedYaml.Contains("gachaSoundVolume:") && !normalizedYaml.Contains("soundVolume:"))
        {
            normalizedYaml = System.Text.RegularExpressions.Regex.Replace(
                normalizedYaml,
                @"(?m)^(\s*)gachaSoundVolume:\s*([0-9.]+)",
                match =>
                {
                    string indent = match.Groups[1].Value;
                    if (double.TryParse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture, out double vol))
                    {
                        int intVol = (int)Math.Round(vol <= 1.0 ? vol * 100 : vol);
                        return $"{indent}soundVolume: {intVol}";
                    }
                    return match.Value;
                }
            );
        }

        // 3. 反序列化：自动匹配已有条目，多余字段自动忽略
        RootConfig? config = null;
        try
        {
            config = _deserializer.Deserialize<RootConfig>(normalizedYaml);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("ConfigService", $"旧配置反序列化容错: {ex.Message}，将保留有效条目并以默认配置补全");
        }

        // 4. 补齐缺失属性的默认值并完成数值边界归一化
        return Normalize(config);
    }

    /// <summary>
    /// 容错转换 YAML 键：将旧版本下划线 (snake_case) 或中划线 (kebab-case) 命名转换为标准 camelCase
    /// </summary>
    private static string NormalizeYamlKeys(string rawYaml)
    {
        if (string.IsNullOrWhiteSpace(rawYaml)) return rawYaml;

        return System.Text.RegularExpressions.Regex.Replace(
            rawYaml,
            @"(?m)^(\s*)([a-z0-9]+(?:[_-][a-z0-9]+)+):",
            match =>
            {
                string indent = match.Groups[1].Value;
                string key = match.Groups[2].Value;
                string camelKey = ToCamelCase(key);
                return $"{indent}{camelKey}:";
            }
        );
    }

    private static string ToCamelCase(string str)
    {
        string[] parts = str.Split('_', '-');
        if (parts.Length <= 1) return str;
        var sb = new StringBuilder(parts[0].ToLowerInvariant());
        for (int i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
            {
                sb.Append(char.ToUpperInvariant(parts[i][0]));
                if (parts[i].Length > 1) sb.Append(parts[i].Substring(1).ToLowerInvariant());
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 保存配置到磁盘
    /// </summary>
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
    /// 写入默认配置内容
    /// </summary>
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
    /// 配置清洗与归一化
    /// </summary>
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
    /// 序列化为带中文注释的 YAML
    /// </summary>
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
                sb.AppendLine($"    weight: {s.Weight:F1}");
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
        sb.AppendLine($"    x: {(cfg.FloatingButton.Position.X.HasValue ? cfg.FloatingButton.Position.X.Value.ToString() : "null")}");
        sb.AppendLine($"    y: {(cfg.FloatingButton.Position.Y.HasValue ? cfg.FloatingButton.Position.Y.Value.ToString() : "null")}");
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
        sb.AppendLine($"  panelOpacity: {cfg.PickResultDialog.PanelOpacity:F2}");
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
