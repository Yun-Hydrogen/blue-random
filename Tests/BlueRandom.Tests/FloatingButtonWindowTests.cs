// ============================================================================
// 文件名称: FloatingButtonWindowTests.cs
// 项目名称: BlueRandom.Tests
// 文件说明: 悬浮按钮（FCB）视图层的生命周期、不夺焦属性与防误关测试
// 核心职责: 在 STA 线程验证 ShowActivated/Focusable 属性、Alt+F4 拦截为 Hide 以及二次唤醒安全
// ============================================================================

using System;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using BlueRandom.Modules.ApiBridge;
using BlueRandom.Views.FloatingButton;
using Xunit;

namespace BlueRandom.Tests;

/// <summary>
/// 悬浮按钮（FloatingButtonWindow）无焦点交互与生命周期守卫测试。
/// </summary>
public class FloatingButtonWindowTests
{
    private sealed class FakeTouchDevice : TouchDevice
    {
        public Point Position { get; set; }

        public FakeTouchDevice(int deviceId, PresentationSource? source) : base(deviceId)
        {
            if (source != null) SetActiveSource(source);
        }

        public override TouchPoint GetTouchPoint(IInputElement relativeTo)
        {
            return new TouchPoint(this, Position, new Rect(Position, new Size(1, 1)), TouchAction.Move);
        }

        public override TouchPointCollection GetIntermediateTouchPoints(IInputElement relativeTo)
        {
            return new TouchPointCollection();
        }
    }
    /// <summary>
    /// 验证悬浮按钮窗口配置了 ShowActivated=false 与 Focusable=false，确保不抢占前台焦点。
    /// </summary>
    [Fact]
    public void Window_ConfiguredWithNoActivateAndNonFocusable()
    {
        RunOnSta(() =>
        {
            var window = new FloatingButtonWindow();
            try
            {
                Assert.False(window.ShowActivated, "悬浮按钮必须设置 ShowActivated=false，防止显示时抢夺 PPT 等前台窗口焦点");
                Assert.False(window.Focusable, "悬浮按钮窗体必须为非可聚焦窗口");
                Assert.False(window.BtnMin.Focusable, "小按钮必须为非可聚焦");
                Assert.False(window.BtnCancel.Focusable, "取消按钮必须为非可聚焦");
                Assert.False(window.BtnConfirm.Focusable, "确认按钮必须为非可聚焦");
            }
            finally
            {
                // 真正退出并注销 IPC 监听
                window.Destroy();
            }
        });
    }

    /// <summary>
    /// 验证悬浮按钮在收到非 Destroy 的 Close 请求时（如 Alt+F4、外部关闭），仅执行隐藏而不销毁底层对象，
    /// 且再次调用 Show() 绝不抛出 InvalidOperationException。
    /// </summary>
    [Fact]
    public void Window_ClosingIsInterceptedToHide_AndCanBeReshown()
    {
        RunOnSta(() =>
        {
            var window = new FloatingButtonWindow();
            try
            {
                window.Show();
                Assert.True(window.IsVisible);

                // 模拟外部触发关闭（例如用户按 Alt+F4）
                window.Close();

                // 应当被拦截并隐藏，而不是彻底销毁
                Assert.False(window.IsVisible, "关闭请求应当被重定向为隐藏 (Hide)");

                // 再次从托盘呼出时调用 Show()，必须能够成功展示，不得抛出 InvalidOperationException
                var ex = Record.Exception(() => window.Show());
                Assert.Null(ex);
                Assert.True(window.IsVisible, "重新 Show() 应当成功恢复显示");
            }
            finally
            {
                // 真正退出并注销 IPC 监听
                window.Destroy();
            }
        });
    }

    /// <summary>
    /// 验证触控点击会标记 Handled 阻断鼠标提升，且即使后续收到提升的 MouseUp 也不会导致选择器展开后瞬间缩回。
    /// </summary>
    [Fact]
    public void TouchTap_MarksHandled_AndDoesNotCollapseOnPromotedMouseUp()
    {
        RunOnSta(() =>
        {
            var window = new FloatingButtonWindow();
            try
            {
                window.Show();
                var touch = new FakeTouchDevice(1, PresentationSource.FromVisual(window))
                {
                    Position = new Point(120, 120)
                };

                var downArgs = new TouchEventArgs(touch, Environment.TickCount)
                {
                    RoutedEvent = UIElement.TouchDownEvent,
                    Source = window.MainButtonBorder
                };
                window.MainButtonBorder.RaiseEvent(downArgs);
                Assert.True(downArgs.Handled, "TouchDown 必须标记 Handled 以阻止 WPF 提升为鼠标事件");

                var upArgs = new TouchEventArgs(touch, Environment.TickCount)
                {
                    RoutedEvent = UIElement.TouchUpEvent,
                    Source = window.MainButtonBorder
                };
                window.MainButtonBorder.RaiseEvent(upArgs);
                Assert.True(upArgs.Handled, "TouchUp 必须标记 Handled 以阻止 WPF 提升为鼠标事件");
                Assert.True(window.IsPickerOpen, "触控点击后选择器应当保持展开");

                // 模拟紧随其后到达的提升 MouseUp，不应把刚展开的选择器再次收起
                var promotedMouseUp = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseUpEvent,
                    Source = window.MainButtonBorder
                };
                window.MainButtonBorder.RaiseEvent(promotedMouseUp);
                Assert.True(window.IsPickerOpen, "触控点击后不应因后续 MouseUp 瞬间缩回");

                // 再次触控点击应正常收起
                window.MainButtonBorder.RaiseEvent(new TouchEventArgs(touch, Environment.TickCount)
                {
                    RoutedEvent = UIElement.TouchDownEvent,
                    Source = window.MainButtonBorder
                });
                window.MainButtonBorder.RaiseEvent(new TouchEventArgs(touch, Environment.TickCount)
                {
                    RoutedEvent = UIElement.TouchUpEvent,
                    Source = window.MainButtonBorder
                });
                Assert.False(window.IsPickerOpen, "第二次触控点击应当正常收起选择器");
            }
            finally
            {
                window.Destroy();
            }
        });
    }

    /// <summary>
    /// 验证触控拖拽超过阈值时直接平滑移动窗口位置，不依赖 DragMove 且抬起时不误触展开选择器。
    /// </summary>
    [Fact]
    public void TouchDrag_MovesWindow_AndDoesNotTogglePicker()
    {
        RunOnSta(() =>
        {
            var window = new FloatingButtonWindow();
            try
            {
                window.Show();
                window.Left = 200;
                window.Top = 200;
                window.ClampPosition();
                double startLeft = window.Left;
                double startTop = window.Top;

                var touch = new FakeTouchDevice(2, PresentationSource.FromVisual(window))
                {
                    Position = new Point(120, 120)
                };

                window.MainButtonBorder.RaiseEvent(new TouchEventArgs(touch, Environment.TickCount)
                {
                    RoutedEvent = UIElement.TouchDownEvent,
                    Source = window.MainButtonBorder
                });

                touch.Position = new Point(150, 140);
                var moveArgs = new TouchEventArgs(touch, Environment.TickCount)
                {
                    RoutedEvent = UIElement.TouchMoveEvent,
                    Source = window.MainButtonBorder
                };
                window.MainButtonBorder.RaiseEvent(moveArgs);
                Assert.True(moveArgs.Handled);

                var upArgs = new TouchEventArgs(touch, Environment.TickCount)
                {
                    RoutedEvent = UIElement.TouchUpEvent,
                    Source = window.MainButtonBorder
                };
                window.MainButtonBorder.RaiseEvent(upArgs);
                Assert.True(upArgs.Handled);

                Assert.False(window.IsPickerOpen, "触控拖拽抬起后不应误触展开选择器");
                Assert.True(window.Left > startLeft, "触控向右拖拽后窗口 Left 应增大");
                Assert.True(window.Top > startTop, "触控向下拖拽后窗口 Top 应增大");
            }
            finally
            {
                window.Destroy();
            }
        });
    }

    /// <summary>
    /// 验证普通鼠标左键点击仍可正常展开与收起选择器。
    /// </summary>
    [Fact]
    public void MouseClick_TogglesPickerOpenAndClosed()
    {
        RunOnSta(() =>
        {
            var window = new FloatingButtonWindow();
            try
            {
                window.Show();

                window.MainButtonBorder.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseDownEvent,
                    Source = window.MainButtonBorder
                });
                window.MainButtonBorder.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseUpEvent,
                    Source = window.MainButtonBorder
                });
                Assert.True(window.IsPickerOpen);

                window.MainButtonBorder.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseDownEvent,
                    Source = window.MainButtonBorder
                });
                window.MainButtonBorder.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseUpEvent,
                    Source = window.MainButtonBorder
                });
                Assert.False(window.IsPickerOpen);
            }
            finally
            {
                window.Destroy();
            }
        });
    }

    /// <summary>在专用 STA 线程上执行 WPF 代码并透传异常。</summary>
    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        if (error != null) throw new Xunit.Sdk.XunitException($"STA 线程执行失败: {error}");
    }
}
