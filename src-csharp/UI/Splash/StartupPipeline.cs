using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using BlueRandom.Core.Config;
using BlueRandom.Core.Ipc;
using BlueRandom.Core.Logging;
using BlueRandom.Core.Resources;
using BlueRandom.Core.Scheduler;
using BlueRandom.Services;

namespace BlueRandom.UI.Splash;

/// <summary>
/// 软件启动流水线 (Startup Pipeline)
/// 严格执行 Stage 0 至 Stage 5 的原子初始化与自愈校验
/// </summary>
public static class StartupPipeline
{
    public static async Task<bool> RunAsync(SplashWindow splash, string[] args)
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string userDataDir = YamlConfigService.GetUserDataDir();

        // -------------------------------------------------------------
        // Stage 0: 启动 (0% -> 10%)
        // -------------------------------------------------------------
        splash.UpdateProgress(5, "正在初始化蔚蓝点名...");
        AppLogger.Initialize(userDataDir);
        AppLogger.Info("Pipeline", $"Blue Random 启动，运行基目录: {baseDir}");
        await Task.Delay(180); // 平滑视觉过渡
        splash.UpdateProgress(10, "初始化应用程序结束");

        // -------------------------------------------------------------
        // Stage 1: 权限 (15% -> 35%)
        // -------------------------------------------------------------
        splash.UpdateProgress(15, "正在检查系统运行权限...");
        await Task.Delay(150);

        // 先预读轻量配置以判断是否开启了提权开关（不触发写回迁移）
        bool requireAdmin = false;
        bool requireUia = false;
        try
        {
            string cfgPath = YamlConfigService.GetConfigFilePath();
            if (File.Exists(cfgPath))
            {
                var tempCfg = YamlConfigService.LoadConfig(autoMigrate: false);
                requireAdmin = tempCfg.Admin.RequireAdminOnLaunch;
                requireUia = tempCfg.Admin.UiAccessEnabled;
            }
        }
        catch
        {
            // 忽略临时异常，留待 Stage 2 完整处理
        }

        // 1. 管理员提权检查 (UIAccess 必须依赖管理员权限前置，若开启 UIA 也一并申请管理员)
        if (WindowsSystemService.HandleAdminElevation(args, requireAdmin || requireUia))
        {
            AppLogger.Info("Pipeline", "已发起管理员提权重启尝试");
            splash.UpdateProgress(20, "正在以管理员权限重启应用...");
            Application.Current.Shutdown();
            return false;
        }

        splash.UpdateProgress(25, "正在校验 UIAccess 置顶权限...");
        await Task.Delay(150);

        // 2. UIAccess 提权检查
        if (WindowsSystemService.HandleUiAccessElevation(args, requireUia))
        {
            splash.UpdateProgress(30, "正在以 UIAccess 权限重启应用...");
            AppLogger.Info("Pipeline", "已发起 UIAccess 提权，当前启动器进程即将退出");
            Application.Current.Shutdown();
            return false;
        }

        splash.UpdateProgress(35, "系统权限验证完成");

        // -------------------------------------------------------------
        // Stage 2: 配置 (40% -> 55%)
        // -------------------------------------------------------------
        splash.UpdateProgress(40, "正在校验配置协议与名单数据...");
        await Task.Delay(150);

        try
        {
            YamlConfigService.EnsureConfigFileExists();

            // 若检测到配置协议版本不同，Initializer 自动进入协议变更迁移并提示用户
            if (YamlConfigService.IsProtocolMigrationNeeded())
            {
                splash.UpdateProgress(45, $"检测到配置协议变更，正在自动迁移至 [{Core.AppVersion.Codename}]...");
                await Task.Delay(250);
            }

            var config = YamlConfigService.LoadConfig(autoMigrate: true);
            ConfigCache.Current = config;
            AppLogger.Info("Pipeline", $"配置已成功注入 ConfigCache (协议代号: {config.ProtocolVersion})，候选学生数: {config.StudentList.Count}");
            ClassManagerService.EnsureInitialized(ConfigCache.Current);
            AppLogger.Info("Pipeline", $"配置与班级数据已成功装载，候选学生数: {ConfigCache.Current.StudentList.Count}，当前班级: {ConfigCache.Current.ActiveClassId}");
            splash.UpdateProgress(55, "配置文件初始化完成！");
        }
        catch (Exception ex)
        {
            splash.SetErrorState("配置文件损坏，无法解析！");
            AppLogger.Error("Pipeline", "主配置文件损坏", ex);

            var result = MessageBox.Show(
                "检测到 config.yaml 格式存在严重损坏，是否还原为默认配置并继续运行？\n（选“否”将退出程序以手动修复）",
                "配置解析错误 - Blue Random",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                YamlConfigService.WriteDefaultConfig(YamlConfigService.GetConfigFilePath());
                ConfigCache.Current = YamlConfigService.LoadConfig();
                ClassManagerService.EnsureInitialized(ConfigCache.Current);
                AppLogger.Info("Pipeline", "用户已选择重置为默认配置模板");
            }
            else
            {
                Application.Current.Shutdown();
                return false;
            }
        }

        splash.UpdateProgress(70, "配置与名单装载就绪");

        // -------------------------------------------------------------
        // Stage 3: 资源 (75% -> 80%)
        // -------------------------------------------------------------
        splash.UpdateProgress(75, "正在预载入美术资源与字体...");
        await Task.Run(() => ResourceCache.Preload(baseDir));
        await Task.Delay(150);
        splash.UpdateProgress(80, "核心素材预热完成");

        // -------------------------------------------------------------
        // Stage 4: 派发 (80% -> 95%)
        // -------------------------------------------------------------
        splash.UpdateProgress(85, "正在启动核心调度器与总线...");
        IpcBroker.Initialize();
        BackendScheduler.Initialize();
        await Task.Delay(120);

        splash.UpdateProgress(90, "正在唤醒各模块独立 UI 线程...");
        WindowManager.InitializeAllThreads();
        await Task.Delay(150);
        splash.UpdateProgress(95, "系统组件已挂载");

        // -------------------------------------------------------------
        // Stage 5: 就绪 (95% -> 100%)
        // -------------------------------------------------------------
        splash.UpdateProgress(100, "欢迎回来，老师！");
        await Task.Delay(500);

        // 退出 Splash 窗口
        await splash.FadeOutAndCloseAsync();
        AppLogger.Info("Pipeline", "启动加载器完成使命，控制权已移交核心总线");
        return true;
    }
}
