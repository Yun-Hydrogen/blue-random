// ==============================================================================
// 文件: WebViewEnvironmentManager.cs
// 命名空间: BlueRandom.Services
// 作用: WebView2 运行时环境管理器。
//
// 架构说明：
//   当前只有配置面板（CP）继续使用 WebView2；结果浮窗已改为原生 DirectComposition
//   渲染，不再需要 WebView2 环境与独立的 userDataFolder（EBWebView_FRW）。
//   CP 环境在 WebUI_Thread 中惰性初始化，避免 COM 套间与 userDataFolder 互斥锁冲突。
// ==============================================================================

using System;
using System.IO;
using System.Threading.Tasks;
using BlueRandom.Core.Logging;
using Microsoft.Web.WebView2.Core;

namespace BlueRandom.Services;

/// <summary>
/// WebView2 运行时环境管理器：为配置面板（CP）提供唯一的浏览器环境。
/// </summary>
public static class WebViewEnvironmentManager
{
    private static Task<CoreWebView2Environment>? _cpEnvTask;
    private static readonly object _lock = new();

    /// <summary>
    /// 获取配置面板（CP）使用的 WebView2 环境（共享目录 EBWebView）。
    /// </summary>
    /// <returns>CoreWebView2Environment 实例任务。</returns>
    public static Task<CoreWebView2Environment> GetCpEnvironmentAsync()
    {
        lock (_lock)
        {
            if (_cpEnvTask == null)
            {
                string userDataDir = Path.Combine(YamlConfigService.GetUserDataDir(), "EBWebView");
                AppLogger.Info("WebViewEnv", $"初始化 CP WebView2 环境（共享目录）: {userDataDir}");
                _cpEnvTask = CoreWebView2Environment.CreateAsync(null, userDataDir);
            }
            return _cpEnvTask;
        }
    }
}
