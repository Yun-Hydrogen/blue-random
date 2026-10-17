// ==============================================================================
// 文件: CustomsStore.cs
// 命名空间: BlueRandom.Services
// 作用: 用户自定义外部多媒体资产（图标、抽卡音效、BGM、立绘）仓储服务。
//       在 ~/.blue random/customs 目录下管理独立 YAML 数据，
//       提供 Base64 DataURL 头部清洗、字节流懒加载解码与 DataURL 实时合成转换。
// ==============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BlueRandom.Core.Config;
using BlueRandom.Core.Logging;
using System.Text.Json.Serialization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BlueRandom.Services;

/// <summary>
/// 自定义资产条目数据模型。
/// </summary>
public class CustomResourceItem
{
    /// <summary>操作成功响应标志。</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; set; } = true;

    /// <summary>资源全局唯一 ID（custom_毫秒时间戳）。</summary>
    [JsonPropertyName("id")]
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>原始上传文件全名（含扩展名）。</summary>
    [JsonPropertyName("fileName")]
    [YamlMember(Alias = "fileName")]
    public string FileName { get; set; } = string.Empty;

    /// <summary>媒体 MIME 类型（如 image/png、audio/wav、audio/mpeg）。</summary>
    [JsonPropertyName("mimeType")]
    [YamlMember(Alias = "mimeType")]
    public string MimeType { get; set; } = string.Empty;

    /// <summary>资产业务用途类别（icon、gachaSound、bgm）。</summary>
    [JsonPropertyName("purpose")]
    [YamlMember(Alias = "purpose")]
    public string Purpose { get; set; } = string.Empty;

    /// <summary>音频媒体总时长（秒数），非音频资产默认 0.0。</summary>
    [JsonPropertyName("duration")]
    [YamlMember(Alias = "duration")]
    public double Duration { get; set; } = 0.0;

    /// <summary>清洗后的无前缀 Base64 编码字符串。</summary>
    [JsonPropertyName("base64")]
    [YamlMember(Alias = "base64")]
    public string Base64 { get; set; } = string.Empty;

    /// <summary>
    /// 解码后的纯二进制字节数组。
    /// 采用按需懒加载解码计算，不参与 YAML/JSON 序列化存储。
    /// </summary>
    [JsonIgnore]
    [YamlIgnore]
    public byte[]? Bytes
    {
        get
        {
            if (string.IsNullOrEmpty(Base64)) return null;
            try
            {
                string clean = CustomsStore.CleanBase64(Base64);
                return Convert.FromBase64String(clean);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CustomsStore", $"解析 Base64 字节流异常 [{Id}]: {ex.Message}");
                return null;
            }
        }
    }
}

/// <summary>
/// 自定义多媒体资源存储服务 (customs/ 目录)。
/// 负责外部自定义素材的 Base64 规范化入库、磁盘序列化与运行时提取。
/// </summary>
public static class CustomsStore
{
    private static readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    /// <summary>
    /// 清除 Base64 字符串中的 DataURL 协议头（例如 "data:image/png;base64,"）。
    /// 返回仅含有效 base64 负载字符的纯净字符串，便于安全解码与序列化。
    /// </summary>
    /// <param name="rawBase64">输入的原始 Base64 或 DataURL 字符串。</param>
    /// <returns>清洗后的纯 Base64 文本。</returns>
    public static string CleanBase64(string rawBase64)
    {
        if (string.IsNullOrEmpty(rawBase64)) return string.Empty;
        int commaIdx = rawBase64.IndexOf(',');
        if (commaIdx >= 0)
        {
            return rawBase64.Substring(commaIdx + 1).Trim();
        }
        return rawBase64.Trim();
    }

    /// <summary>
    /// 标准化用途类别名称，兼容前端与历史传入的 "floating-icon"、"icon"、"gacha-sound"、"gachaSound"、"bgm" 等变体。
    /// </summary>
    /// <param name="purpose">原始用途字符串。</param>
    /// <returns>收敛后的规范用途类别（icon / gachasound / bgm）。</returns>
    public static string NormalizePurpose(string? purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose)) return string.Empty;
        string clean = purpose.Trim().ToLowerInvariant().Replace("-", "").Replace("_", "");
        if (clean.Contains("icon")) return "icon";
        if (clean.Contains("gacha") || clean == "sound") return "gachasound";
        if (clean.Contains("bgm") || clean.Contains("music")) return "bgm";
        return clean;
    }

    /// <summary>
    /// 获取并确保存在自定义素材存储主目录（~/.blue random/customs）。
    /// </summary>
    /// <returns>customs 目录绝对路径。</returns>
    public static string GetCustomsDir()
    {
        string dir = Path.Combine(YamlConfigService.GetUserDataDir(), "customs");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 将前端上传的自定义资源（图标、WAV/MP3 音效、BGM）格式化封装并落盘保存。
    /// 自动清洗 Base64 头部，生成毫秒级时间戳 ID 并持久化为 YAML 文件。
    /// </summary>
    /// <param name="fileName">原文件名。</param>
    /// <param name="mimeType">MIME 类型。</param>
    /// <param name="purpose">资源业务用途（icon、gachaSound、bgm）。</param>
    /// <param name="base64">Base64 数据串（可带 DataURL 头）。</param>
    /// <param name="duration">音频时长（秒）。</param>
    /// <returns>保存成功的资源实体；发生 IO 异常返回 null。</returns>
    public static CustomResourceItem? SaveCustom(string fileName, string mimeType, string purpose, string base64, double duration = 0)
    {
        try
        {
            string id = $"custom_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            string cleanBase64 = CleanBase64(base64);
            var item = new CustomResourceItem
            {
                Id = id,
                FileName = fileName,
                MimeType = mimeType,
                Purpose = purpose,
                Duration = duration,
                Base64 = cleanBase64
            };

            string path = Path.Combine(GetCustomsDir(), $"{id}.yaml");
            string yaml = _serializer.Serialize(item);
            File.WriteAllText(path, yaml, Encoding.UTF8);
            AppLogger.Info("CustomsStore", $"已持久化自定义资源: {id} ({fileName})");
            return item;
        }
        catch (Exception ex)
        {
            AppLogger.Error("CustomsStore", "保存自定义资源失败", ex);
            return null;
        }
    }

    /// <summary>
    /// 根据 customId 检索并加载自定义素材实体（customId.yaml）。
    /// </summary>
    /// <param name="customId">资源唯一标识符。</param>
    /// <returns>资源实体；未找到或解析失败返回 null。</returns>
    public static CustomResourceItem? LoadCustom(string customId)
    {
        if (string.IsNullOrEmpty(customId)) return null;
        string targetPath = Path.Combine(GetCustomsDir(), $"{customId}.yaml");
        if (!File.Exists(targetPath)) return null;

        try
        {
            string yaml = File.ReadAllText(targetPath, Encoding.UTF8);
            var item = _deserializer.Deserialize<CustomResourceItem>(yaml);
            if (item != null) item.Base64 = CleanBase64(item.Base64);
            return item;
        }
        catch (Exception ex)
        {
            AppLogger.Error("CustomsStore", $"加载自定义资源失败: {customId}", ex);
            return null;
        }
    }

    /// <summary>
    /// 彻底删除指定 customId 的磁盘资源文件（customId.yaml）。
    /// </summary>
    /// <param name="customId">目标素材 ID。</param>
    /// <returns>删除成功返回 true，发生异常返回 false。</returns>
    public static bool DeleteCustom(string customId)
    {
        try
        {
            string targetPath = Path.Combine(GetCustomsDir(), $"{customId}.yaml");
            if (File.Exists(targetPath)) File.Delete(targetPath);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("CustomsStore", $"删除自定义资源异常: {customId}", ex);
            return false;
        }
    }

    /// <summary>
    /// 提取指定自定义资产并装配为可在前端浏览器与 img/audio 标签中直接消费的标准 DataURL。
    /// </summary>
    /// <param name="customId">素材资源 ID。</param>
    /// <returns>形如 "data:image/png;base64,..." 的 DataURL 字符串；资源无效时返回 null。</returns>
    public static string? GetDataUrl(string customId)
    {
        var item = LoadCustom(customId);
        if (item == null || string.IsNullOrEmpty(item.Base64)) return null;
        string clean = CleanBase64(item.Base64);
        return $"data:{item.MimeType};base64,{clean}";
    }

    /// <summary>
    /// 扫描并列出 customs/ 目录下所有已保存的有效自定义素材实体。
    /// </summary>
    /// <returns>自定义资源条目列表。</returns>
    public static List<CustomResourceItem> ListAllCustoms()
    {
        var list = new List<CustomResourceItem>();
        string dir = GetCustomsDir();
        if (!Directory.Exists(dir)) return list;

        var files = Directory.GetFiles(dir, "custom_*.yaml");
        foreach (var file in files)
        {
            try
            {
                string yaml = File.ReadAllText(file, Encoding.UTF8);
                var item = _deserializer.Deserialize<CustomResourceItem>(yaml);
                if (item != null)
                {
                    if (string.IsNullOrEmpty(item.Id))
                    {
                        item.Id = Path.GetFileNameWithoutExtension(file);
                    }
                    list.Add(item);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CustomsStore", $"读取自定义资源失败: {file}: {ex.Message}");
            }
        }
        return list;
    }

    /// <summary>
    /// 启动时检查是否存在多个用于相同用途（purpose: icon, gachaSound, bgm）的资源；
    /// 若有多个，仅保留时间戳最新的那一个并物理删除其余旧资源，同时自动将最新资源绑定装载到当前配置中。
    /// </summary>
    /// <param name="config">当前运行态 RootConfig 实例。</param>
    /// <returns>若配置被修改返回 true，否则返回 false。</returns>
    public static bool ConsolidateAndBindLatestResources(RootConfig config)
    {
        try
        {
            string dir = GetCustomsDir();
            bool configChanged = false;

            if (Directory.Exists(dir))
            {
                var files = Directory.GetFiles(dir, "custom_*.yaml");
                if (files.Length > 0)
                {
                    // 1. 扫描所有可用资源并获取其最后修改时间与 Purpose
                    var itemsWithFiles = new List<(CustomResourceItem item, string filePath, DateTime writeTime)>();
                    foreach (var file in files)
                    {
                        try
                        {
                            string yaml = File.ReadAllText(file, Encoding.UTF8);
                            var item = _deserializer.Deserialize<CustomResourceItem>(yaml);
                            if (item != null && !string.IsNullOrWhiteSpace(item.Purpose))
                            {
                                if (string.IsNullOrEmpty(item.Id))
                                {
                                    item.Id = Path.GetFileNameWithoutExtension(file);
                                }
                                var fi = new FileInfo(file);
                                itemsWithFiles.Add((item, file, fi.LastWriteTimeUtc));
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warn("CustomsStore", $"解析资源失败，跳过: {file}: {ex.Message}");
                        }
                    }

                    // 2. 按用途标准化归类后分组处理（兼容 floating-icon, gacha-sound, bgm 等前端格式）
                    var groups = itemsWithFiles
                        .Select(x => (x.item, x.filePath, x.writeTime, normPurpose: NormalizePurpose(x.item.Purpose)))
                        .Where(x => !string.IsNullOrEmpty(x.normPurpose))
                        .GroupBy(x => x.normPurpose);

                    foreach (var group in groups)
                    {
                        string purpose = group.Key;
                        // 按修改时间与 ID 降序，最新文件排在最前
                        var sorted = group.OrderByDescending(x => x.writeTime).ThenByDescending(x => x.item.Id).ToList();
                        var latest = sorted[0];

                        // 如果存在多个相同用途的资源，只保留最新的，物理清理其余旧资源
                        if (sorted.Count > 1)
                        {
                            for (int i = 1; i < sorted.Count; i++)
                            {
                                var oldItem = sorted[i];
                                try
                                {
                                    if (File.Exists(oldItem.filePath))
                                    {
                                        File.Delete(oldItem.filePath);
                                        AppLogger.Info("CustomsStore", $"[资源去重单例化] 清理相同用途 ({purpose}) 的过期旧资源: {oldItem.item.Id} ({oldItem.item.FileName})");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    AppLogger.Warn("CustomsStore", $"清理旧资源失败: {oldItem.filePath}: {ex.Message}");
                                }
                            }
                        }

                        // 3. 将该用途最新生效的资源自动绑定更新至当前配置中
                        switch (purpose)
                        {
                            case "icon":
                                if (config.FloatingButton.CustomIconId != latest.item.Id)
                                {
                                    config.FloatingButton.CustomIconId = latest.item.Id;
                                    configChanged = true;
                                    AppLogger.Info("CustomsStore", $"[资源装载] 悬浮球图标已自动装载最新资源: {latest.item.Id} ({latest.item.FileName})");
                                }
                                break;

                            case "gachasound":
                                if (config.PickResultDialog.GachaSoundCustomId != latest.item.Id)
                                {
                                    config.PickResultDialog.GachaSoundCustomId = latest.item.Id;
                                    configChanged = true;
                                    AppLogger.Info("CustomsStore", $"[资源装载] 抽卡音效已自动装载最新资源: {latest.item.Id} ({latest.item.FileName})");
                                }
                                break;

                            case "bgm":
                                if (config.PickResultDialog.BgmCustomId != latest.item.Id)
                                {
                                    config.PickResultDialog.BgmCustomId = latest.item.Id;
                                    configChanged = true;
                                    AppLogger.Info("CustomsStore", $"[资源装载] 背景音乐已自动装载最新资源: {latest.item.Id} ({latest.item.FileName})");
                                }
                                break;
                        }
                    }
                }
            }

            // 4. 若配置中的自定义 ID 对应文件在磁盘上已不存在，安全回退重置
            if (!string.IsNullOrEmpty(config.FloatingButton.CustomIconId) && LoadCustom(config.FloatingButton.CustomIconId) == null)
            {
                config.FloatingButton.CustomIconId = string.Empty;
                configChanged = true;
            }
            if (!string.IsNullOrEmpty(config.PickResultDialog.GachaSoundCustomId) && LoadCustom(config.PickResultDialog.GachaSoundCustomId) == null)
            {
                config.PickResultDialog.GachaSoundCustomId = string.Empty;
                configChanged = true;
            }
            if (!string.IsNullOrEmpty(config.PickResultDialog.BgmCustomId) && LoadCustom(config.PickResultDialog.BgmCustomId) == null)
            {
                config.PickResultDialog.BgmCustomId = string.Empty;
                configChanged = true;
            }

            if (configChanged)
            {
                YamlConfigService.SaveConfig(config);
            }

            return configChanged;
        }
        catch (Exception ex)
        {
            AppLogger.Error("CustomsStore", "整理与单例化自定义资源异常", ex);
            return false;
        }
    }
}
