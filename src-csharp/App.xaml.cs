using System;
using System.Windows;
using BlueRandom.Core.Logging;
using BlueRandom.Core.Resources;
using BlueRandom.UI.Splash;

namespace BlueRandom;

public partial class App : Application
{
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
