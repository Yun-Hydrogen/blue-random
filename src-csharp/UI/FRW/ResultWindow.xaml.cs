using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
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

public partial class ResultWindow : Window
{
    private static readonly IEasingFunction s_cubicEase = CreateFrozenEase();
    private static IEasingFunction CreateFrozenEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    private bool _isClosing = false;
    private bool _canClose = false;
    private int _currentSessionToken = 0;

    private MediaPlayer? _sfxPlayer;
    private MediaPlayer? _bgmPlayer;
    private System.Windows.Threading.DispatcherTimer? _bgmFadeTimer;
    private double _targetBgmVolume = 0.6;
    private double _currentBgmVolume = 0.0;
    private double _bgmFadeDurationSec = 1.5;
    private bool _isBgmFadingOut = false;

    public ResultWindow()
    {
        InitializeComponent();
        FontFamily = ResourceCache.CustomFont;
        DecoImage.Source = ResourceCache.GetImage("Arona_Plana.png");

        // 构造函数即刻注册 IPC 消息监听，彻底规避 Loaded 陷阱
        IpcBroker.Subscribe(IpcModule.Frw, OnIpcMessageReceived);
    }

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

    private void StopBgmTimer()
    {
        if (_bgmFadeTimer != null)
        {
            _bgmFadeTimer.Stop();
            _bgmFadeTimer = null;
        }
    }

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
    /// 展示招募结果并播放 1:1 复刻自 Electron 的原汁原味入场、15°信封下落与姓名弹起动画
    /// </summary>
    public void DisplayResults(List<CalledStudent> students, PickResultDialogConfig props)
    {
        Dispatcher.Invoke(() =>
        {
            _currentSessionToken++;
            int thisToken = _currentSessionToken;

            _isClosing = false;
            _canClose = false;

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

            // 2. 装饰立绘与空状态判定
            DecoImage.Visibility = props.ShowDeco ? Visibility.Visible : Visibility.Collapsed;
            if (students.Count == 0)
            {
                TxtEmptyResult.Visibility = Visibility.Visible;
                BottomRowPanel.Visibility = Visibility.Collapsed;
                _canClose = true;
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

            // 4. 显示并置顶当前全屏窗口
            Show();
            var hwnd = new WindowInteropHelper(this).Handle;
            WindowsSystemService.RefreshTopMost(hwnd);

            // 5. 播放抽卡音效与背景音乐 (跟随配置音量、自定义资源、起播点与淡入淡出)
            PlaySfx(props);
            PlayBgm(props);

            // 5. 主卡片面板飞入动画 (panel-fly-in: 0.7s cubic-bezier(0.2, 0.8, 0.2, 1))
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

            // 6. 构造每个学员的卡片并分发行列
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

            // 7. 动画播完前锁定关闭 (canClose 机制：防误触关闭)
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
                        }
                    });
                });
            }
        });
    }

    /// <summary>
    /// 构造单个学员招募卡片：
    /// 整个容器统领 15° 倾斜，信封与姓名白卡共用统一倾斜坐标系 (完美对齐 PickResult.vue)
    /// </summary>
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

        // ====== 1. 信封底图图层 (充满容器，带独立柔和阴影，高清采样) ======
        var envImage = new Image
        {
            Source = ResourceCache.GetEnvelopeImage(student.LetterColor),
            Width = 196,
            Height = 147,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(envImage, BitmapScalingMode.HighQuality);

        var envShadow = new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 14,
            ShadowDepth = 6,
            Opacity = 0.22,
            Direction = 270,
            RenderingBias = RenderingBias.Performance
        };
        envShadow.Freeze();
        envImage.Effect = envShadow;
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
    /// 平滑淡出并关闭/隐藏浮窗
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

    private void OnBackgroundMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_canClose && !_isClosing)
        {
            Dismiss();
        }
    }

    private void OnPanelMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 阻止点击卡片内容时误触发关闭
        e.Handled = true;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (_canClose && !_isClosing && (e.Key == Key.Escape || e.Key == Key.Enter || e.Key == Key.Space))
        {
            Dismiss();
        }
    }

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
