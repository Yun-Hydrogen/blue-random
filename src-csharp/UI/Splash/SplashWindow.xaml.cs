// ============================================================================
//  File: SplashWindow.xaml.cs
//  Project: BlueRandom (WPF Native)
//  Description:
//    软件启动加载器过渡窗体 (Splash Window)。
//
//  Responsibilities:
//    1. 视觉品牌与版本代号呈现：加载专属高清 Logo、版本代号（Codename）；
//    2. 阶段进度与微交互呈现：提供 0% - 100% 平滑进度条动画（CubicEase）与各阶段状态文案；
//    3. 异常阻塞交互反馈：在关键初始化失败时提供高亮错误警报并衔接故障恢复弹窗；
//    4. 优雅退场转场：在 Stage 5 就绪后执行 FadeOutStoryboard 渐变退场并销毁自身。
// ============================================================================

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;

namespace BlueRandom.UI.Splash;

/// <summary>
/// 软件启动加载器过渡窗体。
/// </summary>
public partial class SplashWindow : Window
{
    private const double MaxBarWidth = 380.0;

    /// <summary>
    /// 初始化启动窗口实例并订阅 Loaded 事件。
    /// </summary>
    public SplashWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 窗体加载完成后注入版本代号、Logo 图标，并启动淡入进场动效。
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 绑定版本代号
        TxtCodename.Text = Core.AppVersion.Codename;

        // 加载应用 Logo 图标与窗口徽标
        try
        {
            var logo = Core.Resources.ResourceCache.GetImage("BlueRandom.png") ?? Core.Resources.ResourceCache.GetImage("app.ico");
            if (logo != null)
            {
                LogoImage.Source = logo;
                Icon = logo;
            }
        }
        catch
        {
            // 忽略图标加载异常
        }

        // 播放淡入动画
        if (Resources["FadeInStoryboard"] is Storyboard sb)
        {
            sb.Begin(this);
        }
    }

    /// <summary>
    /// 更新启动进度与当前阶段文案（线程安全，内部调度到 UI 线程）。
    /// </summary>
    /// <param name="percent">0 到 100 之间的进度百分比数值。</param>
    /// <param name="statusText">当前阶段操作描述文案。</param>
    public void UpdateProgress(double percent, string statusText)
    {
        Dispatcher.Invoke(() =>
        {
            percent = Math.Clamp(percent, 0.0, 100.0);
            StatusText.Text = statusText;
            PercentText.Text = $"{(int)percent}%";

            double targetWidth = (percent / 100.0) * MaxBarWidth;

            // 平滑推进进度条宽度
            var anim = new DoubleAnimation
            {
                To = targetWidth,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            ProgressBarFill.BeginAnimation(WidthProperty, anim);
        });
    }

    /// <summary>
    /// 触发错误状态高亮展示（将提示文字置为绯红色）。
    /// </summary>
    /// <param name="errorText">异常或错误提示文案。</param>
    public void SetErrorState(string errorText)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Foreground = System.Windows.Media.Brushes.Crimson;
            StatusText.Text = errorText;
        });
    }

    /// <summary>
    /// 异步执行淡出故事板动效 (FadeOutStoryboard) 并在动效完成后关闭与销毁启动窗口。
    /// </summary>
    /// <returns>等待退场动效执行完毕的异步 Task。</returns>
    public async Task FadeOutAndCloseAsync()
    {
        var tcs = new TaskCompletionSource();
        Dispatcher.Invoke(() =>
        {
            if (Resources["FadeOutStoryboard"] is Storyboard sb)
            {
                sb.Completed += (_, _) =>
                {
                    Close();
                    tcs.TrySetResult();
                };
                sb.Begin(this);
            }
            else
            {
                Close();
                tcs.TrySetResult();
            }
        });

        await tcs.Task;
    }
}
