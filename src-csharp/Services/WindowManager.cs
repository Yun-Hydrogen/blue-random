// ==============================================================================
// 文件: WindowManager.cs
// 命名空间: BlueRandom.Services
// 作用: 现代化多线程窗口调度管理器（Window Manager）。
//       采用多 STA（Single Thread Apartment）隔离架构，为桌面悬浮球（FCB）、
//       抽卡演出窗口（FRW）和 WebUI 配置面板（CP）分配独立的线程和 WPF Dispatcher 消息循环，
//       并结合 ApplicationIdle 优先级执行 WebView2 异步预热。
// ==============================================================================

using System;
using System.Threading;
using System.Windows.Threading;
using BlueRandom.Core.Logging;
using BlueRandom.UI.FCB;
using BlueRandom.UI.FRW;
using BlueRandom.UI.CP;
using BlueRandom.UI.LRW;

namespace BlueRandom.Services;

/// <summary>
/// 多线程窗口调度管理器 (Window Manager)。
/// 负责为 FCB、FRW、LRW、CP 分配独立的 STA 线程与消息循环，实现各 UI 模块之间的渲染负载与卡顿隔离。
/// </summary>
public static class WindowManager
{
    private static Thread? _fcbThread;
    private static Thread? _frwThread;
    private static Thread? _webUiThread;
    private static Thread? _lrwThread;
    private static LastResultWindow? _lrwWindow;

    /// <summary>
    /// 并发拉起悬浮球 (FCB)、结果浮窗 (FRW)、结果暂留浮窗 (LRW) 与配置面板 (CP) 的独立 STA UI 线程，并启动系统托盘监听。
    /// <para>
    /// 架构隔离与线程安全优势：
    /// 1. FCB 线程专职监听鼠标拖拽与 60FPS 呼吸/旋转动画，不受任何复杂渲染或 Chromium 引擎阻塞；
    /// 2. FRW 线程承载 DirectComposition 原生结果窗（DCompResultWindow），自有 Win32 消息循环；
    /// 3. CP 线程是唯一承载 WebView2 的 UI 线程（EBWebView 目录）；
    /// 4. 严格遵守 COM 单线程套间 (STA) 规则，CoreWebView2Environment 只在其所属线程调度，
    ///    根除 0x80010106 / InvalidOperationException 与 userDataFolder 互斥锁冲突；
    /// 5. CP 启动时不预热（首次打开时初始化）；FRW 仅通过 EnsureHandle 建立 Win32 HWND，
    ///    绝不调用 Show()，杜绝界面闪烁。
    /// </para>
    /// </summary>
    public static void InitializeAllThreads()
    {
        AppLogger.Info("WinMgr", "正在拉起各模块独立 UI 线程...");

        // 1. 悬浮按钮 (FCB) 独立线程 (纯原生 WPF 高刷新率)
        _fcbThread = new Thread(() =>
        {
            try
            {
                var fcbWin = new FloatingButtonWindow();
                fcbWin.Show();
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                AppLogger.Error("WinMgr", "FCB 线程崩溃", ex);
            }
        })
        {
            Name = "FCB_UI_Thread",
            IsBackground = true
        };
        _fcbThread.SetApartmentState(ApartmentState.STA);
        _fcbThread.Start();

        // 2. 结果浮窗 (FRW) 独立线程：DirectComposition + D3D11 + D2D 透明窗口
        _frwThread = new Thread(() =>
        {
            try
            {
                var frwWin = new DCompResultWindow();
                frwWin.Run();
            }
            catch (Exception ex)
            {
                AppLogger.Error("WinMgr", "FRW 线程崩溃", ex);
            }
        })
        {
            Name = "FRW_UI_Thread",
            IsBackground = true
        };
        _frwThread.SetApartmentState(ApartmentState.STA);
        _frwThread.Start();

        // 3. 配置面板 (CP) 独立线程（首次打开时初始化 WebView2，不做启动预热）
        _webUiThread = new Thread(() =>
        {
            try
            {
                var cpWin = new ConfigPanelWindow();
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                AppLogger.Error("WinMgr", "CP 线程崩溃", ex);
            }
        })
        {
            Name = "WebUI_Thread",
            IsBackground = true
        };
        _webUiThread.SetApartmentState(ApartmentState.STA);
        _webUiThread.Start();

        // 4. 结果暂留浮窗 (LRW) 独立线程：按需显示在屏幕右上角
        _lrwThread = new Thread(() =>
        {
            try
            {
                _lrwWindow = new LastResultWindow();
                AppLogger.Info("WinMgr", "LRW 结果暂留浮窗线程就绪");
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                AppLogger.Error("WinMgr", "LRW 线程崩溃", ex);
            }
        })
        {
            Name = "LRW_UI_Thread",
            IsBackground = true
        };
        _lrwThread.SetApartmentState(ApartmentState.STA);
        _lrwThread.Start();

        // 5. 初始化系统托盘图标
        TrayService.Initialize();

        AppLogger.Info("WinMgr", "所有 UI 线程与托盘已成功挂载");
    }
}
