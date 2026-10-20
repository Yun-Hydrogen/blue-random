// ============================================================================
// 文件名称: LastResultWindowTests.cs
// 项目名称: BlueRandom.Tests
// 文件说明: 结果暂留浮窗（LRW）视图层的构造与配色应用测试
// 核心职责: 在 STA 线程验证窗口可正常加载 XAML、样式资源随不透明度联动
// ============================================================================

using System;
using System.Threading;
using System.Windows.Media;
using BlueRandom.Core.Config;
using BlueRandom.UI.LRW;
using Xunit;

namespace BlueRandom.Tests;

/// <summary>结果暂留浮窗（LRW）视图测试。</summary>
public class LastResultWindowTests
{
    /// <summary>窗口构造后应把配置的不透明度同步到卡片与所有子元素画刷。</summary>
    [Fact]
    public void Window_AppliesOpacityToCardAndChildren()
    {
        RunOnSta(() =>
        {
            var config = ConfigCache.Current;
            var original = config.PickResultDialog;
            try
            {
                config.PickResultDialog.LastResultBgColor = "#ffffff";
                config.PickResultDialog.LastResultBorderColor = "#66ccff";
                config.PickResultDialog.LastResultOpacity = 0.5;
                config.PickResultDialog.LastResultAutoCloseSeconds = 0;

                var window = new LastResultWindow();
                try
                {
                    var expected = (byte)Math.Round(0.5 * 255);

                    var card = ((SolidColorBrush)window.RootCard.Background).Color;
                    Assert.Equal(expected, card.A);

                    foreach (string key in new[]
                             {
                                 "AccentBrush", "RowBackgroundBrush", "BadgeBackgroundBrush",
                                 "CloseHoverBrush", "ProgressTrackBrush"
                             })
                    {
                        var brush = (SolidColorBrush)window.Resources[key];
                        Assert.Equal(expected, brush.Color.A);
                    }
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                config.PickResultDialog = original;
            }
        });
    }

    /// <summary>自动关闭为 0 时进度条必须隐藏。</summary>
    [Fact]
    public void Window_HidesProgressWhenAutoCloseDisabled()
    {
        RunOnSta(() =>
        {
            var config = ConfigCache.Current;
            var original = config.PickResultDialog;
            try
            {
                config.PickResultDialog.LastResultAutoCloseSeconds = 0;

                var window = new LastResultWindow();
                try
                {
                    Assert.Equal(System.Windows.Visibility.Collapsed, window.ProgressHost.Visibility);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                config.PickResultDialog = original;
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
