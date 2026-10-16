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
}
