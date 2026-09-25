using System;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BlueRandom.Core.Config;
using BlueRandom.Core.Ipc;
using BlueRandom.Core.Logging;
using BlueRandom.Core.Resources;
using BlueRandom.Services;

namespace BlueRandom.UI.FCB;

public partial class FloatingButtonWindow : Window
{
    private Point _mouseDownScreenPos;
    private bool _isPressed = false;
    private bool _hasDragged = false;
    private bool _isPickerOpen = false;
    private int _pickCount = 1;
    private SoundPlayer? _clickSoundPlayer;

    public FloatingButtonWindow()
    {
        InitializeComponent();

        // 构造函数即刻注册 IPC 消息监听，规避 Loaded 陷阱
        IpcBroker.Subscribe(IpcModule.Fcb, OnIpcMessageReceived);

        MainButtonBorder.MouseEnter += (_, _) =>
        {
            MainButtonBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(230, 40, 130, 230)); // rgba(40, 130, 230, 0.9)
        };
        MainButtonBorder.MouseLeave += (_, _) =>
        {
            MainButtonBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x66, 0xCC, 0xFF));
        };

        // 窗口失去焦点时自动收起胶囊（点击桌面其他窗口时自动关闭）
        Deactivated += (_, _) =>
        {
            if (_isPickerOpen) TogglePicker(false);
        };

        try
        {
            var icon = ResourceCache.GetImage("app.ico");
            if (icon != null) Icon = icon;
        }
        catch { }

        InitAudio();
        Loaded += OnLoaded;
    }

    private void InitAudio()
    {
        byte[]? soundBytes = ResourceCache.GetAudioBytes("button_click.wav");
        if (soundBytes != null)
        {
            try
            {
                var ms = new MemoryStream(soundBytes);
                _clickSoundPlayer = new SoundPlayer(ms);
                _clickSoundPlayer.Load();
            }
            catch
            {
                // 忽略音频初始化失败
            }
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyProps(ConfigCache.GetFCBProps());
        SetPickCount(ConfigCache.Current.PickCountDialog.DefaultCount);

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) WindowsSystemService.RefreshTopMost(hwnd);

        // 启动置顶保活定时器 (每 1 秒在 AlwaysOnTop 为 true 时保活置顶状态，确保全屏游戏/窗口下稳定置顶)
        var topTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        topTimer.Tick += (_, _) =>
        {
            if (Topmost && Visibility == Visibility.Visible)
            {
                var h = new WindowInteropHelper(this).Handle;
                if (h != IntPtr.Zero) WindowsSystemService.RefreshTopMost(h);
            }
        };
        topTimer.Start();
    }

    private void PlayClickSound()
    {
        try
        {
            _clickSoundPlayer?.Play();
        }
        catch
        {
            // 忽略音频播放异常
        }
    }

    /// <summary>
    /// 依据基准尺寸动态计算所有环绕控件的中心坐标 (1:1 复刻 Floating.vue pickerMetrics 并升级 180° 圆弧)
    /// </summary>
    private void UpdateGeometry(double baseSize)
    {
        double centerX = Width / 2.0;  // 120
        double centerY = Height / 2.0; // 120

        // 1. 主悬浮按钮居中
        MainButtonBorder.Width = baseSize;
        MainButtonBorder.Height = baseSize;
        MainButtonBorder.CornerRadius = new CornerRadius(baseSize / 2.0);
        Canvas.SetLeft(MainButtonBorder, centerX - baseSize / 2.0);
        Canvas.SetTop(MainButtonBorder, centerY - baseSize / 2.0);

        // 2. 环绕几何参数计算 (确认/取消按钮直径 44px，精确位于胶囊 120° 圆弧两端点正下方)
        double scale = baseSize / 50.0;
        double actionBtnSize = 44.0 * scale; // 确认/取消按钮直径 44px
        double endLeftX = centerX + (64.0 * scale) * Math.Cos(-150.0 * Math.PI / 180.0);  // ~64.6
        double endRightX = centerX + (64.0 * scale) * Math.Cos(-30.0 * Math.PI / 180.0);  // ~175.4
        double actionPosY = centerY + (28.0 * scale); // Y = 148

        // 3. 取消 (X) 按钮：位于左端点正下方
        BtnCancel.Width = actionBtnSize;
        BtnCancel.Height = actionBtnSize;
        Canvas.SetLeft(BtnCancel, endLeftX - actionBtnSize / 2.0);
        Canvas.SetTop(BtnCancel, actionPosY - actionBtnSize / 2.0);

        // 4. 确认 (✓) 按钮：位于右端点正下方
        BtnConfirm.Width = actionBtnSize;
        BtnConfirm.Height = actionBtnSize;
        Canvas.SetLeft(BtnConfirm, endRightX - actionBtnSize / 2.0);
        Canvas.SetTop(BtnConfirm, actionPosY - actionBtnSize / 2.0);

        // 5. 顶部人数选择圆弧条 (围绕主按钮圆心呈 120° 圆弧展开)
        UpdateArcPickerLayout(centerX, centerY, baseSize);
    }

    private void UpdateArcPickerLayout(double cx, double cy, double baseSize)
    {
        double scale = baseSize / 50.0;
        double rMid = 64.0 * scale;
        double thickness = 34.0 * scale;

        // 圆心角改为 120 度 (-150.0° 至 -30.0°)，两端为顺滑凸圆头胶囊封口
        ArcBackground.Data = CreateArcCapsuleGeometry(cx, cy, rMid, thickness, -150.0, -30.0);

        // 弧形径向分割线 1 与 4 (位于 MIN 与 - 之间，以及 + 与 MAX 之间)
        SetRadialDivider(Divider1, cx, cy, rMid, -130.0, 7.0 * scale);
        Divider1.Visibility = Visibility.Visible;
        Divider2.Visibility = Visibility.Collapsed;
        Divider3.Visibility = Visibility.Collapsed;
        SetRadialDivider(Divider4, cx, cy, rMid, -50.0, 7.0 * scale);
        Divider4.Visibility = Visibility.Visible;

        // 按钮及徽章定位：内容不倾斜，默认水平正向放置 (Angle = 0)
        // +/- 按钮距中心徽章各 30° 间距；MIN/MAX 位于两端头部，径向半径固定为 rMid (64.0)，垂直/径向完美居中
        PositionOnArc(BtnMin, cx, cy, rMid, -140.0, 0.0);
        PositionOnArc(BtnMinus, cx, cy, rMid, -120.0, 0.0);
        PositionCountBorder(CountBorder, CountRotate, cx, cy, rMid, -90.0, 0.0);
        PositionOnArc(BtnPlus, cx, cy, rMid, -60.0, 0.0);
        PositionOnArc(BtnMax, cx, cy, rMid, -40.0, 0.0);
    }

    private static PathGeometry CreateArcCapsuleGeometry(double cx, double cy, double rMid, double thickness, double startDeg, double endDeg)
    {
        double hT = thickness / 2.0;
        double rIn = rMid - hT;
        double rOut = rMid + hT;

        double radStart = startDeg * Math.PI / 180.0;
        double radEnd = endDeg * Math.PI / 180.0;

        Point pInStart = new(cx + rIn * Math.Cos(radStart), cy + rIn * Math.Sin(radStart));
        Point pInEnd = new(cx + rIn * Math.Cos(radEnd), cy + rIn * Math.Sin(radEnd));
        Point pOutEnd = new(cx + rOut * Math.Cos(radEnd), cy + rOut * Math.Sin(radEnd));
        Point pOutStart = new(cx + rOut * Math.Cos(radStart), cy + rOut * Math.Sin(radStart));

        var figure = new PathFigure
        {
            StartPoint = pInStart,
            IsClosed = true,
            IsFilled = true
        };

        // 1. 沿内弧顺时针至右端 (0°)
        figure.Segments.Add(new ArcSegment(pInEnd, new Size(rIn, rIn), 0, false, SweepDirection.Clockwise, true));
        // 2. 右侧半圆封头 (半径 hT) - Counterclockwise 向下凸出圆滑胶囊端头
        figure.Segments.Add(new ArcSegment(pOutEnd, new Size(hT, hT), 0, false, SweepDirection.Counterclockwise, true));
        // 3. 沿外弧逆时针至左端 (-180°)
        figure.Segments.Add(new ArcSegment(pOutStart, new Size(rOut, rOut), 0, false, SweepDirection.Counterclockwise, true));
        // 4. 左侧半圆封头 (半径 hT) - Counterclockwise 向下凸出圆滑胶囊端头
        figure.Segments.Add(new ArcSegment(pInStart, new Size(hT, hT), 0, false, SweepDirection.Counterclockwise, true));

        var geom = new PathGeometry();
        geom.Figures.Add(figure);
        geom.Freeze();
        return geom;
    }

    private static void SetRadialDivider(Line line, double cx, double cy, double rMid, double deg, double halfLen)
    {
        double rad = deg * Math.PI / 180.0;
        line.X1 = cx + (rMid - halfLen) * Math.Cos(rad);
        line.Y1 = cy + (rMid - halfLen) * Math.Sin(rad);
        line.X2 = cx + (rMid + halfLen) * Math.Cos(rad);
        line.Y2 = cy + (rMid + halfLen) * Math.Sin(rad);
    }

    private static void PositionOnArc(FrameworkElement elem, double cx, double cy, double rMid, double deg, double tiltFactor = 0.0)
    {
        double rad = deg * Math.PI / 180.0;
        double x = cx + rMid * Math.Cos(rad);
        double y = cy + rMid * Math.Sin(rad);

        Canvas.SetLeft(elem, x - elem.Width / 2.0);
        Canvas.SetTop(elem, y - elem.Height / 2.0);

        elem.RenderTransformOrigin = new Point(0.5, 0.5);
        if (tiltFactor != 0.0)
        {
            double tiltDeg = (deg + 90.0) * tiltFactor;
            elem.RenderTransform = new RotateTransform(tiltDeg);
        }
        else
        {
            elem.RenderTransform = Transform.Identity;
        }
    }

    private static void PositionCountBorder(FrameworkElement elem, RotateTransform rotate, double cx, double cy, double rMid, double deg, double tiltFactor = 0.0)
    {
        double rad = deg * Math.PI / 180.0;
        double x = cx + rMid * Math.Cos(rad);
        double y = cy + rMid * Math.Sin(rad);

        Canvas.SetLeft(elem, x - elem.Width / 2.0);
        Canvas.SetTop(elem, y - elem.Height / 2.0);

        rotate.Angle = 0.0;
    }

    /// <summary>
    /// 热更新按钮外观配置
    /// </summary>
    public void ApplyProps(FloatingButtonConfig props)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                // 1. 尺寸计算与动态几何排版 (基准 50px)
                double scale = props.SizePercent / 100.0;
                double baseSize = 50.0 * scale;
                UpdateGeometry(baseSize);

                // 2. 透明度与置顶
                Opacity = Math.Clamp(1.0 - (props.TransparencyPercent / 100.0), 0.1, 1.0);
                Topmost = props.AlwaysOnTop;
                ShowInTaskbar = props.ShowInTaskbar;

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
                MainButtonBorder.BorderBrush = new SolidColorBrush(borderColor);

                // 4. 图标：优先加载自定义图标 (CustomIconId)，其次 BlueRandom.png
                double iconSize = (props.IconSize > 0 ? props.IconSize : 40.0) * scale;
                ButtonIcon.Width = iconSize;
                ButtonIcon.Height = iconSize;

                ImageSource? iconImg = null;
                if (!string.IsNullOrEmpty(props.CustomIconId))
                {
                    var custom = CustomsStore.LoadCustom(props.CustomIconId);
                    if (custom != null)
                    {
                        byte[]? bytes = custom.Bytes;
                        if (bytes != null && bytes.Length > 0)
                        {
                            try
                            {
                                using var ms = new MemoryStream(bytes);
                                ms.Position = 0;
                                var bmp = new BitmapImage();
                                bmp.BeginInit();
                                bmp.CacheOption = BitmapCacheOption.OnLoad;
                                bmp.StreamSource = ms;
                                bmp.EndInit();
                                bmp.Freeze();
                                iconImg = bmp;
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Warn("FCB", $"加载自定义图标解码异常: {ex.Message}");
                                iconImg = null;
                            }
                        }
                    }
                }

                if (iconImg == null)
                {
                    iconImg = ResourceCache.GetImage("BlueRandom.png") ?? ResourceCache.GetImage("app.ico");
                }

                if (iconImg != null)
                {
                    ButtonIcon.Source = iconImg;
                }

                // 5. 初始坐标
                if (props.Position.X.HasValue && props.Position.Y.HasValue)
                {
                    Left = props.Position.X.Value;
                    Top = props.Position.Y.Value;
                }
                else
                {
                    // 默认停靠在右下角
                    double padX = (Width - MainButtonBorder.Width) / 2.0;
                    double padY = (Height - MainButtonBorder.Height) / 2.0;
                    Left = SystemParameters.WorkArea.Right - Width + padX - 30;
                    Top = SystemParameters.WorkArea.Bottom - Height + padY - 80;
                }
                ClampPosition();
            }
            catch (Exception ex)
            {
                AppLogger.Error("FCB", "应用按钮样式异常", ex);
            }
        });
    }

    // ========================================================================
    //  拖拽与点击交互
    // ========================================================================

    private void OnMainButtonMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try
            {
                _mouseDownScreenPos = PointToScreen(e.GetPosition(this));
            }
            catch
            {
                _mouseDownScreenPos = new Point(Left, Top);
            }
            _isPressed = true;
            _hasDragged = false;
        }
    }

    private void OnMainButtonMouseMove(object sender, MouseEventArgs e)
    {
        if (_isPressed && e.LeftButton == MouseButtonState.Pressed)
        {
            Point currentScreenPos;
            try
            {
                currentScreenPos = PointToScreen(e.GetPosition(this));
            }
            catch
            {
                return;
            }

            if (!_hasDragged && (Math.Abs(currentScreenPos.X - _mouseDownScreenPos.X) > 4 || Math.Abs(currentScreenPos.Y - _mouseDownScreenPos.Y) > 4))
            {
                _hasDragged = true;
                if (_isPickerOpen) TogglePicker(false);
                try
                {
                    DragMove();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("FCB", "DragMove 异常: " + ex.Message);
                }

                _isPressed = false;
                ClampPosition();
                var payload = new FcbPositionPayload
                {
                    MonitorNumber = 0,
                    PositionX = (int)Left,
                    PositionY = (int)Top
                };
                IpcBroker.Send(IpcMessage.Create(IpcAction.Post, IpcModule.Fcb, "PositionChanged", payload));
            }
        }
    }

    private void OnMainButtonMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            _isPressed = false;
            if (!_hasDragged)
            {
                // 未发生位移，判定为单次点击：展开或收起选择器
                PlayClickSound();
                TogglePicker(!_isPickerOpen);
            }
            _hasDragged = false;
        }
    }

    /// <summary>
    /// 允许透明扩展区越出屏幕边界，让视觉核心的圆形按钮 100% 紧贴屏幕边缘 (复刻 clampBoundsToWorkArea)
    /// </summary>
    private void ClampPosition()
    {
        var workArea = SystemParameters.WorkArea;
        double padX = (Width - MainButtonBorder.Width) / 2.0;
        double padY = (Height - MainButtonBorder.Height) / 2.0;

        double minX = workArea.Left - padX;
        double minY = workArea.Top - padY;
        double maxX = workArea.Right - Width + padX;
        double maxY = workArea.Bottom - Height + padY;

        Left = Math.Clamp(Left, minX, maxX);
        Top = Math.Clamp(Top, minY, maxY);
    }

    // ========================================================================
    //  人数选择器控制
    // ========================================================================

    private void TogglePicker(bool open)
    {
        _isPickerOpen = open;
        if (open)
        {
            PickerCapsule.Visibility = Visibility.Visible;
            BtnCancel.Visibility = Visibility.Visible;
            BtnConfirm.Visibility = Visibility.Visible;

            // 进场动画：由小到大膨胀弹出 (0.22s)
            var fadeIn = new DoubleAnimation(0, 1.0, TimeSpan.FromMilliseconds(220));
            var slideUp = new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var scaleAnim = new DoubleAnimation(0.8, 1.0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var btnScaleAnim = new DoubleAnimation(0.5, 1.0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            PickerCapsule.BeginAnimation(OpacityProperty, fadeIn);
            CapsuleTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);
            CapsuleScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            CapsuleScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);

            BtnCancel.BeginAnimation(OpacityProperty, fadeIn);
            CancelScale.BeginAnimation(ScaleTransform.ScaleXProperty, btnScaleAnim);
            CancelScale.BeginAnimation(ScaleTransform.ScaleYProperty, btnScaleAnim);

            BtnConfirm.BeginAnimation(OpacityProperty, fadeIn);
            ConfirmScale.BeginAnimation(ScaleTransform.ScaleXProperty, btnScaleAnim);
            ConfirmScale.BeginAnimation(ScaleTransform.ScaleYProperty, btnScaleAnim);
        }
        else
        {
            // 退场收缩动画 (0.18s)：圆弧与按钮迅速向中心主按钮收缩收拢，而非单纯淡出
            var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
            var fadeOut = new DoubleAnimation(1.0, 0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = easeIn
            };
            var slideDown = new DoubleAnimation(0, 14, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = easeIn
            };
            var shrinkCapsule = new DoubleAnimation(1.0, 0.65, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = easeIn
            };
            var shrinkBtn = new DoubleAnimation(1.0, 0.35, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = easeIn
            };

            fadeOut.Completed += (_, _) =>
            {
                PickerCapsule.Visibility = Visibility.Collapsed;
                BtnCancel.Visibility = Visibility.Collapsed;
                BtnConfirm.Visibility = Visibility.Collapsed;
            };

            PickerCapsule.BeginAnimation(OpacityProperty, fadeOut);
            CapsuleTranslate.BeginAnimation(TranslateTransform.YProperty, slideDown);
            CapsuleScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrinkCapsule);
            CapsuleScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrinkCapsule);

            BtnCancel.BeginAnimation(OpacityProperty, fadeOut);
            CancelScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrinkBtn);
            CancelScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrinkBtn);

            BtnConfirm.BeginAnimation(OpacityProperty, fadeOut);
            ConfirmScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrinkBtn);
            ConfirmScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrinkBtn);
        }
    }

    private void SetPickCount(int count)
    {
        _pickCount = Math.Clamp(count, 1, 10);
        TxtCount.Text = _pickCount.ToString();
        PlayClickSound();

        // 按钮禁用/启用状态控制 (min=1 禁用 MIN 与 -；max=10 禁用 MAX 与 +)
        BtnMin.IsEnabled = _pickCount > 1;
        BtnMinus.IsEnabled = _pickCount > 1;
        BtnPlus.IsEnabled = _pickCount < 10;
        BtnMax.IsEnabled = _pickCount < 10;

        // 触发脉冲弹跳动画
        if (Resources["CountPulseStoryboard"] is Storyboard sb)
        {
            sb.Begin();
        }
    }

    private void OnMinClick(object sender, RoutedEventArgs e) => SetPickCount(1);
    private void OnMinusClick(object sender, RoutedEventArgs e) => SetPickCount(_pickCount - 1);
    private void OnPlusClick(object sender, RoutedEventArgs e) => SetPickCount(_pickCount + 1);
    private void OnMaxClick(object sender, RoutedEventArgs e) => SetPickCount(10);

    private void OnCancelPickerClick(object sender, RoutedEventArgs e)
    {
        PlayClickSound();
        TogglePicker(false);
    }

    private void OnConfirmPickerClick(object sender, RoutedEventArgs e)
    {
        PlayClickSound();
        TogglePicker(false);
        // 上报抽取请求给核心后端调度器
        IpcBroker.Send(IpcMessage.Create(IpcAction.Post, IpcModule.Fcb, "RandomTriggered", _pickCount));
    }

    // ========================================================================
    //  IPC 指令监听
    // ========================================================================

    private void OnIpcMessageReceived(IpcMessage msg)
    {
        Dispatcher.Invoke(() =>
        {
            switch (msg.Command)
            {
                case "HideWindow":
                    TogglePicker(false);
                    Hide();
                    break;

                case "ShowWindow":
                    Show();
                    var hwnd = new WindowInteropHelper(this).Handle;
                    WindowsSystemService.RefreshTopMost(hwnd);
                    break;

                case "ApplyNewProp":
                    if (msg.Payload is FloatingButtonConfig cfg)
                    {
                        ApplyProps(cfg);
                    }
                    break;

                case "MoveNewPosition":
                    if (msg.Payload is FcbPositionPayload p)
                    {
                        Left = p.PositionX;
                        Top = p.PositionY;
                        ClampPosition();
                    }
                    break;

                case "Destroy":
                    Close();
                    break;
            }
        });
    }
}
