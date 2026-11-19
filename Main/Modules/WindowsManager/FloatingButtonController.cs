// ==============================================================================
// 文件: FloatingButtonController.cs
// 命名空间: BlueRandom.Modules.WindowsManager
// 作用: 悬浮按钮（FCB: Floating Control Button）控制器。
//       托管悬浮球的业务状态、外观配置归一化、图标解析、位置计算、
//       抽取人数边界夹取以及与后端抽卡调度的 IPC 确认触发。
// ==============================================================================

using System;
using System.Windows;
using System.Windows.Media;

namespace BlueRandom.Modules.WindowsManager;

/// <summary>
/// 悬浮按钮业务状态与控制逻辑控制器。
/// </summary>
internal sealed class FloatingButtonController
{
    private readonly FloatingButtonWindow _view;

    /// <summary>
    /// 当前选中的抽取人数 (1-10)。
    /// </summary>
    public int PickCount { get; private set; } = 1;

    /// <summary>
    /// 人数选择胶囊当前是否正处于展开状态。
    /// </summary>
    public bool IsPickerOpen { get; set; } = false;

    /// <summary>
    /// 当前生效的悬浮按钮配置数据对象。
    /// </summary>
    public FloatingButtonConfig? Props { get; private set; }

    /// <summary>
    /// 初始化悬浮按钮控制器实例并关联宿主视图。
    /// </summary>
    /// <param name="view">悬浮按钮窗口视图。</param>
    public FloatingButtonController(FloatingButtonWindow view)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
    }

    /// <summary>
    /// 规范化并应用按钮外观配置，执行业务参数解析与坐标规划后交付视图呈现。
    /// </summary>
    /// <param name="props">悬浮按钮配置。</param>
    public void ApplyProps(FloatingButtonConfig props)
    {
        if (!_view.Dispatcher.CheckAccess())
        {
            _view.Dispatcher.Invoke(() => ApplyProps(props));
            return;
        }

        try
        {
            Props = props;

            // 1. 尺寸计算与缩放比例 (基准 50px)
            double scale = props.SizePercent / 100.0;
            double baseSize = 50.0 * scale;

            // 2. 透明度与置顶
            double opacity = Math.Clamp(1.0 - (props.TransparencyPercent / 100.0), 0.1, 1.0);
            bool topmost = props.AlwaysOnTop;
            bool showInTaskbar = props.ShowInTaskbar;

            // 3. 边框颜色 (默认使用经典天蓝色 #66CCFF)
            Color borderColor = Color.FromRgb(0x66, 0xCC, 0xFF);
            if (!string.IsNullOrEmpty(props.BorderColor) && props.BorderColor.ToLowerInvariant() != "#ffffff")
            {
                try
                {
                    borderColor = (Color)ColorConverter.ConvertFromString(props.BorderColor);
                }
                catch { }
            }

            // 4. 图标尺寸与解析（优先加载自定义图标，回退内置图标）
            double iconSize = (props.IconSize > 0 ? props.IconSize : 40.0) * scale;
            ImageSource? iconImg = CustomIconLoader.ResolveButtonIcon(props.CustomIconId);

            // 5. 初始坐标计算
            double left;
            double top;
            if (props.Position.X.HasValue && props.Position.Y.HasValue)
            {
                left = props.Position.X.Value;
                top = props.Position.Y.Value;
            }
            else
            {
                // 默认停靠在右下角
                double padX = (_view.Width - baseSize) / 2.0;
                double padY = (_view.Height - baseSize) / 2.0;
                left = SystemParameters.WorkArea.Right - _view.Width + padX - 30;
                top = SystemParameters.WorkArea.Bottom - _view.Height + padY - 80;
            }

            _view.ApplyVisualProps(baseSize, opacity, topmost, showInTaskbar, borderColor, iconSize, iconImg, left, top);
        }
        catch (Exception ex)
        {
            AppLogger.Error("FCB", "应用按钮样式异常", ex);
        }
    }

    /// <summary>
    /// 设置并夹取抽取人数至合法范围 [1, 10]，随后通知视图刷新呈现与动效。
    /// </summary>
    /// <param name="count">目标人数。</param>
    public void SetPickCount(int count)
    {
        if (!_view.Dispatcher.CheckAccess())
        {
            _view.Dispatcher.Invoke(() => SetPickCount(count));
            return;
        }

        PickCount = Math.Clamp(count, 1, 10);
        _view.ApplyPickCountVisual(PickCount);
    }

    /// <summary>
    /// 人数步进调整。
    /// </summary>
    /// <param name="delta">步进步长（正数为增加，负数为减少）。</param>
    public void StepPickCount(int delta)
    {
        SetPickCount(PickCount + delta);
    }

    /// <summary>
    /// 应用默认抽取人数；当选择胶囊正处于展开状态时不打断用户当前的交互调整。
    /// </summary>
    /// <param name="count">配置设定的默认人数。</param>
    public void ApplyDefaultPickCount(int count)
    {
        if (!_view.Dispatcher.CheckAccess())
        {
            _view.Dispatcher.Invoke(() => ApplyDefaultPickCount(count));
            return;
        }

        if (!IsPickerOpen)
        {
            SetPickCount(count);
        }
    }

    /// <summary>
    /// 触发确认抽取：上报 RandomTriggered 事件给核心后端调度器。
    /// </summary>
    public void ConfirmPick()
    {
        IpcBroker.Send(IpcMessage.Create(IpcAction.Post, IpcModule.Fcb, "RandomTriggered", PickCount));
    }

    /// <summary>
    /// 更新选择器展开状态。
    /// </summary>
    /// <param name="open">展开状态。</param>
    public void SetPickerOpen(bool open)
    {
        IsPickerOpen = open;
    }
}
