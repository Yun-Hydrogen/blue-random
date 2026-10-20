// ==============================================================================
// 文件: LastResultWindow.xaml.cs
// 命名空间: BlueRandom.UI.LRW
// 作用: 结果暂留浮窗（Last Result Window）。
//       抽取演播结束后，在屏幕右上角常驻展示上一轮抽取名单，支持手动关闭。
//       运行于独立 STA 线程（由 WindowManager 负责创建与调度）。
// ==============================================================================

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BlueRandom.Core.Config;
using BlueRandom.Core.Ipc;
using BlueRandom.Core.Logging;
using BlueRandom.Core.Resources;

namespace BlueRandom.UI.LRW;

/// <summary>
/// 右上角结果暂留浮窗。订阅 <see cref="IpcModule.Lrw"/> 上的展示、隐藏与样式热更新指令。
/// </summary>
public partial class LastResultWindow : Window
{
    /// <summary>卡片从屏幕右缘滑入/滑出的水平位移量，需大于卡片宽度以完全移出窗口。</summary>
    private const double SlideDistance = 400;

    private static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(200);

    /// <summary>用于取消过期的滑出动画，避免"隐藏→立即显示"时被旧的 Completed 回调误隐藏。</summary>
    private int _hideVersion;

    /// <summary>用于取消过期的自动关闭动画，避免旧进度条结束时误触发隐藏。</summary>
    private int _autoCloseVersion;

    private int _autoCloseSeconds;

    public LastResultWindow()
    {
        InitializeComponent();

        FontFamily = ResourceCache.CustomFont;
        IpcBroker.Subscribe(IpcModule.Lrw, OnIpcMessageReceived);
        ApplyProps(ConfigCache.GetFRWProps());
    }

    private void OnIpcMessageReceived(IpcMessage msg)
    {
        // 消息来自 IPC 分发线程，切回本窗口的 UI 线程处理
        Dispatcher.BeginInvoke(new Action(() =>
        {
            switch (msg.Command)
            {
                case "ShowWindow":
                    if (msg.Payload is LrwShowPayload payload) ShowResults(payload);
                    break;

                case "HideWindow":
                    HideWindow();
                    break;

                case "ApplyNewProp":
                    if (msg.Payload is PickResultDialogConfig props) ApplyProps(props);
                    break;

                case "Destroy":
                    Close();
                    Dispatcher.InvokeShutdown();
                    break;
            }
        }));
    }

    /// <summary>
    /// 载入并展示指定抽取结果，窗口固定出现在主屏工作区右上角。
    /// </summary>
    public void ShowResults(LrwShowPayload payload)
    {
        if (payload == null) return;

        AppLogger.Info("LRW", $"展示上次抽取结果：{payload.CalledStudents?.Count ?? 0} 人");

        _hideVersion++;

        try
        {
            // 每次显示前同步最新样式与自动关闭时长
            ApplyProps(ConfigCache.GetFRWProps());

            var students = payload.CalledStudents ?? new List<CalledStudent>();

            CountText.Text = $"{students.Count} 人";
            TimeText.Text = payload.DrawnAt == default ? string.Empty : $"{payload.DrawnAt:HH:mm} 抽取";

            bool hasData = students.Count > 0;
            EmptyText.Visibility = hasData ? Visibility.Collapsed : Visibility.Visible;
            ListScroll.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;
            ResultList.ItemsSource = BuildRows(students);

            PositionTopRight();

            if (!IsVisible)
            {
                ResetSlide();
                Show();
            }
            PlayEnterAnimation();
            StartAutoClose(_autoCloseSeconds);

            AppLogger.Info("LRW", $"窗口状态 IsVisible={IsVisible} Left={Left:0} Top={Top:0} W={ActualWidth:0} H={ActualHeight:0}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("LRW", "展示上次抽取结果失败", ex);
        }
    }

    /// <summary>固定贴主屏工作区右上角，卡片右缘距屏幕 20 DIP。</summary>
    private void PositionTopRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width;
        Top = workArea.Top;
    }

    /// <summary>
    /// 从屏幕右缘滑入：从当前位移（首次显示时为 <see cref="SlideDistance"/>）插值到 0。
    /// 卡片已在位时直接返回，避免配置热更新重播入场动画造成闪烁。
    /// </summary>
    private void PlayEnterAnimation()
    {
        double start = SlideTransform.X;
        SlideTransform.BeginAnimation(TranslateTransform.XProperty, null);
        SlideTransform.X = start;

        if (start <= 0.5) return;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        SlideTransform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(start, 0, EnterDuration) { EasingFunction = easing });
    }

    /// <summary>把卡片复位到窗口右缘外侧，保证下一次显示一定从屏幕外开始滑入。</summary>
    private void ResetSlide()
    {
        SlideTransform.BeginAnimation(TranslateTransform.XProperty, null);
        SlideTransform.X = SlideDistance;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => HideWindow();

    /// <summary>向右滑出并隐藏窗口；下次抽取结束后会再次自动滑入显示。</summary>
    private void HideWindow()
    {
        StopAutoClose();
        HideProgress();

        if (!IsVisible)
        {
            ResetSlide();
            return;
        }

        int version = ++_hideVersion;

        // 从当前实际位移开始滑出，避免中途被打断时（如入场动画未完）出现跳帧
        double current = SlideTransform.X;
        SlideTransform.BeginAnimation(TranslateTransform.XProperty, null);
        SlideTransform.X = current;

        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };
        var slide = new DoubleAnimation(current, SlideDistance, ExitDuration) { EasingFunction = easing };
        slide.Completed += (_, _) =>
        {
            if (version != _hideVersion) return;
            Hide();
            ResetSlide();
        };
        SlideTransform.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    /// <summary>套用暂留浮窗独立的背景色、边框色、不透明度与自动关闭时长。</summary>
    private void ApplyProps(PickResultDialogConfig? props)
    {
        if (props == null) return;

        var palette = LrwVisualTheme.Compute(
            props.LastResultBgColor, props.LastResultBorderColor, props.LastResultOpacity);

        RootCard.Background = BrushOf(palette.CardBackground);

        // 卡片内的姓名片、徽标、进度条都使用与整张卡片相同的不透明度：
        // 颜色向强调色轻混合保持层级感，透明度跟随配置，避免半透明卡片里残留实心白块。
        Resources["AccentBrush"] = BrushOf(palette.Accent);
        Resources["RowBackgroundBrush"] = BrushOf(palette.RowBackground);
        Resources["BadgeBackgroundBrush"] = BrushOf(palette.BadgeBackground);
        Resources["CloseHoverBrush"] = BrushOf(palette.CloseHover);
        Resources["ProgressTrackBrush"] = BrushOf(palette.ProgressTrack);

        _autoCloseSeconds = Math.Max(0, props.LastResultAutoCloseSeconds);
        if (IsVisible) StartAutoClose(_autoCloseSeconds);
    }

    /// <summary>把 0xAARRGGBB 转成冻结的纯色画刷。</summary>
    private static SolidColorBrush BrushOf(uint argb)
    {
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF)));
        brush.Freeze();
        return brush;
    }

    /// <summary>启动自动关闭进度条；0 秒表示不自动关闭，此时进度条隐藏。</summary>
    private void StartAutoClose(int seconds)
    {
        StopAutoClose();

        _autoCloseSeconds = Math.Max(0, seconds);
        if (_autoCloseSeconds <= 0)
        {
            HideProgress();
            return;
        }

        ProgressHost.Visibility = Visibility.Visible;

        int version = ++_autoCloseVersion;
        var shrink = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(_autoCloseSeconds));
        shrink.Completed += (_, _) =>
        {
            if (version != _autoCloseVersion) return;
            HideWindow();
        };

        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
    }

    /// <summary>停止进度条动画并复位，避免旧的 Completed 回调触发隐藏。</summary>
    private void StopAutoClose()
    {
        _autoCloseVersion++;
        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ProgressScale.ScaleX = 1;
    }

    private void HideProgress()
    {
        ProgressHost.Visibility = Visibility.Collapsed;
    }

    private static List<LastResultRow> BuildRows(IReadOnlyList<CalledStudent> students)
    {
        var rows = new List<LastResultRow>(students.Count);
        foreach (var student in students)
        {
            rows.Add(new LastResultRow
            {
                Name = student.Name,
                BadgeBrush = BadgeFor(student.LetterColor)
            });
        }
        return rows;
    }

    /// <summary>根据信封品阶生成用于姓名左侧圆点的颜色/渐变。</summary>
    private static Brush BadgeFor(string? letterColor)
    {
        switch ((letterColor ?? "rainbow").ToLowerInvariant())
        {
            case "gold":
                return new LinearGradientBrush(
                    Color.FromRgb(0xFF, 0xD3, 0x6B),
                    Color.FromRgb(0xE8, 0xA9, 0x2E), 45);

            case "blue":
                return new LinearGradientBrush(
                    Color.FromRgb(0x8A, 0xDB, 0xFF),
                    Color.FromRgb(0x3D, 0xA9, 0xE8), 45);

            default:
                // 彩信封（三星）在暂留浮窗中用紫色圆点表示
                return new LinearGradientBrush(
                    Color.FromRgb(0xC9, 0x8B, 0xFF),
                    Color.FromRgb(0x7C, 0x4D, 0xE6), 45);
        }
    }
}

/// <summary>结果暂留浮窗的列表行数据。</summary>
public sealed class LastResultRow
{
    public string Name { get; init; } = string.Empty;
    public Brush BadgeBrush { get; init; } = Brushes.Transparent;
}
