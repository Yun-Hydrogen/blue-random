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
    /// <summary>用于取消过期的淡出动画，避免"隐藏→立即显示"时被旧的 Completed 回调误隐藏。</summary>
    private int _hideVersion;

    private readonly DispatcherTimer _autoCloseTimer;
    private int _autoCloseSeconds;
    private int _remainingSeconds;

    public LastResultWindow()
    {
        InitializeComponent();

        _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _autoCloseTimer.Tick += OnAutoCloseTick;

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

            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            if (!IsVisible) Show();
            PlayEnterAnimation();
            StartAutoClose(_autoCloseSeconds);

            AppLogger.Info("LRW", $"窗口状态 IsVisible={IsVisible} Left={Left:0} Top={Top:0} W={ActualWidth:0} H={ActualHeight:0}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("LRW", "展示上次抽取结果失败", ex);
        }
    }

    /// <summary>固定贴主屏工作区右上角，留出 16 DIP 边距。</summary>
    private void PositionTopRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 16;
        Top = workArea.Top + 16;
    }

    private void PlayEnterAnimation()
    {
        Opacity = 0;
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
        SlideTransform.Y = -8;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = easing });
        SlideTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = easing });
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => HideWindow();

    /// <summary>淡出并隐藏窗口；下次抽取结束后会再次自动显示。</summary>
    private void HideWindow()
    {
        StopAutoClose();

        if (!IsVisible) return;

        int version = ++_hideVersion;
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) =>
        {
            if (version != _hideVersion) return;
            Hide();
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>套用暂留浮窗独立的背景色、边框色、不透明度与自动关闭时长。</summary>
    private void ApplyProps(PickResultDialogConfig? props)
    {
        if (props == null) return;

        try
        {
            if (!string.IsNullOrWhiteSpace(props.LastResultBorderColor))
            {
                var border = (Color)ColorConverter.ConvertFromString(props.LastResultBorderColor);
                RootCard.BorderBrush = new SolidColorBrush(border);
            }

            if (!string.IsNullOrWhiteSpace(props.LastResultBgColor))
            {
                var bg = (Color)ColorConverter.ConvertFromString(props.LastResultBgColor);
                bg.A = (byte)Math.Round(Math.Clamp(props.LastResultOpacity, 0.0, 1.0) * 255);
                RootCard.Background = new SolidColorBrush(bg);
            }
        }
        catch
        {
            // 非法颜色字符串保持默认样式
        }

        _autoCloseSeconds = Math.Max(0, props.LastResultAutoCloseSeconds);
        if (IsVisible) StartAutoClose(_autoCloseSeconds);
    }

    private void StartAutoClose(int seconds)
    {
        _autoCloseTimer.Stop();
        _autoCloseSeconds = Math.Max(0, seconds);
        _remainingSeconds = _autoCloseSeconds;

        if (_autoCloseSeconds <= 0)
        {
            CountdownText.Text = string.Empty;
            CountdownText.Visibility = Visibility.Collapsed;
            return;
        }

        CountdownText.Visibility = Visibility.Visible;
        UpdateCountdownText();
        _autoCloseTimer.Start();
    }

    private void StopAutoClose()
    {
        _autoCloseTimer.Stop();
        _remainingSeconds = 0;
        CountdownText.Text = string.Empty;
        CountdownText.Visibility = Visibility.Collapsed;
    }

    private void OnAutoCloseTick(object? sender, EventArgs e)
    {
        if (_remainingSeconds <= 1)
        {
            StopAutoClose();
            HideWindow();
            return;
        }

        _remainingSeconds--;
        UpdateCountdownText();
    }

    private void UpdateCountdownText()
    {
        if (_autoCloseSeconds <= 0) return;
        CountdownText.Text = $"{Math.Max(0, _remainingSeconds)} 秒后自动关闭";
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
