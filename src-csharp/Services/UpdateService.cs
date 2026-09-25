using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BlueRandom.Core;
using BlueRandom.Core.Logging;

namespace BlueRandom.Services;

/// <summary>
/// 检查更新返回结果实体
/// </summary>
public class UpdateCheckResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; } = true;

    /// <summary>
    /// 更新状态：'update' (有新版本) | 'ok' (已是最新) | 'warn' (本地开发预览版本) | 'error' (检查失败)
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "ok";

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("detail")]
    public string Detail { get; set; } = string.Empty;

    [JsonPropertyName("releaseUrl")]
    public string ReleaseUrl { get; set; } = string.Empty;

    [JsonPropertyName("localVersion")]
    public string LocalVersion { get; set; } = string.Empty;

    [JsonPropertyName("remoteVersion")]
    public string RemoteVersion { get; set; } = string.Empty;

    [JsonPropertyName("remoteTag")]
    public string RemoteTag { get; set; } = string.Empty;
}

/// <summary>
/// 应用程序在线检查更新服务
/// 支持 GitHub Releases API 检索与 302 重定向免限流探针降级机制
/// </summary>
public static class UpdateService
{
    public const string RepoOwner = "Yun-Hydrogen";
    public const string RepoName = "blue-random";
    public const string DefaultLatestUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/latest";

    /// <summary>
    /// 执行在线更新检查
    /// </summary>
    public static async Task<UpdateCheckResult> CheckUpdateAsync()
    {
        string localVersion = AppVersion.Version;
        string localCodename = AppVersion.Codename;

        AppLogger.Info("UpdateService", $"开始检查更新，本地版本: {localVersion} (代号: {localCodename})");

        try
        {
            // 阶段 1：首选通过 GitHub Releases REST API 获取最新版本和更新说明
            var apiResult = await TryFetchReleaseFromApiAsync(localVersion, localCodename);
            if (apiResult != null)
            {
                return apiResult;
            }

            // 阶段 2：降级方案，通过 releases/latest 302 重定向探针获取最新 tag（规避 GitHub API 403 限流或未认证拦截）
            AppLogger.Info("UpdateService", "GitHub REST API 未命中或被限流，启用 302 重定向探针模式");
            var redirectResult = await TryFetchReleaseFromRedirectAsync(localVersion, localCodename);
            if (redirectResult != null)
            {
                return redirectResult;
            }

            return new UpdateCheckResult
            {
                Ok = false,
                Status = "error",
                Title = "检查更新失败",
                Detail = "未能从 GitHub 获取最新版本信息，请确认网络连接或点击下方按钮直接访问 Releases 页面。",
                ReleaseUrl = DefaultLatestUrl,
                LocalVersion = localVersion
            };
        }
        catch (Exception ex)
        {
            AppLogger.Warn("UpdateService", $"检查更新异常: {ex.Message}");
            return new UpdateCheckResult
            {
                Ok = false,
                Status = "error",
                Title = "检查更新失败",
                Detail = $"网络请求异常 ({ex.Message})，可点击下方按钮直接前往 GitHub Releases 查看。",
                ReleaseUrl = DefaultLatestUrl,
                LocalVersion = localVersion
            };
        }
    }

    /// <summary>
    /// 通过 GitHub API 获取最新发布
    /// </summary>
    private static async Task<UpdateCheckResult?> TryFetchReleaseFromApiAsync(string localVersion, string localCodename)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All
            };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(8)
            };

            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BlueRandom", localVersion));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            string apiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            var resp = await client.GetAsync(apiUrl);

            if (!resp.IsSuccessStatusCode)
            {
                AppLogger.Warn("UpdateService", $"GitHub API 响应非成功状态码: {(int)resp.StatusCode} {resp.ReasonPhrase}");
                return null;
            }

            string json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            string htmlUrl = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? DefaultLatestUrl : DefaultLatestUrl;
            string body = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

            if (string.IsNullOrWhiteSpace(tagName)) return null;

            return BuildResultFromTag(tagName, htmlUrl, body, localVersion, localCodename);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("UpdateService", $"GitHub API 请求失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 通过 github.com/releases/latest 302 重定向探针获取最新 tag
    /// 无论 API 是否限流，302 跳转 Location 均能无阻碍解析最新版本 tag
    /// </summary>
    private static async Task<UpdateCheckResult?> TryFetchReleaseFromRedirectAsync(string localVersion, string localCodename)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false
            };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(8)
            };

            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BlueRandom", localVersion));

            var resp = await client.GetAsync(DefaultLatestUrl);
            var location = resp.Headers.Location;
            if (location == null) return null;

            string locationStr = location.ToString();
            // 典型重定向形如: https://github.com/Yun-Hydrogen/blue-random/releases/tag/v26.09.01 或 Shiroko-v26.10.01
            int tagIdx = locationStr.LastIndexOf("/releases/tag/", StringComparison.OrdinalIgnoreCase);
            if (tagIdx < 0) return null;

            string tagName = locationStr.Substring(tagIdx + "/releases/tag/".Length).Trim();
            if (string.IsNullOrWhiteSpace(tagName)) return null;

            return BuildResultFromTag(tagName, locationStr, null, localVersion, localCodename);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("UpdateService", $"重定向探针模式失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 根据远端 Tag 生成最终结果
    /// </summary>
    public static UpdateCheckResult BuildResultFromTag(string remoteTag, string releaseUrl, string? body, string localVersion, string localCodename)
    {
        var (remoteVersion, remoteCodename) = ParseTag(remoteTag);
        int cmp = CompareVersions(localVersion, remoteVersion);

        if (cmp < 0)
        {
            // 云端版本更新
            return new UpdateCheckResult
            {
                Ok = true,
                Status = "update",
                Title = $"发现新版本：{remoteTag}",
                Detail = !string.IsNullOrWhiteSpace(body)
                    ? $"更新日志：\n{body.Trim()}"
                    : $"检测到云端发布了新版本（{remoteTag}），建议前往下载更新。",
                ReleaseUrl = releaseUrl,
                LocalVersion = localVersion,
                RemoteVersion = remoteVersion,
                RemoteTag = remoteTag
            };
        }
        else if (cmp == 0)
        {
            // 本地与云端相同
            return new UpdateCheckResult
            {
                Ok = true,
                Status = "ok",
                Title = $"已是最新版本：v{localVersion}",
                Detail = $"当前版本（{localCodename} v{localVersion}）已是最新公开发布版本，无需更新。",
                ReleaseUrl = releaseUrl,
                LocalVersion = localVersion,
                RemoteVersion = remoteVersion,
                RemoteTag = remoteTag
            };
        }
        else
        {
            // 本地版本高于云端公开发布（开发预览版）
            return new UpdateCheckResult
            {
                Ok = true,
                Status = "warn",
                Title = $"当前为开发预览版本：v{localVersion}",
                Detail = $"云端最新公开发布版本为 {remoteTag}，您当前运行的是抢先版本或内部测试构建。",
                ReleaseUrl = releaseUrl,
                LocalVersion = localVersion,
                RemoteVersion = remoteVersion,
                RemoteTag = remoteTag
            };
        }
    }

    /// <summary>
    /// 解析 Git Release Tag 字符串中的版本号与代号
    /// 支持格式如: "Shiroko-v26.10.01", "v26.09.01", "26.08.01", "v2.0.1"
    /// </summary>
    public static (string Version, string? Codename) ParseTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return ("0.0.0", null);
        tag = tag.Trim();

        var match = Regex.Match(tag, @"^(?:(?<codename>[a-zA-Z0-9_\u4e00-\u9fa5-]+)-)?v?(?<version>\d+(?:\.\d+)+)");
        if (match.Success)
        {
            string ver = match.Groups["version"].Value;
            string? code = match.Groups["codename"].Success ? match.Groups["codename"].Value : null;
            return (ver, code);
        }

        string fallback = tag.TrimStart('v', 'V');
        return (fallback, null);
    }

    /// <summary>
    /// 逐段比较两个版本号字符串
    /// 返回值：
    ///   1: v1 > v2
    ///  -1: v1 < v2
    ///   0: v1 == v2
    /// </summary>
    public static int CompareVersions(string v1, string v2)
    {
        var p1 = ParseVersionSegments(v1);
        var p2 = ParseVersionSegments(v2);
        int maxLen = Math.Max(p1.Count, p2.Count);

        for (int i = 0; i < maxLen; i++)
        {
            int s1 = i < p1.Count ? p1[i] : 0;
            int s2 = i < p2.Count ? p2[i] : 0;
            if (s1 > s2) return 1;
            if (s1 < s2) return -1;
        }

        return 0;
    }

    private static List<int> ParseVersionSegments(string version)
    {
        var list = new List<int>();
        if (string.IsNullOrWhiteSpace(version)) return list;

        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var m = Regex.Match(part, @"^\d+");
            if (m.Success && int.TryParse(m.Value, out int val))
            {
                list.Add(val);
            }
            else
            {
                list.Add(0);
            }
        }
        return list;
    }
}
