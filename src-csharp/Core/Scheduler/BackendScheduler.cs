using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BlueRandom.Core.Config;
using BlueRandom.Core.Ipc;
using BlueRandom.Core.Logging;
using BlueRandom.Core.Lottery;
using BlueRandom.Services;

namespace BlueRandom.Core.Scheduler;

/// <summary>
/// 核心后端控制调度器 (Backend Scheduler)
/// 负责状态机仲裁、抽卡算法调度、双缓存更新、防抖持久化与向下游 UI 分发 GET 指令
/// </summary>
public static class BackendScheduler
{
    private static readonly object _stateLock = new();
    private static bool _isDrawing = false;
    private static Timer? _debounceSaveTimer;
    private static DateTime _lastOverlaidTime = DateTime.MinValue;

    /// <summary>
    /// 初始化调度器并订阅下游组件 POST 上报
    /// </summary>
    public static void Initialize()
    {
        AppLogger.Info("Backend", "后端核心控制调度器初始化...");

        // 订阅 FCB 上报
        IpcBroker.Subscribe(IpcModule.Fcb, OnFcbMessageReceived);

        // 订阅 FRW 上报
        IpcBroker.Subscribe(IpcModule.Frw, OnFrwMessageReceived);

        // 订阅 CP 上报
        IpcBroker.Subscribe(IpcModule.Cp, OnCpMessageReceived);
    }

    // ========================================================================
    //  1. 悬浮按钮 (@FCB) 消息处理
    // ========================================================================

    private static void OnFcbMessageReceived(IpcMessage msg)
    {
        if (msg.Action != IpcAction.Post) return;

        switch (msg.Command)
        {
            case "PositionChanged":
                if (msg.Payload is FcbPositionPayload pos)
                {
                    HandleFcbPositionChanged(pos.MonitorNumber, pos.PositionX, pos.PositionY);
                }
                break;

            case "RandomTriggered":
                int amount = msg.Payload is int a ? a : 1;
                HandleFcbRandomTriggered(amount);
                break;

            case "WindowOverlaid":
                HandleFcbWindowOverlaid();
                break;

            case "ErrorHappened":
                string detail = msg.Payload?.ToString() ?? "未知异常";
                AppLogger.Error("Backend", $"[FCB 异常上报]: {detail}");
                break;
        }
    }

    private static void HandleFcbPositionChanged(int monitorNumber, int x, int y)
    {
        // 1. 更新内存配置缓存
        ConfigCache.Mutate(cfg =>
        {
            cfg.FloatingButton.Position.X = x;
            cfg.FloatingButton.Position.Y = y;
        });

        // 2. 防抖落盘 (500ms~1000ms)
        lock (_stateLock)
        {
            _debounceSaveTimer?.Dispose();
            _debounceSaveTimer = new Timer(_ =>
            {
                try
                {
                    YamlConfigService.SaveConfig(ConfigCache.Current);
                    // 触发 @CP.FetchConfig 同步最新配置给 WebUI
                    string yaml = YamlConfigService.ToYamlWithComments(ConfigCache.Current);
                    IpcBroker.Send(IpcMessage.Create(IpcAction.Get, IpcModule.Cp, "FetchConfig", yaml));
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Backend", "防抖写入配置异常", ex);
                }
            }, null, 600, Timeout.Infinite);
        }
    }

    private static DateTime _drawStartTime = DateTime.MinValue;

    private static void HandleFcbRandomTriggered(int amount)
    {
        lock (_stateLock)
        {
            // 步骤 1: 并发与状态拦截 (带 12 秒自愈看门狗，防异常丢帧永久卡死)
            if (_isDrawing && (DateTime.UtcNow - _drawStartTime).TotalSeconds < 12)
            {
                AppLogger.Warn("Backend", "当前已有抽取演播在进行中，丢弃重复抽取请求");
                return;
            }
            _isDrawing = true;
            _drawStartTime = DateTime.UtcNow;
        }

        AppLogger.Info("Backend", $"触发抽取学员，请求人数: {amount}");

        // 步骤 2: 隐藏悬浮按钮
        IpcBroker.Send(IpcMessage.Create(IpcAction.Get, IpcModule.Fcb, "HideWindow"));

        // 步骤 3: 执行核心加权抽卡算法
        var pool = ConfigCache.GetStudentPool();
        bool allowDuplicate = ConfigCache.Current.AllowRepeatDraw;
        var calledStudents = LotteryEngine.Pick(pool, amount, allowDuplicate);

        AppLogger.Info("Backend", $"抽卡算法计算完成，实际抽取: {calledStudents.Count} 人");

        // 步骤 4: 唤醒并分发给招募结果浮窗
        var showPayload = new FrwShowPayload
        {
            Amount = calledStudents.Count,
            CalledStudents = calledStudents
        };
        IpcBroker.Send(IpcMessage.Create(IpcAction.Get, IpcModule.Frw, "ShowWindow", showPayload));
    }

    private static void HandleFcbWindowOverlaid()
    {
        // 1 秒节流，防止高频抢占焦点
        if ((DateTime.Now - _lastOverlaidTime).TotalMilliseconds < 1000)
        {
            return;
        }
        _lastOverlaidTime = DateTime.Now;

        AppLogger.Info("Backend", "检测到悬浮窗被遮挡，触发置顶刷新仲裁");
        // 向 FCB 发送刷新置顶指令
        IpcBroker.Send(IpcMessage.Create(IpcAction.Get, IpcModule.Fcb, "ShowWindow"));
    }

    // ========================================================================
    //  2. 招募结果浮窗 (@FRW) 消息处理
    // ========================================================================

    private static void OnFrwMessageReceived(IpcMessage msg)
    {
        if (msg.Action != IpcAction.Post) return;

        switch (msg.Command)
        {
            case "WindowClosed":
                HandleFrwWindowClosed();
                break;

            case "ErrorHappened":
                string detail = msg.Payload?.ToString() ?? "未知异常";
                AppLogger.Error("Backend", $"[FRW 异常上报]: {detail}");
                // 紧急自愈重置
                lock (_stateLock)
                {
                    _isDrawing = false;
                }
                IpcBroker.Send(IpcMessage.Create(IpcAction.Get, IpcModule.Fcb, "ShowWindow"));
                break;
        }
    }

    private static void HandleFrwWindowClosed()
    {
        AppLogger.Info("Backend", "结果浮窗已关闭，恢复悬浮按钮");
        lock (_stateLock)
        {
            _isDrawing = false;
        }

        // 恢复桌面悬浮球可见性
        IpcBroker.Send(IpcMessage.Create(IpcAction.Get, IpcModule.Fcb, "ShowWindow"));
    }

    // ========================================================================
    //  3. 配置面板 (@CP) 消息处理
    // ========================================================================

    private static void OnCpMessageReceived(IpcMessage msg)
    {
        if (msg.Action != IpcAction.Post) return;

        switch (msg.Command)
        {
            case "ConfigSaved":
                string? yaml = msg.Payload as string;
                HandleCpConfigSaved(yaml);
                break;

            case "ConfigReset":
                HandleCpConfigReset();
                break;

            case "ErrorHappened":
                string detail = msg.Payload?.ToString() ?? "未知异常";
                AppLogger.Error("Backend", $"[CP 异常上报]: {detail}");
                break;
        }
    }

    public static bool SaveAndApplyConfig(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return false;

        try
        {
            // 1. 尝试反序列化校验
            var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
                .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            var newConfig = deserializer.Deserialize<RootConfig>(yaml);
            if (newConfig == null) throw new InvalidOperationException("反序列化结果为空");

            // 2. 持久化到本地 config.yaml
            // 2. 如果指定了当前激活班级，同步更新该班级的名单文件
            if (!string.IsNullOrWhiteSpace(newConfig.ActiveClassId))
            {
                var targetClass = ClassManagerService.LoadClass(newConfig.ActiveClassId);
                if (targetClass != null)
                {
                    targetClass.StudentList = newConfig.StudentList ?? new();
                    ClassManagerService.SaveClass(targetClass);
                    AppLogger.Info("Backend", $"已同步更新当前班级名单文件: {targetClass.Name} ({targetClass.ClassId})");
                }
            }

            // 3. 持久化到本地 config.yaml
            YamlConfigService.SaveConfig(newConfig);

            // 3. 更新内存 ConfigCache
            // 4. 更新内存 ConfigCache
            ConfigCache.Current = YamlConfigService.Normalize(newConfig);
            AppLogger.Info("Backend", "已从配置面板应用新配置并刷新 ConfigCache");

            // 4. 热重载广播下发
            // 5. 热重载广播下发
            IpcBroker.Send(IpcMessage.Create(IpcAction.Get, IpcModule.Fcb, "ApplyNewProp", ConfigCache.GetFCBProps()));
            IpcBroker.Send(IpcMessage.Create(IpcAction.Get, IpcModule.Frw, "ApplyNewProp", ConfigCache.GetFRWProps()));
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Backend", "配置保存校验失败，下发回滚覆写", ex);
            // 校验失败：将缓存中的旧配置覆写给前端
            string fallbackYaml = YamlConfigService.ToYamlWithComments(ConfigCache.Current);
            IpcBroker.Send(IpcMessage.Create(IpcAction.Get, IpcModule.Cp, "OverwriteConfig", fallbackYaml));
            return false;
        }
    }

    private static void HandleCpConfigSaved(string? yaml)
    {
        SaveAndApplyConfig(yaml);
    }

    private static void HandleCpConfigReset()
    {
        AppLogger.Info("Backend", "收到恢复默认配置请求，准备覆写并重启");
        try
        {
            string cfgPath = YamlConfigService.GetConfigFilePath();
            YamlConfigService.WriteDefaultConfig(cfgPath);

            // 自重启流程
            string exePath = Environment.ProcessPath ?? "";
            if (!string.IsNullOrEmpty(exePath))
            {
                Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true });
            }

            // 安全关闭
            IpcBroker.Send(IpcMessage.Create(IpcAction.Dest, IpcModule.Ipc, "Destroy"));
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Backend", "恢复默认配置异常", ex);
        }
    }
}
