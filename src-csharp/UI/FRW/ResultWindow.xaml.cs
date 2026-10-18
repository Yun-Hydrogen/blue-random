// ============================================================================
//  File: ResultWindow.xaml.cs
//  Project: BlueRandom (WPF Native)
//  Description:
//    招募结果展示浮窗 (FRW: Floating Result Window)。
//
//  Architecture & Visual Engineering Highlights:
//    1. 还原经典手游招募动效体系：
//       - 主容器 0.7s cubic-bezier(0.2, 0.8, 0.2, 1.0) 平滑飞入与弹性缩放；
//       - 统一 15° 倾斜空间坐标系：信封与姓名卡片同构旋转，避免局部倾斜产生的边缘撕裂；
//       - 交错编排 (Stagger Sequencing)：信封依次延迟 index * 120ms 下落并放大至原尺寸；
//         待信封全数到位后，纯白姓名条自中心弹跳揭晓；
//    2. 解决 WPF 文本栅格化渲染缺陷：
//       - 采用双层 Border 架构，将 DropShadowEffect 隔离于底层空白 Border，顶层实体 Border 承载纯净矢量文字，
//         彻底杜绝 WPF 像素着色器导致的 ClearType 降级模糊；
//       - 信封阴影改为预渲染进 PNG（Letter_*_Shadow.png，pad=20 / blur=14 / offsetY=6 / opacity=0.22），
//         运行时不再挂 DropShadowEffect，10 抽动画期间省去 10 路高斯模糊重算。
//    3. 音频与流媒体引擎：
//       - 支持独立抽卡音效即时播放；
//       - 基于 DispatcherTimer 驱动 30ms 粒度 BGM 渐入与离场平滑淡出 (Fade In / Fade Out)；
//    4. 交互安全与状态防抖：
//       - 内置基于 Token 与动画生命周期的 canClose 锁，防止用户误触提前关闭导致视觉截断；
//       - 窗口为内容包围盒（非全屏），仅底部“点击此处关闭”胶囊可触发关闭；
//         窗口面积大幅缩小，降低 layered 透明窗口的软件光栅化开销（P0-1 性能优化）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using BlueRandom.Core.Config;
using BlueRandom.Core.Ipc;
using BlueRandom.Core.Logging;
using BlueRandom.Core.Resources;
using BlueRandom.Services;

namespace BlueRandom.UI.FRW;

/// <summary>
/// 全屏沉浸式招募结果展示窗体。
/// </summary>
public partial class ResultWindow : Window
{
    private static readonly IEasingFunction s_cubicEase = CreateFrozenEase();

    /// <summary>
    /// 构建并冻结全局共享的三次贝塞尔平滑缓动曲线实例。
    /// </summary>
    private static IEasingFunction CreateFrozenEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    private bool _isClosing = false;
    private bool _canClose = false;
    private int _currentSessionToken = 0;

    /// <summary>
    /// 最近一次展示的学员列表（供 NativeBridge.GetLastResultsJson 读取）。
    /// </summary>
    public List<CalledStudent> CurrentStudents { get; private set; } = new();

    private MediaPlayer? _sfxPlayer;
    private MediaPlayer? _bgmPlayer;
    private System.Windows.Threading.DispatcherTimer? _bgmFadeTimer;
    private double _targetBgmVolume = 0.6;
    private double _currentBgmVolume = 0.0;
    private double _bgmFadeDurationSec = 1.5;
    private bool _isBgmFadingOut = false;

    /// <summary>
    /// 初始化结果窗口，注入私有字体资源，并立即挂载 IPC 监听。
    /// </summary>
    public ResultWindow()
    {
        InitializeComponent();
        FontFamily = ResourceCache.CustomFont;

        // 构造函数即刻注册 IPC 消息监听，彻底规避 Loaded 陷阱
        IpcBroker.Subscribe(IpcModule.Frw, OnIpcMessageReceived);
    }

    /// <summary>
    /// 根据配置回放单次抽卡音效（支持自定义音频或默认内置音效）。
    /// </summary>
    /// <param name="props">结果窗口配置数据。</param>
    private void PlaySfx(PickResultDialogConfig props)
    {
        if (!props.DefaultPlayGachaSound) return;

        try
        {
            string tempAudioDir = Path.Combine(Path.GetTempPath(), "BlueRandom_Audio");
            Directory.CreateDirectory(tempAudioDir);

            string? targetFilePath = null;
            if (!string.IsNullOrEmpty(props.GachaSoundCustomId))
            {
                var custom = CustomsStore.LoadCustom(props.GachaSoundCustomId);
                if (custom != null && !string.IsNullOrEmpty(custom.Base64))
                {
                    byte[] bytes = Convert.FromBase64String(custom.Base64);
                    string ext = Path.GetExtension(custom.FileName);
                    if (string.IsNullOrEmpty(ext)) ext = ".wav";
                    string filePath = Path.Combine(tempAudioDir, $"{custom.Id}{ext}");
                    if (!File.Exists(filePath) || new FileInfo(filePath).Length != bytes.Length)
                    {
                        File.WriteAllBytes(filePath, bytes);
                    }
                    targetFilePath = filePath;
                }
            }

            if (string.IsNullOrEmpty(targetFilePath))
            {
                byte[]? soundBytes = ResourceCache.GetAudioBytes("button_click.wav");
                if (soundBytes != null)
                {
                    string filePath = Path.Combine(tempAudioDir, "button_click.wav");
                    if (!File.Exists(filePath) || new FileInfo(filePath).Length != soundBytes.Length)
                    {
                        File.WriteAllBytes(filePath, soundBytes);
                    }
                    targetFilePath = filePath;
                }
            }

            if (!string.IsNullOrEmpty(targetFilePath) && File.Exists(targetFilePath))
            {
                if (_sfxPlayer == null)
                {
                    _sfxPlayer = new MediaPlayer();
                }
                else
                {
                    _sfxPlayer.Stop();
                    _sfxPlayer.Close();
                }

                double volume = Math.Clamp(props.SoundVolume / 100.0, 0.0, 1.0);
                _sfxPlayer.Volume = volume;
                _sfxPlayer.Open(new Uri(targetFilePath));
                _sfxPlayer.Play();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("FRW", $"播放抽卡音效失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 初始化并播放背景音乐 (BGM)，执行起播点跳转与平滑淡入定时步进。
    /// </summary>
    /// <param name="props">结果窗口配置数据。</param>
    private void PlayBgm(PickResultDialogConfig props)
    {
        StopBgmTimer();

        if (!props.PlayMusic || string.IsNullOrEmpty(props.BgmCustomId))
        {
            StopBgmImmediate();
            return;
        }

        try
        {
            var custom = CustomsStore.LoadCustom(props.BgmCustomId);
            if (custom == null || string.IsNullOrEmpty(custom.Base64))
            {
                StopBgmImmediate();
                return;
            }

            string tempAudioDir = Path.Combine(Path.GetTempPath(), "BlueRandom_Audio");
            Directory.CreateDirectory(tempAudioDir);

            byte[] bytes = Convert.FromBase64String(custom.Base64);
            string ext = Path.GetExtension(custom.FileName);
            if (string.IsNullOrEmpty(ext)) ext = ".mp3";
            string filePath = Path.Combine(tempAudioDir, $"{custom.Id}{ext}");
            if (!File.Exists(filePath) || new FileInfo(filePath).Length != bytes.Length)
            {
                File.WriteAllBytes(filePath, bytes);
            }

            if (_bgmPlayer == null)
            {
                _bgmPlayer = new MediaPlayer();
            }
            else
            {
                _bgmPlayer.Stop();
                _bgmPlayer.Close();
            }

            _targetBgmVolume = Math.Clamp(props.MusicVolume / 100.0, 0.0, 1.0);
            _bgmFadeDurationSec = Math.Max(0.1, props.BgmFadeDuration);
            _currentBgmVolume = 0.0;
            _isBgmFadingOut = false;

            _bgmPlayer.MediaEnded -= OnBgmEnded;
            _bgmPlayer.MediaEnded += OnBgmEnded;
            _bgmPlayer.Volume = 0.0;
            _bgmPlayer.Open(new Uri(filePath));
            _bgmPlayer.Position = TimeSpan.FromSeconds(Math.Max(0.0, props.BgmStartTime));
            _bgmPlayer.Play();

            // 淡入动画定时器 (间隔 30ms)
            double steps = (_bgmFadeDurationSec * 1000.0) / 30.0;
            double volStep = _targetBgmVolume / Math.Max(1.0, steps);

            _bgmFadeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _bgmFadeTimer.Tick += (_, _) =>
            {
                if (_isBgmFadingOut) return;
                _currentBgmVolume += volStep;
                if (_currentBgmVolume >= _targetBgmVolume)
                {
                    _currentBgmVolume = _targetBgmVolume;
                    if (_bgmPlayer != null) _bgmPlayer.Volume = _currentBgmVolume;
                    _bgmFadeTimer.Stop();
                }
                else
                {
                    if (_bgmPlayer != null) _bgmPlayer.Volume = _currentBgmVolume;
                }
            };
            _bgmFadeTimer.Start();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("FRW", $"播放抽卡 BGM 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 当 BGM 播放自然结束时的循环回放处理（跳转至指定的 BgmStartTime 重新循环）。
    /// </summary>
    private void OnBgmEnded(object? sender, EventArgs e)
    {
        try
        {
            if (_bgmPlayer != null && !_isBgmFadingOut)
            {
                var props = ConfigCache.GetFRWProps();
                _bgmPlayer.Position = TimeSpan.FromSeconds(Math.Max(0.0, props.BgmStartTime));
                _bgmPlayer.Play();
            }
        }
        catch { }
    }

    /// <summary>
    /// 停止并销毁当前运行的 BGM 音量淡入淡出定时器。
    /// </summary>
    private void StopBgmTimer()
    {
        if (_bgmFadeTimer != null)
        {
            _bgmFadeTimer.Stop();
            _bgmFadeTimer = null;
        }
    }

    /// <summary>
    /// 立即强制静音并关闭 BGM 媒体流。
    /// </summary>
    private void StopBgmImmediate()
    {
        StopBgmTimer();
        if (_bgmPlayer != null)
        {
            try
            {
                _bgmPlayer.Volume = 0.0;
                _bgmPlayer.Stop();
                _bgmPlayer.Close();
            }
            catch { }
        }
        _currentBgmVolume = 0.0;
    }

    /// <summary>
    /// 触发 BGM 平滑线性淡出 (Fade Out)，衰减至 0 后停止回放并执行完成回调。
    /// </summary>
    /// <param name="onComplete">淡出完毕后的延续动作。</param>
    private void FadeOutBgm(Action onComplete)
    {
        StopBgmTimer();

        if (_bgmPlayer != null && _currentBgmVolume > 0.01 && _bgmFadeDurationSec > 0.1)
        {
            _isBgmFadingOut = true;
            double steps = (_bgmFadeDurationSec * 1000.0) / 30.0;
            double volStep = _currentBgmVolume / Math.Max(1.0, steps);

            _bgmFadeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _bgmFadeTimer.Tick += (_, _) =>
            {
                _currentBgmVolume -= volStep;
                if (_currentBgmVolume <= 0.0)
                {
                    StopBgmImmediate();
                    onComplete();
                }
                else if (_bgmPlayer != null)
                {
                    _bgmPlayer.Volume = _currentBgmVolume;
                }
            };
            _bgmFadeTimer.Start();
        }
        else
        {
            StopBgmImmediate();
            onComplete();
        }
    }

    /// <summary>
    /// 按当前结果人数计算内容包围盒窗口尺寸并居中（非全屏）。
    /// <para>
    /// 设计常量与 ResultWindow.xaml 保持一致：
    /// 卡片 196×147、卡片左右 Margin 16（相邻间距 32）、
    /// 面板 Padding 48/24、边框 4、关闭胶囊 32 + 上边距 24 + 下边距 2、行间距 44。
    /// 额外左右 160 / 上下 120 余量用于容纳信封入场动画（初始 scale 2.5）与阴影，
    /// 避免信封放大阶段被窗口边界裁剪。
    /// </para>
    /// </summary>
    /// <param name="count">本轮抽取人数（0 表示空结果）。</param>
    private void UpdateWindowLayout(int count)
    {
        int rows = count > 5 ? 2 : 1;
        int cols = Math.Clamp(count, 1, 5);

        const double cardW = 196;
        const double cardH = 147;
        const double colGap = 32;
        const double rowGap = 44;
        const double panelBandX = 48 * 2 + 4 * 2;   // 左右 Padding + 边框
        const double panelBandY = 24 + 20 + 4 * 2;  // 上下 Padding + 边框
        const double hintBand = 32 + 24 + 2;        // 关闭胶囊高度 + 上下 Margin
        const double decoWidth = 420;
        const double decoHeight = 176;              // 420 × 506 / 1205 ≈ 176

        double panelW = Math.Max(420, cols * cardW + Math.Max(cols - 1, 0) * colGap + panelBandX);
        double panelH = rows * cardH + Math.Max(rows - 1, 0) * rowGap + panelBandY + hintBand;
        double contentW = Math.Max(panelW, decoWidth);
        double contentH = decoHeight - 4 + panelH;

        /*
         * 底部“点击此处关闭”胶囊：宽度约为抽取面板宽度的 75%，
         * 并限制在面板内可用宽度内（面板宽度 - 左右 Padding/边框），避免小人数时溢出。
         */
        double hintMaxWidth = panelW - panelBandX;
        HintPill.Width = Math.Clamp(panelW * 0.75, 260, hintMaxWidth);

        // 动画溢出与阴影余量（保证信封 scale 2.5 阶段完整可见）
        const double marginX = 160;
        const double marginY = 120;

        var workArea = SystemParameters.WorkArea;
        double winW = Math.Min(contentW + marginX * 2, workArea.Width * 0.95);
        double winH = Math.Min(contentH + marginY * 2, workArea.Height * 0.95);

        Width = winW;
        Height = winH;
        Left = workArea.Left + (workArea.Width - winW) / 2;
        Top = workArea.Top + (workArea.Height - winH) / 2;
    }

    /// <summary>
    /// 编排并呈现抽卡结果序列：
    /// 包括面板 0.7s cubic-bezier 飞入、信封 15° 倾角交错下落、BGM 起播以及防误触 canClose 锁定。
    /// </summary>
    /// <param name="students">被抽中的学员列表及其对应的信封品阶色彩。</param>
    /// <param name="props">浮窗个性化样式与声音配置。</param>
    public void DisplayResults(List<CalledStudent> students, PickResultDialogConfig props)
    {
        Dispatcher.Invoke(() =>
        {
            CurrentStudents = students ?? new List<CalledStudent>();
            _currentSessionToken++;
            int thisToken = _currentSessionToken;

            _isClosing = false;
            _canClose = false;
            TxtHint.Text = "正在揭晓抽取结果...";
            HintPill.Opacity = 0.6;

            // 彻底清理并重置动画属性持有，解除 WPF FillBehavior.HoldEnd 属性锁
            CenterContainer.BeginAnimation(OpacityProperty, null);
            PanelContainer.BeginAnimation(OpacityProperty, null);
            MainPanel.BeginAnimation(OpacityProperty, null);
            PanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            PanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            PanelTranslate.BeginAnimation(TranslateTransform.YProperty, null);

            CenterContainer.Opacity = 1;
            PanelContainer.Opacity = 1;
            MainPanel.Opacity = 1;

            // 1. 清空上一轮遗留元素
            TopRowPanel.Children.Clear();
            BottomRowPanel.Children.Clear();

            // 2. 空状态判定
            if (students.Count == 0)
            {
                TxtEmptyResult.Visibility = Visibility.Visible;
                BottomRowPanel.Visibility = Visibility.Collapsed;
                _canClose = true;
                TxtHint.Text = "点击此处关闭";
                HintPill.Opacity = 1.0;
            }
            else
            {
                TxtEmptyResult.Visibility = Visibility.Collapsed;
                BottomRowPanel.Visibility = students.Count > 5 ? Visibility.Visible : Visibility.Collapsed;
            }

            // 3. 应用个性化面板属性 (仅调节背景 Alpha 与边框色，子元素保持 100% 不透明)
            try
            {
                if (!string.IsNullOrEmpty(props.PanelBorderColor))
                {
                    var borderColor = (Color)ColorConverter.ConvertFromString(props.PanelBorderColor);
                    MainPanel.BorderBrush = new SolidColorBrush(borderColor);
                }
                double targetOpacity = Math.Clamp(props.PanelOpacity, 0.2, 1.0);
                byte alpha = (byte)Math.Clamp((int)(targetOpacity * 255), 0, 255);
                Color bgColor = Colors.White;
                if (!string.IsNullOrEmpty(props.PanelBgColor))
                {
                    try
                    {
                        bgColor = (Color)ColorConverter.ConvertFromString(props.PanelBgColor);
                    }
                    catch { }
                }
                MainPanel.Background = new SolidColorBrush(Color.FromArgb(alpha, bgColor.R, bgColor.G, bgColor.B));
                MainPanel.Opacity = 1.0;
                PanelShadow.Opacity = targetOpacity * 0.16;
            }
            catch { }

            // 4. 计算内容包围盒窗口尺寸并居中（非全屏，降低 layered 窗口软件光栅化面积）
            UpdateWindowLayout(students.Count);

            // 5. 显示并置顶当前窗口
            Show();
            var hwnd = new WindowInteropHelper(this).Handle;
            WindowsSystemService.RefreshTopMost(hwnd);

            // 6. 播放抽卡音效与背景音乐 (跟随配置音量、自定义资源、起播点与淡入淡出)
            PlaySfx(props);
            PlayBgm(props);

            // 7. 主卡片面板飞入动画 (panel-fly-in: 0.7s cubic-bezier(0.2, 0.8, 0.2, 1))
            PanelContainer.RenderTransformOrigin = new Point(0.5, 0.5);
            var panelPop = new DoubleAnimation(0.85, 1.0, TimeSpan.FromMilliseconds(700))
            {
                EasingFunction = s_cubicEase
            };
            var panelSlide = new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(700))
            {
                EasingFunction = s_cubicEase
            };
            var panelFade = new DoubleAnimation(0, 1.0, TimeSpan.FromMilliseconds(400));

            PanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, panelPop);
            PanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, panelPop);
            PanelTranslate.BeginAnimation(TranslateTransform.YProperty, panelSlide);
            PanelContainer.BeginAnimation(OpacityProperty, panelFade);

            // 8. 构造每个学员的卡片并分发行列
            for (int i = 0; i < students.Count; i++)
            {
                var student = students[i];
                var card = CreateStudentCard(student, i, students.Count);

                if (i < 5)
                {
                    TopRowPanel.Children.Add(card);
                }
                else
                {
                    BottomRowPanel.Children.Add(card);
                }
            }

            // 9. 动画播完前锁定关闭 (canClose 机制：防误触关闭)
            if (students.Count > 0)
            {
                int totalDelayMs = (Math.Max(students.Count - 1, 0) * 120) + 600;
                int readyDelayMs = totalDelayMs + 450;
                Task.Delay(readyDelayMs).ContinueWith(_ =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_currentSessionToken == thisToken)
                        {
                            _canClose = true;
                            TxtHint.Text = "点击此处关闭";
                            HintPill.Opacity = 1.0;
                        }
                    });
                });
            }
        });
    }

    /// <summary>
    /// 构造单个学员招募卡片：
    /// 整个卡片网格统领 15° 倾斜，信封与姓名白卡共用统一倾斜坐标系，并挂载交错下落与独立阴影。
    /// </summary>
    /// <param name="student">被抽取的学员信息（姓名、颜色品阶）。</param>
    /// <param name="index">在当前抽取批次中的次序索引（用于交错延迟）。</param>
    /// <param name="totalCount">本轮抽取的学员总数。</param>
    /// <returns>构建完成且配置有进场关键帧动画的卡片 FrameworkElement 实例。</returns>
    private FrameworkElement CreateStudentCard(CalledStudent student, int index, int totalCount)
    {
        // 外层卡片网格容器 (4:3 比例，宽 196，高 147，水平外间距 16px -> 相邻卡片间距 32px)
        var cardGrid = new Grid
        {
            Width = 196,
            Height = 147,
            Margin = new Thickness(16, 0, 16, 0),
            RenderTransformOrigin = new Point(0.5, 0.5),
            Opacity = 0,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };

        // 核心：容器整体倾斜 15°，入场缩放 2.5 -> 1.0，Y轴位移 -24 -> 0
        var cardScale = new ScaleTransform(2.5, 2.5);
        var cardTranslate = new TranslateTransform(0, -24);
        var cardRotate = new RotateTransform(15);
        var cardTransformGroup = new TransformGroup();
        cardTransformGroup.Children.Add(cardScale);
        cardTransformGroup.Children.Add(cardTranslate);
        cardTransformGroup.Children.Add(cardRotate);
        cardGrid.RenderTransform = cardTransformGroup;

        // 信封进场动画：letter-fly-in 0.6s ease-out，交错延迟 index * 0.12s
        var envDelay = TimeSpan.FromMilliseconds(index * 120);
        var cardScaleAnim = new DoubleAnimation(2.5, 1.0, TimeSpan.FromMilliseconds(600))
        {
            BeginTime = envDelay,
            EasingFunction = s_cubicEase
        };
        var cardTranslateAnim = new DoubleAnimation(-24, 0, TimeSpan.FromMilliseconds(600))
        {
            BeginTime = envDelay,
            EasingFunction = s_cubicEase
        };
        var cardFadeAnim = new DoubleAnimation(0, 1.0, TimeSpan.FromMilliseconds(400))
        {
            BeginTime = envDelay
        };

        cardScale.BeginAnimation(ScaleTransform.ScaleXProperty, cardScaleAnim);
        cardScale.BeginAnimation(ScaleTransform.ScaleYProperty, cardScaleAnim);
        cardTranslate.BeginAnimation(TranslateTransform.YProperty, cardTranslateAnim);
        cardGrid.BeginAnimation(OpacityProperty, cardFadeAnim);

        // ====== 1. 信封底图图层 (阴影已预渲染进 PNG，运行时零 Effect、动画期间零模糊重算) ======
        /*
         * 阴影图为 264×304（原图 216×256 + 四周 pad 24，双层阴影：近层 0.30 / 远层 0.40）。
         * 以 152×175 显示时，信封本体恢复为 124×147（与旧版一致），
         * 多出的上下 14px 为阴影留白，卡片 Grid 默认不裁剪。
         */
        var envImage = new Image
        {
            Source = ResourceCache.GetEnvelopeImage(student.LetterColor),
            Width = 152,
            Height = 175,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(envImage, BitmapScalingMode.HighQuality);
        cardGrid.Children.Add(envImage);

        // ====== 2. 姓名卡片层 (纯白底，圆角 12px，信封中央浮现，完全跟随 15° 倾斜) ======
        var nameContainer = new Grid
        {
            Width = 156,
            Height = 94,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Opacity = 0,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };

        // 2.1 独立阴影底板 (承载 DropShadowEffect，无任何文本子元素，彻底杜绝文本着色器栅格化模糊)
        var nameShadowBorder = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(12),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 12,
                ShadowDepth = 5,
                Opacity = 0.20,
                Direction = 270,
                RenderingBias = RenderingBias.Performance
            }
        };
        nameContainer.Children.Add(nameShadowBorder);

        // 2.2 实体卡片底板
        var nameCardBorder = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(12)
        };
        nameContainer.Children.Add(nameCardBorder);

        var nameScale = new ScaleTransform(0.96, 0.96);
        var nameTranslate = new TranslateTransform(0, 12);
        var nameTransformGroup = new TransformGroup();
        nameTransformGroup.Children.Add(nameScale);
        nameTransformGroup.Children.Add(nameTranslate);
        nameContainer.RenderTransform = nameTransformGroup;

        double fSize = student.Name.Length > 8 ? 18.0 : (student.Name.Length > 5 ? 21.0 : 24.0);
        var nameText = new TextBlock
        {
            Text = student.Name,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1C, 0x27, 0x41)),
            FontWeight = FontWeights.Bold,
            FontSize = fSize,
            FontFamily = ResourceCache.CustomFont,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(8, 4, 8, 4)
        };
        TextOptions.SetTextFormattingMode(nameText, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(nameText, TextRenderingMode.Auto);
        RenderOptions.SetClearTypeHint(nameText, ClearTypeHint.Enabled);
        nameContainer.Children.Add(nameText);

        // 姓名揭晓动画：所有信封就位后 (totalDelay = (count-1)*120 + 600)，各卡片依次弹出 (0.3s ease-out)
        int totalDelayMs = (Math.Max(totalCount - 1, 0) * 120) + 600;
        var nameDelay = TimeSpan.FromMilliseconds(totalDelayMs + 100 + index * 120);

        var nameScaleAnim = new DoubleAnimation(0.96, 1.0, TimeSpan.FromMilliseconds(300))
        {
            BeginTime = nameDelay,
            EasingFunction = s_cubicEase
        };
        var nameTranslateAnim = new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(300))
        {
            BeginTime = nameDelay,
            EasingFunction = s_cubicEase
        };
        var nameFadeAnim = new DoubleAnimation(0, 1.0, TimeSpan.FromMilliseconds(260))
        {
            BeginTime = nameDelay
        };

        nameScale.BeginAnimation(ScaleTransform.ScaleXProperty, nameScaleAnim);
        nameScale.BeginAnimation(ScaleTransform.ScaleYProperty, nameScaleAnim);
        nameTranslate.BeginAnimation(TranslateTransform.YProperty, nameTranslateAnim);
        nameContainer.BeginAnimation(OpacityProperty, nameFadeAnim);

        cardGrid.Children.Add(nameContainer);

        return cardGrid;
    }

    /// <summary>
    /// 平滑播放面板飞出退场动效 (0.2s)、执行 BGM 淡出，并在完全隐去后向后端上报 WindowClosed。
    /// </summary>
    public void Dismiss()
    {
        Dispatcher.Invoke(() =>
        {
            if (_isClosing) return;
            _isClosing = true;

            // 触发 BGM 平滑淡出与离场动画
            FadeOutBgm(() => { });

            // 退场飞出动画 (panel-fly-out: 0.2s)
            var flyOutScale = new DoubleAnimation(1.0, 0.9, TimeSpan.FromMilliseconds(200))
            {
                FillBehavior = FillBehavior.Stop
            };
            var flyOutTranslate = new DoubleAnimation(0, 8, TimeSpan.FromMilliseconds(200))
            {
                FillBehavior = FillBehavior.Stop
            };
            var fadeOut = new DoubleAnimation(1.0, 0, TimeSpan.FromMilliseconds(200))
            {
                FillBehavior = FillBehavior.Stop
            };

            fadeOut.Completed += (_, _) =>
            {
                PanelContainer.BeginAnimation(OpacityProperty, null);
                CenterContainer.BeginAnimation(OpacityProperty, null);
                PanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                PanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                PanelTranslate.BeginAnimation(TranslateTransform.YProperty, null);

                CenterContainer.Opacity = 0;
                PanelContainer.Opacity = 0;
                Hide();
                _isClosing = false;
                // 上报浮窗已关闭，通知后端恢复悬浮按钮
                IpcBroker.Send(IpcMessage.Create(IpcAction.Post, IpcModule.Frw, "WindowClosed"));
            };

            PanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, flyOutScale);
            PanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, flyOutScale);
            PanelTranslate.BeginAnimation(TranslateTransform.YProperty, flyOutTranslate);
            PanelContainer.BeginAnimation(OpacityProperty, fadeOut);
            CenterContainer.BeginAnimation(OpacityProperty, fadeOut);
        });
    }

    /// <summary>
    /// 拦截原生窗口关闭行为，将其转化为平滑的 Dismiss 隐藏逻辑，避免进程被提前销毁。
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (Application.Current != null && !Application.Current.Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
            Dismiss();
        }
        else
        {
            base.OnClosing(e);
        }
    }

    /// <summary>
    /// 底部“点击此处关闭”胶囊：本窗口唯一的鼠标关闭入口。
    /// 动画未解锁（_canClose=false）时点击无效，避免误触。
    /// </summary>
    private void OnClosePillMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_canClose || _isClosing) return;
        Dismiss();
    }

    /// <summary>
    /// 卡片主面板点击拦截事件：阻止点击面板内容冒泡（本窗口仅底部胶囊可关闭）。
    /// </summary>
    private void OnPanelMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    /// <summary>
    /// 全局键盘快捷键响应：Esc 在解锁后可快速关闭（鼠标关闭仅限底部胶囊）。
    /// </summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (_canClose && !_isClosing && e.Key == Key.Escape)
        {
            Dismiss();
        }
    }

    /// <summary>
    /// 处理面向结果浮窗模块的 IPC 消息分发（ShowWindow, CloseWindow, ApplyNewProp, Destroy）。
    /// </summary>
    /// <param name="msg">IPC 消息包。</param>
    private void OnIpcMessageReceived(IpcMessage msg)
    {
        Dispatcher.Invoke(() =>
        {
            switch (msg.Command)
            {
                case "ShowWindow":
                    if (msg.Payload is FrwShowPayload showPayload)
                    {
                        DisplayResults(showPayload.CalledStudents, ConfigCache.GetFRWProps());
                    }
                    break;

                case "CloseWindow":
                    Dismiss();
                    break;

                case "ApplyNewProp":
                    break;

                case "Destroy":
                    Close();
                    break;
            }
        });
    }
}
