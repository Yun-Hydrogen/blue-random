using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace BlueRandom.UI.Splash;

public partial class SplashWindow : Window
{
    private const double MaxBarWidth = 380.0;

    public SplashWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

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
    /// 更新启动进度（线程安全，内部调度到 UI 线程）
    /// </summary>
    /// <param name="percent">0 到 100 之间的百分比</param>
    /// <param name="statusText">当前阶段文案描述</param>
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
    /// 触发错误状态高亮展示
    /// </summary>
    public void SetErrorState(string errorText)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Foreground = System.Windows.Media.Brushes.Crimson;
            StatusText.Text = errorText;
        });
    }

    /// <summary>
    /// 淡出并销毁窗口
    /// </summary>
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
