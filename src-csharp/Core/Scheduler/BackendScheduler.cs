// ==============================================================================
// 文件: BackendScheduler.cs
// 命名空间: BlueRandom.Core.Scheduler
// 作用: 核心业务仲裁与状态调度引擎（Backend Scheduler）。
//       驱动抽卡流程状态机（互斥锁、12 秒丢帧自愈看门狗）、悬浮窗坐标 600ms 防抖落盘、
//       顶层 Z 序防遮挡探测与配置面板实时热重载广播（ApplyNewProp）。
// ==============================================================================

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
/// 核心后端控制调度器 (Backend Scheduler)。
/// 负责状态机仲裁、抽卡算法调度、双缓存更新、防抖持久化与向下游 UI 分发 GET 指令。
/// </summary>
public static class BackendScheduler
{
    private static readonly object _stateLock = new();
    private static bool _isDrawing = false;
    private static Timer? _debounceSaveTimer;
    private static DateTime _lastOverlaidTime = DateTime.MinValue;

    private static List<CalledStudent> _lastResults = new();
    private static DateTime _lastDrawAt = DateTime.MinValue;

    /// <summary>最近一次抽取结果快照（供结果暂留浮窗与 NativeBridge 读取）。</summary>
    public static IReadOnlyList<CalledStudent> LastResults
    {
        get { lock (_stateLock) { return _lastResults.ToArray(); } }
    }

    /// <summary>最近一次抽取完成的时间。</summary>
    public static DateTime LastDrawAt
    {
        get { lock (_stateLock) { return _lastDrawAt; } }
    }

    /// <summary>
    /// 初始化调度引擎，注册 FCB（悬浮球）、FRW（结果浮窗）、CP（配置面板）的 IPC 监听器。
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

    /// <summary>
    /// 响应悬浮按钮模块（FCB）上报的各类 POST 事件（位置拖拽、抽卡触发、遮挡仲裁、运行期异常）。
    /// </summary>
    /// <param name="msg">来自 FCB 的标准 IPC 消息对象。</param>
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

    /// <summary>
    /// 处理悬浮按钮拖拽移动完成后的坐标变更事件。
    /// 先行以纳秒级速度原子修改内存中的 ConfigCache 快照，并启动 600ms 定时器执行防抖落盘，
    /// 避免在快速连续拖拽过程中高频触发磁盘 YAML 序列化导致卡顿，落盘完成后向配置面板同步最新配置。
    /// </summary>
    /// <param name="monitorNumber">显示器序号（备用扩展字段）。</param>
    /// <param name="x">悬浮球屏幕物理 X 坐标。</param>
    /// <param name="y">悬浮球屏幕物理 Y 坐标。</param>
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

    /// <summary>
    /// 核心抽卡业务触发调度方法。
    /// <para>
    /// 状态机仲裁与流程控制：
    /// 1. 状态锁定：通过 _isDrawing 标志防止连击误触并发抽取；
    ///    内置 12 秒看门狗（Watchdog）：若上一轮抽取演播因前端渲染崩溃或丢包未在 12 秒内回复 WindowClosed，自动解除锁定自愈；
    /// 2. 界面状态流转：向 FCB 发送 HideWindow 指令暂时隐藏悬浮球，让出视觉焦点；
    /// 3. 计算名单：从 ConfigCache 提取当前活跃学生池与去重配置，交由 LotteryEngine 执行加权轮盘赌或 A-Res 采样；
    /// 4. 唤醒展示：向 FRW 发送 ShowWindow 指令并携带结果载荷（FrwShowPayload），拉起抽卡演出浮窗。
    /// </para>
    /// </summary>
    /// <param name="amount">期望抽取学生人数。</param>
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

        // 步骤 2: 隐藏悬浮按钮与上次的结果暂留浮窗，让出视觉焦点
        IpcBroker.Send(IpcMessage.Create(IpcAction.Get, IpcModule.Fcb, "HideWindow"));
        IpcBroker.Send(IpcMessage.Create(IpcAction.Get, IpcModule.Lrw, "HideWindow"));

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

    /// <summary>
    /// 处理悬浮球失去顶层 Z 序或被全屏窗口遮挡的仲裁信号。
    /// 包含 1000ms 本地节流防抖，向 FCB 投递 ShowWindow 强制重置 HWND_TOPMOST。
    /// </summary>
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

    /// <summary>
    /// 响应招募结果展示窗口（FRW）上报的 POST 消息。
    /// 负责捕获动画演播结束关闭事件或渲染未捕获异常。
    /// </summary>
    /// <param name="msg">来自 FRW 模块的 IPC 消息实体。</param>
    private static void OnFrwMessageReceived(IpcMessage msg)
    {
        if (msg.Action != IpcAction.Post) return;

        switch (msg.Command)
        {
            case "WindowClosed":
                HandleFrwWindowClosed(msg.Payload as List<CalledStudent>);
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
                RefreshLastResultWindow();
                break;
        }
    }

    /// <summary>
    /// 招募结果浮窗关闭后的状态收敛处理。
    /// 解除抽取互斥锁定、缓存最近结果，并发送 ShowWindow 唤醒悬浮球恢复桌面交互。
    /// </summary>
    /// <param name="lastStudents">结果浮窗随 WindowClosed 上报的本次抽取名单。</param>
    private static void HandleFrwWindowClosed(List<CalledStudent>? lastStudents)
    {
        AppLogger.Info("Backend", "结果浮窗已关闭，恢复悬浮按钮");
        lock (_stateLock)
        {
            _isDrawing = false;
            if (lastStudents is { Count: > 0 })
            {
                _lastResults = new List<CalledStudent>(lastStudents);
                _lastDrawAt = DateTime.Now;
            }
        }

        // 恢复桌面悬浮球可见性
        IpcBroker.Send(IpcMessage.Create(IpcAction.Get, IpcModule.Fcb, "ShowWindow"));

        // 若开启"结果暂留浮窗"，在屏幕右上角保留本次结果
        RefreshLastResultWindow();
    }

    /// <summary>
    /// 依据当前配置与最近一次抽取结果，同步结果暂留浮窗（LRW）的样式与显隐。
    /// 抽取演出进行中或未开启开关时保持隐藏。
    /// </summary>
    public static void RefreshLastResultWindow()
    {
        var props = ConfigCache.GetFRWProps();
        IpcBroker.Send(IpcMessage.Create(IpcAction.Get, IpcModule.Lrw, "ApplyNewProp", props));

        LrwShowPayload? payload = null;
        if (props.ShowLastResult)
        {
            lock (_stateLock)
            {
                if (!_isDrawing && _lastResults.Count > 0)
                {
                    payload = new LrwShowPayload
                    {
                        CalledStudents = new List<CalledStudent>(_lastResults),
                        DrawnAt = _lastDrawAt
                    };
                }
            }
        }

        if (payload == null)
        {
            IpcBroker.Send(IpcMessage.Create(IpcAction.Get, IpcModule.Lrw, "HideWindow"));
            return;
        }

        AppLogger.Info("Backend", $"显示结果暂留浮窗，共 {payload.CalledStudents.Count} 人");
        IpcBroker.Send(IpcMessage.Create(IpcAction.Get, IpcModule.Lrw, "ShowWindow", payload));
    }

    // ========================================================================
    //  3. 配置面板 (@CP) 消息处理
    // ========================================================================

    /// <summary>
    /// 响应前端配置面板（CP / WebUI）上报的 POST 消息。
    /// 处理用户主动保存配置、恢复出厂配置或前端脚本运行期错误。
    /// </summary>
    /// <param name="msg">来自 CP 模块的 IPC 消息实体。</param>
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

    /// <summary>
    /// 校验、持久化并全量热生效来自前端或外部的新 YAML 配置。
    /// <para>
    /// 执行步骤：
    /// 1. 反序列化语义校验：使用 CamelCase 命名法尝试解析，防止语法损坏；
    /// 2. 班级名单同步：若指定了 activeClassId，自动同步更新 classes_{id}.json 名单仓储；
    /// 3. 持久化落盘：将配置写入 config.yaml；
    /// 4. 内存快照热更新：更新 ConfigCache.Current 并规范化字段边界；
    /// 5. 热重载广播（ApplyNewProp）：向 FCB 和 FRW 发送更新通知，实现无需重启界面实时变动；
    /// 6. 容错回滚：若校验或写入抛出异常，自动读取 ConfigCache 中的旧有效配置并通过 OverwriteConfig 回滚前端输入框。
    /// </para>
    /// </summary>
    /// <param name="yaml">前端传来的完整 YAML 文本内容。</param>
    /// <returns>保存并应用成功返回 true，发生异常回滚返回 false。</returns>
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

            // 4. 更新内存 ConfigCache
            ConfigCache.Current = YamlConfigService.Normalize(newConfig);
            AppLogger.Info("Backend", "已从配置面板应用新配置并刷新 ConfigCache");

            // 5. 热重载广播下发
            IpcBroker.Send(IpcMessage.Create(IpcAction.Get, IpcModule.Fcb, "ApplyNewProp", ConfigCache.GetFCBProps()));
            IpcBroker.Send(IpcMessage.Create(IpcAction.Get, IpcModule.Frw, "ApplyNewProp", ConfigCache.GetFRWProps()));
            RefreshLastResultWindow();
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

    /// <summary>
    /// 响应前端 ConfigSaved 指令的适配器方法。
    /// </summary>
    /// <param name="yaml">待应用的 YAML 文本。</param>
    private static void HandleCpConfigSaved(string? yaml)
    {
        SaveAndApplyConfig(yaml);
    }

    /// <summary>
    /// 处理前端发起的恢复出厂默认设置请求。
    /// 将 config.yaml 覆写为初始模板，唤起新实例进程并退出当前进程以完成干净重置。
    /// </summary>
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
