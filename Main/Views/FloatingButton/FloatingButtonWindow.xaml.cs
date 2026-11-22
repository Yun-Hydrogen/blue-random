// ============================================================================
//  File: FloatingButtonWindow.xaml.cs
//  Project: BlueRandom (WPF Native)
//  Description:
//    桌面常驻全局交互核心——透明悬浮球窗体 (FCB: Floating Control Button)。
//
//  Architecture & Implementation Highlights:
//    1. 动态几何排版体系：
//       - 采用 240x240 逻辑透明画布，以圆心 (120, 120) 为基准原点，承载 50px 基础核心按钮；
//       - 围绕圆心构建 -150° 至 -30° 的 120° 顺滑圆头胶囊弧形选择器（ArcCapsuleGeometry）；
//       - 确认/取消操作按钮动态映射于弧形两侧端点正下方，确保视觉层次与操作重心的平衡；
//    2. 屏幕吸附与边缘裁剪 (Edge Clamping)：
//       - 采用 padding 补偿边界算法，允许透明扩展区越出屏幕边界，让视觉核心按钮能 100% 紧贴工作区边缘；
//    3. 微交互与硬件加速动效：
//       - 具有 0.22s 进场三次贝塞尔平滑膨胀与 0.18s 径向快速聚拢退场动画；
//       - 支持内存 DirectSound 点击音效回放与按钮脉冲缩放；
//    4. 跨进程与置顶保活：
//       - 内置每秒 DispatcherTimer 定时器，调用 Win32 SetWindowPos(HWND_TOPMOST) 抵御全屏抢占。
// ============================================================================

using System;
using System.ComponentModel;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace BlueRandom.Views.FloatingButton;

/// <summary>
/// 桌面常驻全局透明悬浮按钮窗体。
/// </summary>
public partial class FloatingButtonWindow : Window
{
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_MAXIMIZEBOX = 0x00010000;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private readonly FloatingButtonController _controller;
    private Point _mouseDownScreenPos;
    private bool _isMousePressed = false;
    private bool _hasMouseDragged = false;
    private int? _activeTouchId = null;
    private Point _touchDownScreenPos;
    private Point _touchStartWindowPos;
    private bool _isTouchPressed = false;
    private bool _hasTouchDragged = false;
    private int _pickerAnimVersion = 0;
    private bool _isRealClose = false;
    private SoundPlayer? _clickSoundPlayer;

    /// <summary>
    /// 悬浮按钮业务与控制逻辑控制器。
    /// </summary>
    internal FloatingButtonController Controller => _controller;

    /// <summary>
    /// 人数选择胶囊是否正处于展开状态。
    /// </summary>
    public bool IsPickerOpen => _controller.IsPickerOpen;

    /// <summary>
    /// 当前选中的抽取人数。
    /// </summary>
    public int PickCount => _controller.PickCount;

    /// <summary>
    /// 初始化悬浮窗体实例，挂载事件监听并预加载音频与图标资源。
    /// </summary>
    public FloatingButtonWindow()
    {
        InitializeComponent();

        _controller = new FloatingButtonController(this);

        // 构造函数即刻注册 IPC 消息监听，规避 Loaded 陷阱
        IpcBroker.Subscribe(IpcModule.Fcb, OnIpcMessageReceived);
        Closed += (_, _) => IpcBroker.Unsubscribe(IpcModule.Fcb, OnIpcMessageReceived);

        // 注入 WS_EX_NOACTIVATE 扩展样式，彻底消除悬浮按钮拖动与点击时的前台焦点抢夺
        SourceInitialized += OnSourceInitialized;

        // 禁用 WPF 对触摸的系统级长按反馈与手势提升，消除 Windows 触控圆圈与轻扫干扰
        Stylus.SetIsPressAndHoldEnabled(MainButtonBorder, false);
        Stylus.SetIsFlicksEnabled(this, false);
        Stylus.SetIsTapFeedbackEnabled(this, false);
        Stylus.SetIsTouchFeedbackEnabled(this, false);

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
            if (_controller.IsPickerOpen) TogglePicker(false);
        };

        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) ResetPointerStates();
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

    /// <summary>
    /// 注入 Win32 样式与无焦点扩展属性：
    /// 1. 剥离最大化与可调边框样式，注入 WS_EX_TOOLWINDOW，配合 WM_GETMINMAXINFO 锁定尺寸彻底禁用左右边缘分屏贴靠；
    /// 2. 注入 WS_EX_NOACTIVATE 并拦截 WM_MOUSEACTIVATE，确保拖动与点击悬浮球绝不抢夺前台放映 PPT 的焦点。
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            // 剥离 WS_MAXIMIZEBOX、WS_MINIMIZEBOX 与 WS_THICKFRAME
            long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            style &= ~WS_MAXIMIZEBOX;
            style &= ~WS_MINIMIZEBOX;
            style &= ~WS_THICKFRAME;
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));

            // 注入 WS_EX_TOOLWINDOW（工具窗口不参与分屏）与 WS_EX_NOACTIVATE（无焦点）
            long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            exStyle |= WS_EX_TOOLWINDOW;
            exStyle |= WS_EX_NOACTIVATE;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle));

            var source = HwndSource.FromHwnd(hwnd);
            source?.AddHook(WndProc);
        }
    }

    /// <summary>
    /// Win32 消息过滤：
    /// 1. WM_MOUSEACTIVATE: 拦截激活请求，保持前台原有窗口获得焦点；
    /// 2. WM_GETMINMAXINFO: 锁定最大追踪尺寸为固定尺寸，阻止 Windows 对悬浮窗进行左右半屏分屏拉伸。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                handled = true;
                return (IntPtr)MA_NOACTIVATE;

            case WM_GETMINMAXINFO:
                MINMAXINFO mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                uint dpi = GetDpiForWindow(hwnd);
                double scale = dpi == 0 ? 1.0 : dpi / 96.0;
                int fixedW = (int)Math.Round(Width * scale);
                int fixedH = (int)Math.Round(Height * scale);
                mmi.ptMinTrackSize.X = fixedW;
                mmi.ptMinTrackSize.Y = fixedH;
                mmi.ptMaxTrackSize.X = fixedW;
                mmi.ptMaxTrackSize.Y = fixedH;
                mmi.ptMaxSize.X = fixedW;
                mmi.ptMaxSize.Y = fixedH;
                Marshal.StructureToPtr(mmi, lParam, true);
                handled = true;
                return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 拦截 Alt+F4 系统关闭组合键，重定向为隐藏窗口而非销毁。
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.System && e.SystemKey == Key.F4)
        {
            e.Handled = true;
            TogglePicker(false);
            Hide();
        }
    }

    /// <summary>
    /// 拦截窗体关闭请求，将其重定向为隐藏（Hide），保护窗口实例存活以供托盘随时恢复。
    /// 仅在收到宿主 Destroy 命令时（_isRealClose 为 true）才允许物理关闭。
    /// </summary>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_isRealClose)
        {
            e.Cancel = true;
            TogglePicker(false);
            Hide();
        }
    }

    /// <summary>
    /// 从全内存资源缓存中初始化按键音效播放器。
    /// </summary>
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

    /// <summary>
    /// 窗体完成呈现后的初次布局同步与 Win32 置顶看门狗定时器启动。
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyProps(ConfigCache.GetFcbProps());
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

    /// <summary>
    /// 触发按键交互短音效回放。
    /// </summary>
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
    /// 依据基准尺寸动态计算所有环绕控件的中心坐标 (1:1 复刻 Floating.vue pickerMetrics 并升级 180° 圆弧)。
    /// </summary>
    /// <param name="baseSize">中心主按钮基础直径尺寸（px）。</param>
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

    /// <summary>
    /// 动态生成并布局 -150° 至 -30° 的 120 度圆弧胶囊条、径向刻度分割线以及加减操作按钮。
    /// </summary>
    /// <param name="cx">主圆心 X 坐标。</param>
    /// <param name="cy">主圆心 Y 坐标。</param>
    /// <param name="baseSize">中心主按钮基础尺寸。</param>
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

    /// <summary>
    /// 数学构建两端具备圆滑外凸半圆封头的环状弧形胶囊 PathGeometry 矢量图形。
    /// </summary>
    /// <param name="cx">圆心 X。</param>
    /// <param name="cy">圆心 Y。</param>
    /// <param name="rMid">弧线中心轨迹半径。</param>
    /// <param name="thickness">胶囊厚度（径向环宽）。</param>
    /// <param name="startDeg">起始角度（度）。</param>
    /// <param name="endDeg">终止角度（度）。</param>
    /// <returns>构建完成且冻结 (Freeze) 的 WPF PathGeometry 实例。</returns>
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

    /// <summary>
    /// 计算并设置沿圆心径向法线方向的分割线段端点坐标。
    /// </summary>
    private static void SetRadialDivider(Line line, double cx, double cy, double rMid, double deg, double halfLen)
    {
        double rad = deg * Math.PI / 180.0;
        line.X1 = cx + (rMid - halfLen) * Math.Cos(rad);
        line.Y1 = cy + (rMid - halfLen) * Math.Sin(rad);
        line.X2 = cx + (rMid + halfLen) * Math.Cos(rad);
        line.Y2 = cy + (rMid + halfLen) * Math.Sin(rad);
    }

    /// <summary>
    /// 依极坐标将指定的 UI 元素中心定位到圆弧轨迹特定角度上，支持可选切向倾角变换。
    /// </summary>
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

    /// <summary>
    /// 定位人数展示徽章并将其旋转角度归零锁定为水平正向。
    /// </summary>
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
    /// 热更新按钮外观配置，转发至控制器处理业务状态与配置归一化。
    /// </summary>
    /// <param name="props">悬浮按钮配置数据对象。</param>
    public void ApplyProps(FloatingButtonConfig props)
    {
        Dispatcher.Invoke(() =>
        {
            _controller.ApplyProps(props);
        });
    }

    /// <summary>
    /// 应用控制器计算好的视觉属性（尺寸几何、透明度、置顶、边框、图标及初始坐标）。
    /// </summary>
    internal void ApplyVisualProps(
        double baseSize,
        double opacity,
        bool topmost,
        bool showInTaskbar,
        Color borderColor,
        double iconSize,
        ImageSource? iconImg,
        double left,
        double top)
    {
        // 1. 尺寸计算与动态几何排版 (基准 50px)
        UpdateGeometry(baseSize);

        // 2. 透明度与置顶
        Opacity = opacity;
        Topmost = topmost;
        ShowInTaskbar = showInTaskbar;

        // 3. 边框颜色
        MainButtonBorder.BorderBrush = new SolidColorBrush(borderColor);

        // 4. 图标
        ButtonIcon.Width = iconSize;
        ButtonIcon.Height = iconSize;
        if (iconImg != null)
        {
            ButtonIcon.Source = iconImg;
        }

        // 5. 初始坐标
        Left = left;
        Top = top;
        ClampPosition();
    }

    // ========================================================================
    //  拖拽与点击交互 (鼠标走原生 DragMove；触控独立处理并阻断向鼠标事件提升)
    // ========================================================================

    private static bool IsTouchPromotedMouseEvent(MouseEventArgs e)
        => e.StylusDevice?.TabletDevice?.Type == TabletDeviceType.Touch;

    private void ResetPointerStates()
    {
        _isMousePressed = false;
        _hasMouseDragged = false;
        _isTouchPressed = false;
        _hasTouchDragged = false;
        _activeTouchId = null;
    }

    private Point GetScreenDipPosition(Point windowRelativeDip)
    {
        try
        {
            Point screenPx = PointToScreen(windowRelativeDip);
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                return source.CompositionTarget.TransformFromDevice.Transform(screenPx);
            }
            return screenPx;
        }
        catch
        {
            return new Point(Left + windowRelativeDip.X, Top + windowRelativeDip.Y);
        }
    }

    private void SendPositionChanged()
    {
        var payload = new FcbPositionPayload
        {
            MonitorNumber = 0,
            PositionX = (int)Left,
            PositionY = (int)Top
        };
        IpcBroker.Send(IpcMessage.Create(IpcAction.Post, IpcModule.Fcb, "PositionChanged", payload));
    }

    /// <summary>
    /// 鼠标左键按下事件：记录屏幕绝对坐标起点，准备拖拽或点击判定。
    /// </summary>
    private void OnMainButtonMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsTouchPromotedMouseEvent(e)) return;

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
            _isMousePressed = true;
            _hasMouseDragged = false;
        }
    }

    /// <summary>
    /// 鼠标移动事件：超过 4 像素移动阈值时自动触发 Windows 原生平滑拖拽，并在用户松手后贴紧边缘上报坐标。
    /// </summary>
    private void OnMainButtonMouseMove(object sender, MouseEventArgs e)
    {
        if (IsTouchPromotedMouseEvent(e)) return;

        if (_isMousePressed && e.LeftButton == MouseButtonState.Pressed)
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

            if (!_hasMouseDragged && (Math.Abs(currentScreenPos.X - _mouseDownScreenPos.X) > 4 || Math.Abs(currentScreenPos.Y - _mouseDownScreenPos.Y) > 4))
            {
                _hasMouseDragged = true;
                if (_controller.IsPickerOpen) TogglePicker(false);
                try
                {
                    DragMove();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("FCB", "DragMove 异常: " + ex.Message);
                }

                _isMousePressed = false;
                ClampPosition();
                SendPositionChanged();
            }
        }
    }

    /// <summary>
    /// 鼠标左键抬起事件：若未发生位移，判定为单次点击切换展开/收起。
    /// </summary>
    private void OnMainButtonMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (IsTouchPromotedMouseEvent(e)) return;

        if (e.ChangedButton == MouseButton.Left)
        {
            bool wasPressed = _isMousePressed;
            bool hasDragged = _hasMouseDragged;
            _isMousePressed = false;
            _hasMouseDragged = false;

            if (wasPressed && !hasDragged)
            {
                // 未发生位移，判定为单次点击：展开或收起选择器
                PlayClickSound();
                TogglePicker(!_controller.IsPickerOpen);
            }
        }
    }

    /// <summary>
    /// 触控按下事件：捕获触点并标记 Handled，阻止 WPF 将触控提升为鼠标事件导致二次触发。
    /// </summary>
    private void OnMainButtonTouchDown(object? sender, TouchEventArgs e)
    {
        e.Handled = true;
        if (_activeTouchId != null) return;

        var touchPoint = e.GetTouchPoint(this);
        _activeTouchId = e.TouchDevice.Id;
        _touchDownScreenPos = GetScreenDipPosition(touchPoint.Position);
        _touchStartWindowPos = new Point(Left, Top);
        _isTouchPressed = true;
        _hasTouchDragged = false;
        MainButtonBorder.CaptureTouch(e.TouchDevice);
    }

    /// <summary>
    /// 触控移动事件：超过 4 像素移动阈值时直接按 DIP 增量平滑移动窗口并贴紧工作区边界。
    /// </summary>
    private void OnMainButtonTouchMove(object? sender, TouchEventArgs e)
    {
        if (!_isTouchPressed || _activeTouchId != e.TouchDevice.Id) return;
        e.Handled = true;

        var touchPoint = e.GetTouchPoint(this);
        Point currentScreenPos = GetScreenDipPosition(touchPoint.Position);
        double dx = currentScreenPos.X - _touchDownScreenPos.X;
        double dy = currentScreenPos.Y - _touchDownScreenPos.Y;

        if (!_hasTouchDragged && (Math.Abs(dx) > 4 || Math.Abs(dy) > 4))
        {
            _hasTouchDragged = true;
            if (_controller.IsPickerOpen) TogglePicker(false);
        }

        if (_hasTouchDragged)
        {
            Left = _touchStartWindowPos.X + dx;
            Top = _touchStartWindowPos.Y + dy;
            ClampPosition();
        }
    }

    /// <summary>
    /// 触控抬起事件：标记 Handled 阻断鼠标提升；未拖拽时切换选择器展开状态，拖拽后上报新坐标。
    /// </summary>
    private void OnMainButtonTouchUp(object? sender, TouchEventArgs e)
    {
        if (!_isTouchPressed || _activeTouchId != e.TouchDevice.Id) return;
        e.Handled = true;
        FinishTouchInteraction(e.TouchDevice, isCancel: false);
    }

    /// <summary>
    /// 触控捕获意外丢失时清理状态；若已发生拖拽则收敛位置并上报。
    /// </summary>
    private void OnMainButtonLostTouchCapture(object? sender, TouchEventArgs e)
    {
        if (!_isTouchPressed || _activeTouchId != e.TouchDevice.Id) return;
        FinishTouchInteraction(e.TouchDevice, isCancel: true);
    }

    private void FinishTouchInteraction(TouchDevice touchDevice, bool isCancel)
    {
        bool hasDragged = _hasTouchDragged;
        _isTouchPressed = false;
        _hasTouchDragged = false;
        _activeTouchId = null;

        if (Equals(touchDevice.Captured, MainButtonBorder))
        {
            MainButtonBorder.ReleaseTouchCapture(touchDevice);
        }

        if (hasDragged)
        {
            ClampPosition();
            SendPositionChanged();
        }
        else if (!isCancel)
        {
            PlayClickSound();
            TogglePicker(!_controller.IsPickerOpen);
        }
    }

    /// <summary>
    /// 允许透明扩展区越出屏幕边界，让视觉核心的圆形按钮 100% 紧贴屏幕边缘 (复刻 clampBoundsToWorkArea)。
    /// </summary>
    internal void ClampPosition()
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

    /// <summary>
    /// 展开或收缩人数选择胶囊，驱动 CubicEase 贝塞尔弹性动效与渐变。
    /// </summary>
    /// <param name="open">true 为展开，false 为收拢。</param>
    internal void TogglePicker(bool open)
    {
        _controller.IsPickerOpen = open;
        int version = ++_pickerAnimVersion;
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
                if (version != _pickerAnimVersion) return;
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

    /// <summary>
    /// 应用人数选择器的视觉状态（文本、按钮启用状态及脉冲动画）。
    /// </summary>
    /// <param name="count">目标抽取人数 (1-10)。</param>
    internal void ApplyPickCountVisual(int count)
    {
        TxtCount.Text = count.ToString();
        PlayClickSound();

        // 按钮禁用/启用状态控制 (min=1 禁用 MIN 与 -；max=10 禁用 MAX 与 +)
        BtnMin.IsEnabled = count > 1;
        BtnMinus.IsEnabled = count > 1;
        BtnPlus.IsEnabled = count < 10;
        BtnMax.IsEnabled = count < 10;

        // 触发脉冲弹跳动画
        if (Resources["CountPulseStoryboard"] is Storyboard sb)
        {
            sb.Begin();
        }
    }

    /// <summary>
    /// 设置抽取人数（转发至控制器）。
    /// </summary>
    private void SetPickCount(int count) => _controller.SetPickCount(count);

    /// <summary>
    /// 热更新配置面板设置的默认抽取人数；人数选择器展开时不打断用户当前调整。
    /// </summary>
    public void ApplyDefaultPickCount(int count)
    {
        Dispatcher.Invoke(() =>
        {
            _controller.ApplyDefaultPickCount(count);
        });
    }

    /// <summary>
    /// 快捷设置抽取人数为下限 1 人。
    /// </summary>
    private void OnMinClick(object sender, RoutedEventArgs e) => _controller.SetPickCount(1);

    /// <summary>
    /// 抽取人数步进减 1。
    /// </summary>
    private void OnMinusClick(object sender, RoutedEventArgs e) => _controller.StepPickCount(-1);

    /// <summary>
    /// 抽取人数步进加 1。
    /// </summary>
    private void OnPlusClick(object sender, RoutedEventArgs e) => _controller.StepPickCount(1);

    /// <summary>
    /// 快捷设置抽取人数为上限 10 人。
    /// </summary>
    private void OnMaxClick(object sender, RoutedEventArgs e) => _controller.SetPickCount(10);

    /// <summary>
    /// 点击取消按钮：关闭选择器。
    /// </summary>
    private void OnCancelPickerClick(object sender, RoutedEventArgs e)
    {
        PlayClickSound();
        TogglePicker(false);
    }

    /// <summary>
    /// 点击确认按钮：收拢选择器并向后端广播 RandomTriggered 抽取触发事件。
    /// </summary>
    private void OnConfirmPickerClick(object sender, RoutedEventArgs e)
    {
        PlayClickSound();
        TogglePicker(false);
        // 上报抽取请求给核心后端调度器
        _controller.ConfirmPick();
    }

    // ========================================================================
    //  IPC 指令监听
    // ========================================================================

    /// <summary>
    /// 处理面向悬浮球模块的 IPC 消息分发（HideWindow, ShowWindow, ApplyProps, MoveNewPosition, Destroy）。
    /// </summary>
    /// <param name="msg">IPC 消息包。</param>
    private void OnIpcMessageReceived(IpcMessage msg)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished || !Dispatcher.Thread.IsAlive)
        {
            IpcBroker.Unsubscribe(IpcModule.Fcb, OnIpcMessageReceived);
            return;
        }

        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                switch (msg.Command)
                {
                    case "HideWindow":
                        TogglePicker(false);
                        Hide();
                        break;

                    case "ShowWindow":
                        if (!IsVisible)
                        {
                            Show();
                        }
                        var hwnd = new WindowInteropHelper(this).Handle;
                        WindowsSystemService.RefreshTopMost(hwnd);
                        break;

                    case "ApplyProps":
                        if (msg.Payload is FloatingButtonConfig cfg)
                        {
                            ApplyProps(cfg);
                        }
                        break;

                    case "ApplyDefaultPickCount":
                        if (msg.Payload is int count)
                        {
                            ApplyDefaultPickCount(count);
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
                        Destroy();
                        break;
                }
            }));
        }
        catch (Exception ex)
        {
            AppLogger.Warn("FCB", "处理 IPC 消息异常: " + ex.Message);
        }
    }

    /// <summary>
    /// 触发窗口真实物理关闭并注销 IPC 监听。
    /// </summary>
    internal void Destroy()
    {
        _isRealClose = true;
        IpcBroker.Unsubscribe(IpcModule.Fcb, OnIpcMessageReceived);
        Closing -= OnWindowClosing;
        Close();
    }
}
