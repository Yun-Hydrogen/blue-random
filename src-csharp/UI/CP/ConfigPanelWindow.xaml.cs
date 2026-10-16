// ==============================================================================
// 文件: ConfigPanelWindow.xaml.cs
// 命名空间: BlueRandom.UI.CP
// 作用: WebUI 设置面板宿主窗体。
//       基于 Microsoft.Web.WebView2（Chromium 内核），通过 WebResourceRequested 拦截器
//       直接从 ResourceCache 内存中加载前端静态产物（https://appassets/*），
//       向 JavaScript 注入 NativeBridge COM 对象，并实现窗体隐藏常驻秒开机制。
// ==============================================================================

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BlueRandom.Core.Ipc;
using BlueRandom.Core.Logging;
using BlueRandom.Core.Resources;
using BlueRandom.Services;
using Microsoft.Web.WebView2.Core;

namespace BlueRandom.UI.CP;

/// <summary>
/// WebUI 配置面板窗体宿主。
/// </summary>
public partial class ConfigPanelWindow : Window
{
    private bool _isWebViewReady = false;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    /// <summary>
    /// 初始化配置面板窗体实例，挂载托盘与程序图标，并异步发起 WebView2 预热。
    /// </summary>
    public ConfigPanelWindow()
    {
        InitializeComponent();
        var icon = ResourceCache.GetImage("app.ico");
        if (icon != null) Icon = icon;
        IpcBroker.Subscribe(IpcModule.Cp, OnIpcMessageReceived);
        _ = InitializeWebViewAsync();
    }

    /// <summary>
    /// 确保 WebView2 运行时环境已完成初始化与前端页面预载（供窗口管理器后台预热调用）。
    /// </summary>
    /// <returns>异步任务 Task。</returns>
    public Task EnsureReadyAsync() => InitializeWebViewAsync();

    /// <summary>
    /// 异步初始化 CoreWebView2 运行时、注入 NativeBridge 桥接对象与虚拟域资源拦截器。
    /// <para>
    /// 核心机制：
    /// 1. 用户数据目录隔离：指定 ~/.blue random/EBWebView 独立存储 Cookie/Storage；
    /// 2. 缓存全量禁用：调用 DevTools Protocol 强制禁用并清空 Network Cache，保证前端更新 100% 实时生效；
    /// 3. 原生 JS 桥接注入：AddHostObjectToScript("nativeBridge", new NativeBridge(this))；
    /// 4. 虚拟域零磁盘托管：拦截 <c>https://appassets/*</c> 请求，从 ResourceCache 内存字典返回静态流；
    /// 5. 外部链接安全路由：拦截 NewWindowRequested 并委托系统默认浏览器打开。
    /// </para>
    /// </summary>
    private async Task InitializeWebViewAsync()
    {
        if (_isWebViewReady) return;
        await _initLock.WaitAsync();
        try
        {
            if (_isWebViewReady) return;

            string userDataDir = Path.Combine(YamlConfigService.GetUserDataDir(), "EBWebView");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
            await WebViewControl.EnsureCoreWebView2Async(env);

            // 彻底清除并全局禁用 WebView2 HTTP 磁盘/内存/Storage 缓存 (防前端改动因缓存不生效)
            try
            {
                await WebViewControl.CoreWebView2.Profile.ClearBrowsingDataAsync();
                await WebViewControl.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.clearBrowserCache", "{}");
                await WebViewControl.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled", "{\"cacheDisabled\": true}");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CP", $"禁用 WebView2 前端缓存提示: {ex.Message}");
            }

            WebViewControl.CoreWebView2.Settings.IsStatusBarEnabled = false;
            WebViewControl.CoreWebView2.Settings.AreDevToolsEnabled = true;

            // 注册 NativeBridge 对象到前端 window.chrome.webview.hostObjects.nativeBridge
            WebViewControl.CoreWebView2.AddHostObjectToScript("nativeBridge", new NativeBridge(this));

            // 外部链接拦截，使用系统默认浏览器打开
            WebViewControl.CoreWebView2.NewWindowRequested += (s, args) =>
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
            WebViewControl.CoreWebView2.AddWebResourceRequestedFilter("https://appassets/*", CoreWebView2WebResourceContext.All);
            WebViewControl.CoreWebView2.WebResourceRequested += (sender, args) =>
            {
                var uri = new Uri(args.Request.Uri);
                string path = uri.AbsolutePath.TrimStart('/');
                if (string.IsNullOrEmpty(path)) path = "index.html";

                byte[]? content = ResourceCache.GetWebUiAsset(path);
                if (content != null)
                {
                    string mimeType = GetMimeType(path);
                    var stream = new MemoryStream(content);
                    var response = WebViewControl.CoreWebView2.Environment.CreateWebResourceResponse(
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
                    args.Response = WebViewControl.CoreWebView2.Environment.CreateWebResourceResponse(
                        null, 404, "Not Found", "Cache-Control: no-cache\r\n"
                    );
                }
            };

            string devUrl = Environment.GetEnvironmentVariable("VITE_DEV_SERVER_URL") ?? "";
            if (!string.IsNullOrEmpty(devUrl))
            {
                WebViewControl.Source = new Uri($"{devUrl}#/config-panel");
            }
            else
            {
                WebViewControl.Source = new Uri("https://appassets/index.html#/config-panel");
            }

            _isWebViewReady = true;
            AppLogger.Info("CP", "WebView2 配置面板初始化就绪 (ResCache 全内存托管 + 禁用缓存模式)");
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

    /// <summary>
    /// 窗体关闭拦截钩子。
    /// 取消实际销毁操作，改为直接隐藏窗体（Hide），以保持 WebView2 渲染上下文实现秒开体验。
    /// </summary>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        // 隐藏而非销毁，保持 WebView2 状态以秒开
        e.Cancel = true;
        Hide();
    }

    /// <summary>
    /// 响应发往 CP 模块的 IPC 消息。
    /// 调度 ShowConfigPanel 激活前台、FetchConfig/OverwriteConfig 向前端注入最新 YAML，以及 Destroy 彻底销毁。
    /// </summary>
    /// <param name="msg">IPC 消息实体。</param>
    private void OnIpcMessageReceived(IpcMessage msg)
    {
        Dispatcher.Invoke(async () =>
        {
            switch (msg.Command)
            {
                case "ShowConfigPanel":
                    Show();
                    if (!_isWebViewReady)
                    {
                        await InitializeWebViewAsync();
                    }
                    if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                    Activate();
                    Topmost = true;
                    Topmost = false;
                    Focus();
                    break;

                case "FetchConfig":
                case "OverwriteConfig":
                    if (_isWebViewReady && msg.Payload is string yaml)
                    {
                        // 通过 JS 调用前端通知
                        string escapedYaml = System.Text.Json.JsonSerializer.Serialize(yaml);
                        string script = $"if (window.__onConfigUpdated) window.__onConfigUpdated({escapedYaml});";
                        try
                        {
                            await WebViewControl.ExecuteScriptAsync(script);
                        }
                        catch
                        {
                            // 忽略前端尚未加载完时的异常
                        }
                    }
                    break;

                case "Destroy":
                    Closing -= OnWindowClosing;
                    Close();
                    break;
            }
        });
    }
}
