// ============================================================================
// 文件名称: FloatingButtonWindowTests.cs
// 项目名称: BlueRandom.Tests
// 文件说明: 悬浮按钮（FCB）视图层的生命周期、不夺焦属性与防误关测试
// 核心职责: 在 STA 线程验证 ShowActivated/Focusable 属性、Alt+F4 拦截为 Hide 以及二次唤醒安全
// ============================================================================

using System;
using System.Threading;
using BlueRandom.Modules.ApiBridge;
using BlueRandom.Views.FloatingButton;
using Xunit;

namespace BlueRandom.Tests;

/// <summary>
/// 悬浮按钮（FloatingButtonWindow）无焦点交互与生命周期守卫测试。
/// </summary>
public class FloatingButtonWindowTests
{
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
