// ==============================================================================
// 文件: App.xaml.cs
// 命名空间: BlueRandom
// 作用: WPF 应用程序入口宿主类，管理应用生命周期、顶层未捕获异常兜底，
//       并触发启动阶段前置资源准备（EarlyInit）与核心启动流水线（StartupPipeline）。
// ==============================================================================

using System;
using System.Windows;
using BlueRandom.Core.Logging;
using BlueRandom.Core.Resources;
using BlueRandom.UI.Splash;

namespace BlueRandom;

/// <summary>
/// 应用程序全局单例宿主，承载整个进程的 WPF 生命周期。
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 应用程序启动入口。
    /// 负责注册全局异常捕获钩子、执行阶段 -1 静态资源早熟加载（确保启动窗口无字体缺失闪烁），
    /// 展示纯原生启动屏幕（SplashWindow），并调度六阶段异步启动流水线。
    /// </summary>
    /// <param name="e">包含命令行参数的启动事件参数对象。</param>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                AppLogger.Error("App", "全局未捕获异常", ex);
            }
        };

        // 阶段 -1 极速硬加载：立即解压注册 UI.ttf 字体与应用 Logo，确保启动器窗口即刻拥有自定义字体与高清徽标
        ResourceCache.EarlyInit(AppDomain.CurrentDomain.BaseDirectory);

        // 实例化原生启动器窗口
        var splash = new SplashWindow();
        splash.Show();

        // 启动 Stage 0 至 Stage 5 初始化流水线
        bool ok = await StartupPipeline.RunAsync(splash, e.Args);
        if (!ok)
        {
            AppLogger.Info("App", "启动流水线提前退出");
            return;
        }

        AppLogger.Info("App", "应用主循环保持运行中");
    }
}
