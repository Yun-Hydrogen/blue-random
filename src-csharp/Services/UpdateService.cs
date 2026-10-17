// ==============================================================================
// 文件: UpdateService.cs
// 命名空间: BlueRandom.Services
// 作用: 应用程序在线检查更新与版本仲裁服务。
//       具备 GitHub Releases REST API 与 HTTP 302 重定向探针双通道容灾检测机制，
//       并提供语义化版本号（YY.MM.PATCH）与版本代号（Codename）的精准比对与分流。
// ==============================================================================

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
/// 检查更新返回结果实体。
/// 承载状态、标题、更新日志详情、下载页面 URL 及版本号对比数据。
/// </summary>
public class UpdateCheckResult
{
    /// <summary>检查更新是否成功发起并完成探测。</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; set; } = true;

    /// <summary>
    /// 更新状态状态码：
    /// 'update' (云端有更高版本可供升级) | 'ok' (本地与云端一致，已是最新) | 'warn' (本地版本高于云端，处于开发预览状态) | 'error' (网络或接口异常)
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "ok";

    /// <summary>弹窗或前端展示标题。</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>详细说明或版本更新日志（Release Notes）。</summary>
    [JsonPropertyName("detail")]
    public string Detail { get; set; } = string.Empty;

    /// <summary>GitHub Release 对应的页面访问或下载链接。</summary>
    [JsonPropertyName("releaseUrl")]
    public string ReleaseUrl { get; set; } = string.Empty;

    /// <summary>当前本地运行的版本号字符串。</summary>
    [JsonPropertyName("localVersion")]
    public string LocalVersion { get; set; } = string.Empty;

    /// <summary>远端最新的版本号字符串。</summary>
    [JsonPropertyName("remoteVersion")]
    public string RemoteVersion { get; set; } = string.Empty;

    /// <summary>远端最新的完整 Git Tag 标签名（例如 "Shiroko-v26.10.01"）。</summary>
    [JsonPropertyName("remoteTag")]
    public string RemoteTag { get; set; } = string.Empty;
}

/// <summary>
/// 应用程序在线检查更新服务。
/// 支持 GitHub Releases API 检索与 302 重定向免限流探针降级机制。
/// </summary>
public static class UpdateService
{
    /// <summary>GitHub 仓库拥有者名称。</summary>
    public const string RepoOwner = "Yun-Hydrogen";

    /// <summary>GitHub 仓库项目名称。</summary>
    public const string RepoName = "blue-random";

    /// <summary>GitHub 仓库最新版本发布默认跳转地址。</summary>
    public const string DefaultLatestUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/latest";

    /// <summary>
    /// 异步执行应用在线更新检查。
    /// <para>
    /// 双模检测流水线：
    /// 1. 阶段 1：首选请求 GitHub Releases REST API（获取完整版本号、代号与 Markdown 格式更新日志）；
    /// 2. 阶段 2（降级容灾）：若 API 请求因网络波动、无鉴权触发 403 限流或超时失败，自动启用 302 重定向探针；
    ///    通过禁止自动跟随重定向读取 Location 响应头中定向的 tag 名称，保证无论 GitHub API 限流状态如何均能 100% 探知最新可用版本；
    /// 3. 若双通道均断网不可达，构造规范的 error 状态对象并附带手动跳转链接。
    /// </para>
    /// </summary>
    /// <returns>标准化 UpdateCheckResult 结果实体。</returns>
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
    /// 首选检测途径：请求 GitHub Releases REST API。
    /// 解析返回的 JSON 结构，提取 tag_name、html_url 与 Release Notes 正文。
    /// </summary>
    /// <param name="localVersion">本地当前版本。</param>
    /// <param name="localCodename">本地版本代号。</param>
    /// <returns>解析成功的 UpdateCheckResult；若接口超时、403 限流或网络异常则返回 null 进入降级模式。</returns>
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
    /// 降级容灾途径：通过 github.com/releases/latest 发起 HTTP HEAD/GET 探针。
    /// <para>
    /// 原理：
    /// GitHub 对未经身份认证的 REST API 设置了每小时 60 次的 IP 频次限制（403 Rate Limit Exceeded）；
    /// 但用户直接访问 releases/latest 会由 GitHub 服务器返回 HTTP 302 Found 重定向至目标 Tag 路径（如 .../releases/tag/Shiroko-v26.10.01）。
    /// 本方法通过将 <c>AllowAutoRedirect</c> 置为 false，拦截并读取 Location 标头，实现 100% 免 API 限流的版本获取。
    /// </para>
    /// </summary>
    /// <param name="localVersion">本地当前版本。</param>
    /// <param name="localCodename">本地版本代号。</param>
    /// <returns>基于重定向 Location 生成的更新结果；若探针失败返回 null。</returns>
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
    /// 根据远端 Tag 与本地版本进行语义化对比，装配为结构化 UpdateCheckResult 结果。
    /// 涵盖三种业务分支：update（发现新版）、ok（已是最新）、warn（抢先体验/开发预览版）。
    /// </summary>
    /// <param name="remoteTag">远端 Git 标签名。</param>
    /// <param name="releaseUrl">目标版本详情链接。</param>
    /// <param name="body">更新日志详情（可选）。</param>
    /// <param name="localVersion">本地语义化版本号。</param>
    /// <param name="localCodename">本地版本代号。</param>
    /// <returns>组装好的实体结果对象。</returns>
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
            string changelog = !string.IsNullOrWhiteSpace(body) ? body.Trim() : AppVersion.GetChangelog();
            return new UpdateCheckResult
            {
                Ok = true,
                Status = "ok",
                Title = $"已是最新版本：v{localVersion}",
                Detail = !string.IsNullOrWhiteSpace(changelog) ? changelog : $"当前版本（{localCodename} v{localVersion}）已是最新公开发布版本，无需更新。",
                ReleaseUrl = releaseUrl,
                LocalVersion = localVersion,
                RemoteVersion = remoteVersion,
                RemoteTag = remoteTag
            };
        }
        else
        {
            // 本地版本高于云端公开发布（开发预览版）
            string changelog = !string.IsNullOrWhiteSpace(body) ? body.Trim() : AppVersion.GetChangelog();
            return new UpdateCheckResult
            {
                Ok = true,
                Status = "warn",
                Title = $"当前为开发预览版本：v{localVersion}",
                Detail = !string.IsNullOrWhiteSpace(changelog) ? changelog : $"云端最新公开发布版本为 {remoteTag}，您当前运行的是抢先版本或内部测试构建。",
                ReleaseUrl = releaseUrl,
                LocalVersion = localVersion,
                RemoteVersion = remoteVersion,
                RemoteTag = remoteTag
            };
        }
    }

    /// <summary>
    /// 解析 Git Release Tag 字符串中的版本号与代号。
    /// 正则兼容模式：
    /// 1. "Shiroko-v26.10.01" -> Version: "26.10.01", Codename: "Shiroko"
    /// 2. "v26.09.01" -> Version: "26.09.01", Codename: null
    /// 3. "26.08.01" -> Version: "26.08.01", Codename: null
    /// 4. "v2.0.1" -> Version: "2.0.1", Codename: null
    /// </summary>
    /// <param name="tag">原始 Tag 字符串。</param>
    /// <returns>拆解出的版本号与可选代号元组。</returns>
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
    /// 逐段数值化比较两个版本号字符串（支持多段比较，如 26.10.02 vs 26.09.01，1.0 vs 1.0.0）。
    /// </summary>
    /// <param name="v1">版本号 1。</param>
    /// <param name="v2">版本号 2。</param>
    /// <returns>
    ///  1: v1 大于 v2；
    /// -1: v1 小于 v2；
    ///  0: v1 等于 v2。
    /// </returns>
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

    /// <summary>
    /// 将以点分隔的版本号文本切分为纯数字段数组。
    /// 能够过滤非数字后缀并以 0 填充非法分段。
    /// </summary>
    /// <param name="version">版本字符串。</param>
    /// <returns>整数分段列表。</returns>
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
