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

public class CustomResourceItem
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; } = true;

    [JsonPropertyName("id")]
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("fileName")]
    [YamlMember(Alias = "fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("mimeType")]
    [YamlMember(Alias = "mimeType")]
    public string MimeType { get; set; } = string.Empty;

    [JsonPropertyName("purpose")]
    [YamlMember(Alias = "purpose")]
    public string Purpose { get; set; } = string.Empty;

    [JsonPropertyName("duration")]
    [YamlMember(Alias = "duration")]
    public double Duration { get; set; } = 0.0;

    [JsonPropertyName("base64")]
    [YamlMember(Alias = "base64")]
    public string Base64 { get; set; } = string.Empty;

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
/// 自定义资源存储服务 (customs/ 目录)
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

    public static string GetCustomsDir()
    {
        string dir = Path.Combine(YamlConfigService.GetUserDataDir(), "customs");
        Directory.CreateDirectory(dir);
        return dir;
    }

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

    public static CustomResourceItem? LoadCustom(string customId)
    {
        if (string.IsNullOrEmpty(customId)) return null;
        string path1 = Path.Combine(GetCustomsDir(), $"{customId}.yaml");
        string path2 = Path.Combine(GetCustomsDir(), $"{customId}.yml");
        string targetPath = File.Exists(path1) ? path1 : (File.Exists(path2) ? path2 : "");
        if (string.IsNullOrEmpty(targetPath)) return null;

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

    public static bool DeleteCustom(string customId)
    {
        try
        {
            string path1 = Path.Combine(GetCustomsDir(), $"{customId}.yaml");
            string path2 = Path.Combine(GetCustomsDir(), $"{customId}.yml");
            if (File.Exists(path1)) File.Delete(path1);
            if (File.Exists(path2)) File.Delete(path2);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("CustomsStore", $"删除自定义资源异常: {customId}", ex);
            return false;
        }
    }

    public static string? GetDataUrl(string customId)
    {
        var item = LoadCustom(customId);
        if (item == null || string.IsNullOrEmpty(item.Base64)) return null;
        string clean = CleanBase64(item.Base64);
        return $"data:{item.MimeType};base64,{clean}";
    }
}
