// ==============================================================================
// 文件: LastResultAutoCloseController.cs
// 命名空间: BlueRandom.Modules.WindowsManager
// 作用: 结果暂留浮窗（LRW）自动关闭计时与状态控制器。
//       解耦 XAML 控件依赖，负责状态管理、防重版本号控制、倒计时调度与完成事件回调。
// ==============================================================================

using System;
using System.Windows.Threading;

namespace BlueRandom.Modules.WindowsManager;

/// <summary>
/// 结果暂留浮窗（LRW）自动关闭控制器。
/// 负责维护版本号、自动关闭时长、计时调度与触发完成回调，完全不直接依赖 XAML 控件。
/// </summary>
internal sealed class LastResultAutoCloseController
{
    private readonly Action<double>? _onProgressTick;
    private readonly Action<int, Action>? _onStartAnimation;
    private readonly Action? _onStopAnimation;
    private readonly Action? _onHideProgress;
    private readonly Action? _onCompleted;

    private DispatcherTimer? _timer;
    private int _version;
    private int _seconds;
    private int _remainingSeconds;

    /// <summary>当前设置的自动关闭时长（秒）。</summary>
    public int Seconds
    {
        get => _seconds;
        set => _seconds = Math.Max(0, value);
    }

    /// <summary>用于取消过期回调的版本号计数。</summary>
    public int Version => _version;

    /// <summary>是否正在进行倒计时调度。</summary>
    public bool IsRunning => _timer?.IsEnabled == true || (_onStartAnimation != null && _seconds > 0);

    /// <summary>
    /// 构造自动关闭控制器（动画回调模式）。
    /// 由 View 提供具体的动画启动、停止与进度条隐藏操作，Controller 负责版本防重与完成调度。
    /// </summary>
    /// <param name="onStartAnimation">启动动画回调：(int seconds, Action onCompleted)。</param>
    /// <param name="onStopAnimation">停止动画回调。</param>
    /// <param name="onHideProgress">隐藏进度条宿主回调。</param>
    /// <param name="onCompleted">自动关闭触发回调（如执行隐藏窗口）。</param>
    public LastResultAutoCloseController(
        Action<int, Action> onStartAnimation,
        Action onStopAnimation,
        Action onHideProgress,
        Action onCompleted)
    {
        _onStartAnimation = onStartAnimation ?? throw new ArgumentNullException(nameof(onStartAnimation));
        _onStopAnimation = onStopAnimation ?? throw new ArgumentNullException(nameof(onStopAnimation));
        _onHideProgress = onHideProgress ?? throw new ArgumentNullException(nameof(onHideProgress));
        _onCompleted = onCompleted ?? throw new ArgumentNullException(nameof(onCompleted));
    }

    /// <summary>
    /// 构造自动关闭控制器（基于 DispatcherTimer 的每秒推进进度模式）。
    /// </summary>
    /// <param name="onProgressTick">每秒进度回调（0.0 ~ 1.0）。</param>
    /// <param name="onCompleted">倒计时完成回调。</param>
    /// <param name="onHideProgress">隐藏进度条回调（可选）。</param>
    public LastResultAutoCloseController(
        Action<double> onProgressTick,
        Action onCompleted,
        Action? onHideProgress = null)
    {
        _onProgressTick = onProgressTick ?? throw new ArgumentNullException(nameof(onProgressTick));
        _onCompleted = onCompleted ?? throw new ArgumentNullException(nameof(onCompleted));
        _onHideProgress = onHideProgress;
    }

    /// <summary>
    /// 启动自动关闭；0 秒表示不自动关闭，此时隐藏进度条。
    /// </summary>
    public void Start(int seconds)
    {
        Stop();

        _seconds = Math.Max(0, seconds);
        if (_seconds <= 0)
        {
            _onHideProgress?.Invoke();
            _onProgressTick?.Invoke(0.0);
            return;
        }

        int version = ++_version;

        if (_onStartAnimation != null)
        {
            _onStartAnimation.Invoke(_seconds, () =>
            {
                if (version != _version) return;
                _onCompleted?.Invoke();
            });
        }
        else if (_onProgressTick != null)
        {
            _remainingSeconds = _seconds;
            _onProgressTick.Invoke(1.0);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) =>
            {
                if (version != _version)
                {
                    _timer?.Stop();
                    _timer = null;
                    return;
                }

                _remainingSeconds--;
                double progress = Math.Max(0.0, (double)_remainingSeconds / _seconds);
                _onProgressTick.Invoke(progress);

                if (_remainingSeconds <= 0)
                {
                    _timer?.Stop();
                    _timer = null;
                    _onCompleted?.Invoke();
                }
            };
            _timer.Start();
        }
    }

    /// <summary>
    /// 使用当前保存的 <see cref="Seconds"/> 启动自动关闭。
    /// </summary>
    public void Start() => Start(_seconds);

    /// <summary>
    /// 停止自动关闭计时并复位，递增版本号取消过期的完成回调。
    /// </summary>
    public void Stop()
    {
        _version++;

        if (_timer != null)
        {
            _timer.Stop();
            _timer = null;
        }

        _onStopAnimation?.Invoke();
    }
}
