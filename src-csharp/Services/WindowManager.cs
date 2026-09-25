using System;
using System.Threading;
using System.Windows.Threading;
using BlueRandom.Core.Logging;
using BlueRandom.UI.FCB;
using BlueRandom.UI.FRW;
using BlueRandom.UI.CP;

namespace BlueRandom.Services;

/// <summary>
/// 多线程窗口调度管理器 (Window Manager)
/// 负责为 FCB、FRW、CP 分配独立的 STA 线程与消息循环
/// </summary>
public static class WindowManager
{
    private static Thread? _fcbThread;
    private static Thread? _frwThread;
    private static Thread? _cpThread;

    public static void InitializeAllThreads()
    {
        AppLogger.Info("WinMgr", "正在拉起各模块独立 UI 线程...");

        // 1. 悬浮按钮 (FCB) 独立线程
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

        // 2. 结果浮窗 (FRW) 独立线程
        _frwThread = new Thread(() =>
        {
            try
            {
                var frwWin = new ResultWindow();
                // 启动时隐藏，等待 ShowWindow 唤醒
                Dispatcher.Run();
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

        // 3. 配置面板 (CP) 独立线程
        _cpThread = new Thread(() =>
        {
            try
            {
                var cpWin = new ConfigPanelWindow();
                // 在消息泵启动后低优先级预热 WebView2
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        await cpWin.EnsureReadyAsync();
                    }
                    catch { }
                }), DispatcherPriority.ApplicationIdle);

                // 启动消息循环
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                AppLogger.Error("WinMgr", "CP 线程崩溃", ex);
            }
        })
        {
            Name = "CP_UI_Thread",
            IsBackground = true
        };
        _cpThread.SetApartmentState(ApartmentState.STA);
        _cpThread.Start();

        // 4. 初始化系统托盘图标
        TrayService.Initialize();

        AppLogger.Info("WinMgr", "所有 UI 线程与托盘已成功挂载");
    }
}
