// ==============================================================================
// 文件: ConfigPanelBrowserHost.cs
// 命名空间: BlueRandom.Modules.WebUIBridger
// 作用: WebUI 设置面板 WebView2 宿主管理类。
//       基于 Microsoft.Web.WebView2（Chromium 内核），管理核心运行时初始化、
//       COM 原生桥接注册、虚拟域内存静态资源拦截（https://appassets/*）、
//       外部超链接安全分发以及向前端推送配置更新脚本。
// ==============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace BlueRandom.Modules.WebUIBridger;

/// <summary>
/// WebUI 配置面板 WebView2 浏览器宿主管理器。
/// </summary>
internal sealed class ConfigPanelBrowserHost
{
    private readonly WebView2 _webView;
    private readonly NativeBridge _bridge;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _isReady = false;

    /// <summary>
    /// 获取 WebView2 运行时环境与前端页面是否已初始化就绪。
    /// </summary>
    public bool IsReady => _isReady;

    /// <summary>
    /// 初始化配置面板浏览器宿主实例。
    /// </summary>
    /// <param name="webView">承载前端页面的 WebView2 控件。</param>
    /// <param name="bridge">注入前端的原生交互桥接对象。</param>
    public ConfigPanelBrowserHost(WebView2 webView, NativeBridge bridge)
    {
        _webView = webView;
        _bridge = bridge;
    }

    /// <summary>
    /// 确保 WebView2 运行时环境已完成初始化与前端页面预载（供窗口管理器后台预热调用）。
    /// <para>
    /// 核心机制：
    /// 1. 用户数据目录隔离：指定 ~/.blue random/EBWebView 独立存储 Cookie/Storage；
    /// 2. 缓存全量禁用：调用 DevTools Protocol 强制禁用并清空 Network Cache，保证前端更新 100% 实时生效；
    /// 3. 原生 JS 桥接注入：AddHostObjectToScript("nativeBridge", bridge)；
    /// 4. 虚拟域零磁盘托管：拦截 <c>https://appassets/*</c> 请求，从 ResourceCache 内存字典返回静态流；
    /// 5. 外部链接安全路由：拦截 NewWindowRequested 并委托系统默认浏览器打开。
    /// </para>
    /// </summary>
    /// <returns>异步任务 Task。</returns>
    public async Task EnsureReadyAsync()
    {
        if (_isReady) return;
        await _initLock.WaitAsync();
        try
        {
            if (_isReady) return;
            var window = Window.GetWindow(_webView);
            if (window != null)
            {
                new WindowInteropHelper(window).EnsureHandle();
            }
            await InitializeWebViewCoreAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("CP", "初始化 WebView2 失败", ex);
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// 向前端页面推送最新的配置 YAML 文本。
    /// </summary>
    /// <param name="yaml">YAML 格式配置字符串。</param>
    /// <returns>异步任务 Task。</returns>
    public async Task PushConfigUpdatedAsync(string yaml)
    {
        if (!_isReady) return;

        // 通过 JS 调用前端通知
        string escapedYaml = JsonSerializer.Serialize(yaml);
        string script = $"if (window.__onConfigUpdated) window.__onConfigUpdated({escapedYaml});";
        try
        {
            await _webView.ExecuteScriptAsync(script);
        }
        catch
        {
            // 忽略前端尚未加载完时的异常
        }
    }

    /// <summary>
    /// 异步执行 CoreWebView2 内核初始化、环境配置、桥接对象注入及资源拦截器注册。
    /// </summary>
    /// <returns>异步任务 Task。</returns>
    private async Task InitializeWebViewCoreAsync()
    {
        var env = await WebViewEnvironmentManager.GetCpEnvironmentAsync();
        await _webView.EnsureCoreWebView2Async(env);

        // 彻底清除并全局禁用 WebView2 HTTP 磁盘/内存/Storage 缓存 (防前端改动因缓存不生效)
        try
        {
            await _webView.CoreWebView2.Profile.ClearBrowsingDataAsync();
            await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.clearBrowserCache", "{}");
            await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled", "{\"cacheDisabled\": true}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("CP", $"禁用 WebView2 前端缓存提示: {ex.Message}");
        }

        _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;

        // 注册 NativeBridge 对象到前端 window.chrome.webview.hostObjects.nativeBridge
        _webView.CoreWebView2.AddHostObjectToScript("nativeBridge", _bridge);

        // 外部链接拦截，使用系统默认浏览器打开
        _webView.CoreWebView2.NewWindowRequested += (s, args) =>
        {
            args.Handled = true;
            try
            {
                Process.Start(new ProcessStartInfo { FileName = args.Uri, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLogger.Error("CP", $"无法在浏览器中打开链接: {args.Uri}", ex);
            }
        };

        // 注册 WebResourceRequested 拦截器：所有前端 WebUI 页面与资源均由 ResCache 零磁盘全内存托管提供
        _webView.CoreWebView2.AddWebResourceRequestedFilter("https://appassets/*", CoreWebView2WebResourceContext.All);
        _webView.CoreWebView2.WebResourceRequested += (sender, args) =>
        {
            var uri = new Uri(args.Request.Uri);
            string path = uri.AbsolutePath.TrimStart('/');
            if (string.IsNullOrEmpty(path)) path = "index.html";

            byte[]? content = ResourceCache.GetWebUiAsset(path);
            if (content != null)
            {
                string mimeType = GetMimeType(path);
                var stream = new MemoryStream(content);
                var response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                    stream,
                    200,
                    "OK",
                    $"Content-Type: {mimeType}\r\n" +
                    "Cache-Control: no-cache, no-store, must-revalidate, max-age=0\r\n" +
                    "Pragma: no-cache\r\n" +
                    "Expires: 0\r\n"
                );
                args.Response = response;
            }
            else
            {
                args.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                    null, 404, "Not Found", "Cache-Control: no-cache\r\n"
                );
            }
        };

        string devUrl = Environment.GetEnvironmentVariable("VITE_DEV_SERVER_URL") ?? "";
        if (!string.IsNullOrEmpty(devUrl))
        {
            _webView.Source = new Uri($"{devUrl}#/config-panel");
        }
        else
        {
            _webView.Source = new Uri("https://appassets/index.html#/config-panel");
        }

        _isReady = true;
        AppLogger.Info("CP", "WebView2 配置面板初始化就绪 (ResCache 全内存托管 + 禁用缓存模式)");
    }

    /// <summary>
    /// 根据相对文件路径扩展名解析标准的 HTTP Content-Type MIME 标头。
    /// </summary>
    /// <param name="path">资源相对文件路径。</param>
    /// <returns>MIME 字符串（如 "text/html; charset=utf-8"）。</returns>
    private static string GetMimeType(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".woff2" => "font/woff2",
            ".woff" => "font/woff",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            ".wasm" => "application/wasm",
            _ => "application/octet-stream"
        };
    }
}
