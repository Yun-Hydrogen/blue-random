// ==============================================================================
// 文件: WebViewEnvironmentManager.cs
// 命名空间: BlueRandom.Services
// 作用: WebView2 运行时环境管理器（按消费者隔离）。
//
// 架构说明（2026-10-17 重构）：
//   1. CP（配置面板）与 FRW（结果浮窗）使用**完全独立的 CoreWebView2Environment**，
//      并指定不同的 userDataFolder。WebView2 以 userDataFolder 为粒度创建浏览器进程，
//      因此 FRW 被拆分为独立的 Web 进程（独立渲染/GPU/音频进程），
//      彻底隔离配置面板的清缓存、DevTools、后台节流对结果浮窗的影响。
//   2. FRW 环境额外注入浏览器参数：
//      - --use-gl=angle + --use-angle=d3d9
//         结果浮窗固定使用 ANGLE D3D9 渲染后端，规避 UIAccess 模式下
//         D3D11/硬件合成链路导致的内容不可见问题。
//      - --autoplay-policy=no-user-gesture-required
//         招募音效/BGM 在无用户手势（窗口 Show 后自动播放）时不被 Chromium 拦截。
//      - --disable-background-timer-throttling
//        --disable-renderer-backgrounding
//        --disable-backgrounding-occluded-windows
//        --disable-features=CalculateNativeWinOcclusion
//         结果窗采用 Show-Then-Hide 预热 + 全屏透明置顶，
//         Chromium 会因“窗口不可见/被遮挡”节流定时器与渲染，导致动画卡住/音频不播。
//   3. 两个环境在不同 STA 线程中初始化（CP: WebUI_Thread，FRW: FRW_UI_Thread），
//      避免 COM 套间与 userDataFolder 互斥锁冲突。
// ==============================================================================

using System;
using System.IO;
using System.Threading.Tasks;
using BlueRandom.Core.Logging;
using Microsoft.Web.WebView2.Core;

namespace BlueRandom.Services;

/// <summary>
/// WebView2 运行时环境管理器。
/// CP 与 FRW 各自持有独立环境与独立 userDataFolder，运行于独立 Web 进程。
/// </summary>
public static class WebViewEnvironmentManager
{
    private static Task<CoreWebView2Environment>? _cpEnvTask;
    private static Task<CoreWebView2Environment>? _frwEnvTask;
    private static readonly object _lock = new();

    /// <summary>
    /// FRW 专用浏览器参数：
    ///   - --use-gl=angle + --use-angle=d3d9：指定 ANGLE 使用 D3D9 后端渲染
    ///     （UIAccess 模式下 DWM 对 D3D11/硬件合成路径限制较多，D3D9 兼容性更好）；
    ///   - --autoplay-policy=no-user-gesture-required：允许招募音效/BGM 自动播放；
    ///   - 禁用后台节流/遮挡检测：Show-Then-Hide 预热窗口不暂停定时器与渲染。
    /// </summary>
    private const string FrwBrowserArguments =
        "--use-gl=angle " +
        "--use-angle=d3d9 " +
        "--autoplay-policy=no-user-gesture-required " +
        "--disable-background-timer-throttling " +
        "--disable-renderer-backgrounding " +
        "--disable-backgrounding-occluded-windows " +
        "--disable-features=CalculateNativeWinOcclusion";

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

    /// <summary>
    /// 获取结果浮窗（FRW）使用的独立 WebView2 环境。
    /// <para>
    /// 使用独立 userDataFolder（EBWebView_FRW）使 WebView2 启动独立的浏览器进程，
    /// 与配置面板的 Web 进程完全隔离；并附加自动播放/禁用后台节流参数。
    /// </para>
    /// </summary>
    /// <returns>FRW 专用 CoreWebView2Environment 实例任务。</returns>
    public static Task<CoreWebView2Environment> GetFrwEnvironmentAsync()
    {
        lock (_lock)
        {
            if (_frwEnvTask == null)
            {
                string userDataDir = Path.Combine(YamlConfigService.GetUserDataDir(), "EBWebView_FRW");

                /*
                 * 渲染后端：FRW 固定使用 ANGLE D3D9（--use-gl=angle --use-angle=d3d9），
                 * 以规避 UIAccess 模式下 D3D11/硬件合成链路不可见的问题。
                 * UIAccess 状态下额外记录日志便于排查。
                 */
                string browserArgs = FrwBrowserArguments;
                bool isUiAccess = WindowsSystemService.IsProcessUiAccess();
                if (isUiAccess)
                {
                    AppLogger.Info("WebViewEnv", "检测到 UIAccess 模式：FRW 使用 ANGLE D3D9 渲染后端");
                }

                AppLogger.Info("WebViewEnv", $"初始化 FRW 独立 WebView2 环境: {userDataDir} (args: {browserArgs})");
                var options = new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments = browserArgs
                };
                _frwEnvTask = CoreWebView2Environment.CreateAsync(null, userDataDir, options);
            }
            return _frwEnvTask;
        }
    }

    /// <summary>
    /// 兼容旧调用：默认返回配置面板环境。
    /// </summary>
    /// <returns>CoreWebView2Environment 实例任务。</returns>
    public static Task<CoreWebView2Environment> GetEnvironmentAsync() => GetCpEnvironmentAsync();
}
