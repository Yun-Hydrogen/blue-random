// ==============================================================================
// 文件: UpdateService.cs
// 命名空间: BlueRandom.Modules.ApiBridge
// 作用: 应用程序在线检查更新与版本仲裁服务。
//       具备 GitHub Releases REST API 与 HTTP 302 重定向探针双通道容灾检测机制，
//       并提供语义化版本号（YY.MM.PATCH）与版本代号（Codename）的精准比对与分流。
// ==============================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BlueRandom.Modules.ApiBridge;

/// <summary>
/// 检查更新返回结果实体。
/// 承载状态、标题、更新日志详情、下载页面 URL、直链资产信息及版本号对比数据。
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

    /// <summary>详细说明或版本更新日志（Release Notes / changelog.md）。</summary>
    [JsonPropertyName("detail")]
    public string Detail { get; set; } = string.Empty;

    /// <summary>GitHub Release 对应的页面访问或下载链接。</summary>
    [JsonPropertyName("releaseUrl")]
    public string ReleaseUrl { get; set; } = string.Empty;

    /// <summary>远端 Release 可执行文件直链下载地址。</summary>
    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = string.Empty;

    /// <summary>远端 Release 可执行文件资产名称（如 BlueRandom.exe）。</summary>
    [JsonPropertyName("assetName")]
    public string AssetName { get; set; } = string.Empty;

    /// <summary>远端 Release 可执行文件字节大小（未知时为 0）。</summary>
    [JsonPropertyName("assetSize")]
    public long AssetSize { get; set; } = 0;

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
/// 更新包下载进度快照。
/// </summary>
public class UpdateDownloadProgress
{
    [JsonPropertyName("state")]
    public string State { get; set; } = "downloading";

    [JsonPropertyName("percent")]
    public double Percent { get; set; }

    [JsonPropertyName("bytesReceived")]
    public long BytesReceived { get; set; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("speedBytesPerSec")]
    public double SpeedBytesPerSec { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// 更新包下载结果。
/// </summary>
public class UpdateDownloadResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("cancelled")]
    public bool Cancelled { get; set; }

    [JsonPropertyName("stagedPath")]
    public string StagedPath { get; set; } = string.Empty;

    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// 自动替换更新启动结果。
/// </summary>
public class UpdateApplyResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

internal sealed record ReleaseApiMetadata(
    string TagName,
    string HtmlUrl,
    string Body,
    string? ChangelogAssetUrl,
    string DownloadUrl,
    string AssetName,
    long AssetSize);

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

    /// <summary>统一单文件发布产物文件名。</summary>
    public const string ReleaseExeFileName = "BlueRandom.exe";

    /// <summary>Release 附带的更新日志文件名。</summary>
    public const string ReleaseChangelogFileName = "changelog.md";

    /// <summary>构造指定 Tag 下 Release 资产的直接下载地址。</summary>
    public static string BuildReleaseAssetUrl(string tagName, string fileName)
    {
        return $"https://github.com/{RepoOwner}/{RepoName}/releases/download/{Uri.EscapeDataString(tagName)}/{Uri.EscapeDataString(fileName)}";
    }

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
            var parsed = ParseReleaseApiJson(json);
            if (parsed == null) return null;

            var (remoteVersion, _) = ParseTag(parsed.TagName);
            int cmp = CompareVersions(localVersion, remoteVersion);

            string? changelog = parsed.Body;
            if (cmp <= 0)
            {
                string? remoteChangelog = await TryFetchRemoteChangelogAsync(parsed.ChangelogAssetUrl, parsed.TagName, localVersion);
                if (!string.IsNullOrWhiteSpace(remoteChangelog))
                {
                    changelog = remoteChangelog;
                }
            }

            string downloadUrl = !string.IsNullOrWhiteSpace(parsed.DownloadUrl)
                ? parsed.DownloadUrl
                : BuildReleaseAssetUrl(parsed.TagName, ReleaseExeFileName);
            string assetName = !string.IsNullOrWhiteSpace(parsed.AssetName)
                ? parsed.AssetName
                : ReleaseExeFileName;

            return BuildResultFromTag(
                parsed.TagName,
                parsed.HtmlUrl,
                changelog,
                localVersion,
                localCodename,
                downloadUrl,
                assetName,
                parsed.AssetSize);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("UpdateService", $"GitHub API 请求失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 解析 GitHub Releases REST API 返回的 JSON，提取 Tag、正文、changelog.md 资产与可执行文件资产。
    /// </summary>
    internal static ReleaseApiMetadata? ParseReleaseApiJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string tagName = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(tagName)) return null;

        string htmlUrl = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? DefaultLatestUrl : DefaultLatestUrl;
        string body = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

        string? changelogAssetUrl = null;
        string downloadUrl = string.Empty;
        string assetName = string.Empty;
        long assetSize = 0;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string url = asset.TryGetProperty("browser_download_url", out var d) ? d.GetString() ?? "" : "";
                long size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out long sz) ? sz : 0;

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;

                if (name.Equals(ReleaseChangelogFileName, StringComparison.OrdinalIgnoreCase))
                {
                    changelogAssetUrl = url;
                }
                else if (name.Equals(ReleaseExeFileName, StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = url;
                    assetName = name;
                    assetSize = size;
                }
                else if (string.IsNullOrEmpty(downloadUrl) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = url;
                    assetName = name;
                    assetSize = size;
                }
            }
        }

        return new ReleaseApiMetadata(tagName, htmlUrl, body, changelogAssetUrl, downloadUrl, assetName, assetSize);
    }

    /// <summary>
    /// 从远端 Release 资产下载 changelog.md 文本内容。
    /// </summary>
    private static async Task<string?> TryFetchRemoteChangelogAsync(string? assetUrl, string tagName, string localVersion)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(assetUrl)) candidates.Add(assetUrl);
        string fallbackUrl = BuildReleaseAssetUrl(tagName, ReleaseChangelogFileName);
        if (!candidates.Contains(fallbackUrl, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(fallbackUrl);
        }

        foreach (string url in candidates)
        {
            try
            {
                using var handler = new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.All
                };
                using var client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(8)
                };
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BlueRandom", localVersion));

                var resp = await client.GetAsync(url);
                if (!resp.IsSuccessStatusCode) continue;

                string text = await resp.Content.ReadAsStringAsync();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("UpdateService", $"拉取远端 changelog.md 失败 ({url}): {ex.Message}");
            }
        }

        return null;
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

            var (remoteVersion, _) = ParseTag(tagName);
            int cmp = CompareVersions(localVersion, remoteVersion);
            string? remoteChangelog = cmp <= 0
                ? await TryFetchRemoteChangelogAsync(null, tagName, localVersion)
                : null;

            return BuildResultFromTag(
                tagName,
                locationStr,
                remoteChangelog,
                localVersion,
                localCodename,
                BuildReleaseAssetUrl(tagName, ReleaseExeFileName),
                ReleaseExeFileName,
                0);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("UpdateService", $"重定向探针模式失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 根据远端 Tag 与本地版本进行语义化对比，装配为结构化 UpdateCheckResult 结果。
    /// </summary>
    public static UpdateCheckResult BuildResultFromTag(string remoteTag, string releaseUrl, string? body, string localVersion, string localCodename)
        => BuildResultFromTag(remoteTag, releaseUrl, body, localVersion, localCodename, null, null, 0);

    /// <summary>
    /// 根据远端 Tag 与本地版本进行语义化对比，装配为结构化 UpdateCheckResult 结果（含下载资产信息）。
    /// 涵盖三种业务分支：update（发现新版）、ok（已是最新）、warn（抢先体验/开发预览版）。
    /// </summary>
    public static UpdateCheckResult BuildResultFromTag(
        string remoteTag,
        string releaseUrl,
        string? body,
        string localVersion,
        string localCodename,
        string? downloadUrl,
        string? assetName,
        long assetSize)
    {
        var (remoteVersion, remoteCodename) = ParseTag(remoteTag);
        int cmp = CompareVersions(localVersion, remoteVersion);

        if (cmp < 0)
        {
            // 云端版本更新：优先展示远端 Release 的 changelog.md / 正文
            return new UpdateCheckResult
            {
                Ok = true,
                Status = "update",
                Title = $"发现新版本：{remoteTag}",
                Detail = !string.IsNullOrWhiteSpace(body)
                    ? body.Trim()
                    : $"检测到云端发布了新版本（{remoteTag}），可点击下方按钮下载并自动更新。",
                ReleaseUrl = releaseUrl,
                DownloadUrl = downloadUrl ?? string.Empty,
                AssetName = assetName ?? string.Empty,
                AssetSize = assetSize,
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
            string localChangelog = AppVersion.GetChangelog();
            string changelog = !string.IsNullOrWhiteSpace(localChangelog)
                ? localChangelog
                : (!string.IsNullOrWhiteSpace(body) ? body.Trim() : string.Empty);
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

    // ========================================================================
    //  下载更新包与自动替换重启
    // ========================================================================

    /// <summary>
    /// 从指定 Release 下载地址流式下载更新包至临时目录，并实时回调下载进度。
    /// </summary>
    public static async Task<UpdateDownloadResult> DownloadUpdateAsync(
        string downloadUrl,
        long expectedSize,
        Action<UpdateDownloadProgress>? onProgress,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return new UpdateDownloadResult { Ok = false, Message = "无效的更新下载地址（仅支持 HTTPS）。" };
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "BlueRandom_Update");
        string partPath = Path.Combine(tempDir, $"{ReleaseExeFileName}.part");
        string stagedPath = Path.Combine(tempDir, ReleaseExeFileName);

        try
        {
            Directory.CreateDirectory(tempDir);
            TryDeleteFile(partPath);
            TryDeleteFile(stagedPath);

            onProgress?.Invoke(new UpdateDownloadProgress
            {
                State = "downloading",
                Percent = 0,
                BytesReceived = 0,
                TotalBytes = expectedSize > 0 ? expectedSize : -1,
                SpeedBytesPerSec = 0,
                Message = "正在连接下载服务器..."
            });

            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true
            };
            using var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BlueRandom", AppVersion.Version));

            using var resp = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                string errMsg = $"下载失败：HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
                onProgress?.Invoke(new UpdateDownloadProgress { State = "error", Message = errMsg });
                return new UpdateDownloadResult { Ok = false, Message = errMsg };
            }

            long totalBytes = resp.Content.Headers.ContentLength ?? (expectedSize > 0 ? expectedSize : -1);
            long bytesReceived = 0;
            var sw = Stopwatch.StartNew();
            long lastReportMs = -100;
            long lastSpeedBytes = 0;
            long lastSpeedMs = 0;
            double currentSpeed = 0;

            await using (var netStream = await resp.Content.ReadAsStreamAsync(ct))
            await using (var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                byte[] buffer = new byte[65536];
                int read;
                while ((read = await netStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                    bytesReceived += read;

                    long nowMs = sw.ElapsedMilliseconds;
                    if (nowMs - lastReportMs >= 80)
                    {
                        long dtMs = nowMs - lastSpeedMs;
                        if (dtMs >= 250)
                        {
                            currentSpeed = (bytesReceived - lastSpeedBytes) * 1000.0 / dtMs;
                            lastSpeedBytes = bytesReceived;
                            lastSpeedMs = nowMs;
                        }

                        double pct = totalBytes > 0
                            ? Math.Clamp(bytesReceived * 100.0 / totalBytes, 0, 99.9)
                            : 0;

                        onProgress?.Invoke(new UpdateDownloadProgress
                        {
                            State = "downloading",
                            Percent = Math.Round(pct, 1),
                            BytesReceived = bytesReceived,
                            TotalBytes = totalBytes,
                            SpeedBytesPerSec = currentSpeed,
                            Message = "正在下载更新包..."
                        });
                        lastReportMs = nowMs;
                    }
                }
            }

            onProgress?.Invoke(new UpdateDownloadProgress
            {
                State = "verifying",
                Percent = 100,
                BytesReceived = bytesReceived,
                TotalBytes = totalBytes > 0 ? totalBytes : bytesReceived,
                SpeedBytesPerSec = currentSpeed,
                Message = "正在校验更新包完整性..."
            });

            File.Move(partPath, stagedPath, overwrite: true);

            if (!IsValidExecutableFile(stagedPath))
            {
                TryDeleteFile(stagedPath);
                const string invalidMsg = "下载的文件不是有效的 Windows 可执行程序，已中止更新。";
                onProgress?.Invoke(new UpdateDownloadProgress { State = "error", Message = invalidMsg });
                return new UpdateDownloadResult { Ok = false, Message = invalidMsg };
            }

            onProgress?.Invoke(new UpdateDownloadProgress
            {
                State = "completed",
                Percent = 100,
                BytesReceived = bytesReceived,
                TotalBytes = totalBytes > 0 ? totalBytes : bytesReceived,
                SpeedBytesPerSec = currentSpeed,
                Message = "下载完成，准备替换并重启..."
            });

            AppLogger.Info("UpdateService", $"更新包下载并校验完成: {stagedPath} ({bytesReceived} bytes)");
            return new UpdateDownloadResult
            {
                Ok = true,
                StagedPath = stagedPath,
                Bytes = bytesReceived,
                Message = "下载完成"
            };
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(partPath);
            TryDeleteFile(stagedPath);
            onProgress?.Invoke(new UpdateDownloadProgress { State = "cancelled", Message = "已取消下载" });
            return new UpdateDownloadResult { Ok = false, Cancelled = true, Message = "已取消下载" };
        }
        catch (Exception ex)
        {
            TryDeleteFile(partPath);
            TryDeleteFile(stagedPath);
            AppLogger.Warn("UpdateService", $"下载更新包异常: {ex.Message}");
            string msg = $"下载更新失败：{ex.Message}";
            onProgress?.Invoke(new UpdateDownloadProgress { State = "error", Message = msg });
            return new UpdateDownloadResult { Ok = false, Message = msg };
        }
    }

    /// <summary>
    /// 校验文件是否为有效的 Windows PE 可执行文件（检查 MZ 文件头与最小长度）。
    /// </summary>
    internal static bool IsValidExecutableFile(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            var fi = new FileInfo(path);
            if (fi.Length < 1024) return false;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int b0 = fs.ReadByte();
            int b1 = fs.ReadByte();
            return b0 == 'M' && b1 == 'Z';
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 判断当前进程路径是否支持原地自替换更新（排除 dotnet.exe 调试宿主）。
    /// </summary>
    internal static bool CanSelfUpdate(string? currentExePath, out string reason)
    {
        if (string.IsNullOrWhiteSpace(currentExePath) || !File.Exists(currentExePath))
        {
            reason = "无法定位当前可执行文件路径。";
            return false;
        }

        string fileName = Path.GetFileName(currentExePath);
        if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            reason = "当前处于 dotnet 宿主调试模式，无法自动替换可执行文件，请使用发布版运行或手动下载。";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// 生成用于等待旧进程退出、替换可执行文件并重启新版本的 PowerShell 更新脚本。
    /// </summary>
    internal static string BuildUpdaterScript()
    {
        return """
param(
    [int]$TargetPid,
    [string]$SourceExe,
    [string]$TargetExe,
    [string]$TempDir
)
$ErrorActionPreference = 'Stop'
for ($i = 0; $i -lt 150; $i++) {
    try {
        $null = Get-Process -Id $TargetPid -ErrorAction Stop
        Start-Sleep -Milliseconds 100
    } catch {
        break
    }
}
$replaced = $false
for ($i = 0; $i -lt 50; $i++) {
    try {
        Copy-Item -LiteralPath $SourceExe -Destination $TargetExe -Force
        $replaced = $true
        break
    } catch {
        Start-Sleep -Milliseconds 200
    }
}
if ($replaced) {
    Start-Process -FilePath $TargetExe
}
try {
    if ($TempDir -and (Test-Path -LiteralPath $TempDir)) {
        Remove-Item -LiteralPath $TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
} catch {}
""";
    }

    /// <summary>
    /// 启动独立更新替换脚本，准备退出当前进程并自动重启新版本。
    /// </summary>
    public static UpdateApplyResult LaunchSelfUpdate(string stagedPath)
    {
        if (!IsValidExecutableFile(stagedPath))
        {
            return new UpdateApplyResult { Ok = false, Message = "更新包不存在或校验失败，请重新下载。" };
        }

        string currentExePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        if (!CanSelfUpdate(currentExePath, out string reason))
        {
            return new UpdateApplyResult { Ok = false, Message = reason };
        }

        string tempDir = Path.GetDirectoryName(stagedPath) ?? Path.Combine(Path.GetTempPath(), "BlueRandom_Update");
        string scriptPath = Path.Combine(tempDir, "apply_update.ps1");

        try
        {
            File.WriteAllText(scriptPath, BuildUpdaterScript(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            string targetDir = Path.GetDirectoryName(currentExePath) ?? "";
            bool writable = CanWriteToDirectory(targetDir);
            int pid = Environment.ProcessId;
            string args = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\" -TargetPid {pid} -SourceExe \"{stagedPath}\" -TargetExe \"{currentExePath}\" -TempDir \"{tempDir}\"";

            var psi = writable
                ? new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
                : new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = args,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };

            Process.Start(psi);
            AppLogger.Info("UpdateService", $"已启动自动替换更新脚本，目标路径: {currentExePath}");
            return new UpdateApplyResult { Ok = true, Message = "正在应用更新并重启..." };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new UpdateApplyResult { Ok = false, Message = "已取消管理员授权，无法替换受保护目录中的程序文件。" };
        }
        catch (Exception ex)
        {
            AppLogger.Error("UpdateService", "启动自动更新脚本失败", ex);
            return new UpdateApplyResult { Ok = false, Message = $"启动更新程序失败：{ex.Message}" };
        }
    }

    private static bool CanWriteToDirectory(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        string probe = Path.Combine(dir, $".br_write_probe_{Guid.NewGuid():N}.tmp");
        try
        {
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
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
