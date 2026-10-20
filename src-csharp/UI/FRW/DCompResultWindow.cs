// ==============================================================================
// 文件: DCompResultWindow.cs
// 命名空间: BlueRandom.UI.FRW
// 作用: 基于 DirectComposition + D3D11 + Direct2D 的招募结果浮窗（重写版）。
//
// 架构（重写要点）：
//   1. 窗口：普通 Win32 窗口（WS_EX_NOREDIRECTIONBITMAP），内容由 DirectComposition
//      每像素 Alpha 合成，全程 GPU，无 WPF layered 窗口的软件光栅化。
//   2. 渲染：整窗 **单张 IDCompositionSurface + 单个 Visual**，采用即时模式绘制——
//      每帧把预渲染好的 D2D 位图按图层顺序（底 → 顶）画进同一张 surface，
//      彻底规避"多 surface 在部分驱动上只有少数被合成上屏"的问题。
//   3. 图层：每个元素一次性预渲染为 WPF 位图 → 上传为 ID2D1Bitmap1（预乘 Alpha）。
//      动画只改 Matrix3x2 与不透明度，不重建位图、不触发重新布局。
//   4. 坐标：布局一律用 DIP；surface 尺寸 = 窗口物理像素；
//      绘制变换 = layerTransform(DIP) * Scale(dpiScale)（行向量：左先右后）。
//   5. 光标：窗口类显式指定 IDC_ARROW，并在 WM_SETCURSOR / WM_MOUSEMOVE / 每帧渲染后
//      统一刷新。注意 SetCursor 只能接收 LoadCursorW 返回的真实 HCURSOR，传 IDC_* 资源 ID
//      会静默失败，导致继承的进程级等待光标（转圈）无法被纠正。
//   6. 重建：每次展示前释放旧 surface 与全部图层位图，杜绝上一轮结果残留。
//
// 交互：
//   - 就绪后点击屏幕任意位置（WH_MOUSE_LL 低级钩子，安装在专职消息泵线程）或按 Esc 关闭；
//   - 钩子回调仅做标志检查 + PostMessage（微秒级），就绪时吞掉点击不透传下层，未就绪原样放行；
//   - 提示胶囊位于面板正下方（面板外）；就绪后整窗手型光标。
// ==============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using BlueRandom.Core.Config;
using BlueRandom.Core.Ipc;
using BlueRandom.Core.Logging;
using BlueRandom.Core.Resources;
using BlueRandom.Services;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using D3DDriverType = Vortice.Direct3D.DriverType;
using D3DFeatureLevel = Vortice.Direct3D.FeatureLevel;
using D2DColor4 = Vortice.Mathematics.Color4;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using SizeI = Vortice.Mathematics.SizeI;

namespace BlueRandom.UI.FRW;

/// <summary>
/// DirectComposition 招募结果浮窗。在独立 STA 线程运行（WindowManager 负责创建线程并调用 <see cref="Run"/>）。
/// </summary>
public sealed class DCompResultWindow
{
    // ========================================================================
    //  1. Win32 互操作
    // ========================================================================

    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    private const int WM_DESTROY = 0x0002;
    private const int WM_CLOSE = 0x0010;
    private const int WM_ERASEBKGND = 0x0014;
    private const int WM_PAINT = 0x000F;
    private const int WM_TIMER = 0x0113;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SETCURSOR = 0x0020;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_APP_EXEC = 0x8000 + 1;
    private const int HTCLIENT = 0x0001;
    private const int VK_ESCAPE = 0x1B;
    private const uint TIMER_ANIM = 0xB1D0;

    // 低级鼠标钩子（WH_MOUSE_LL）：就绪后监听全屏点击
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN_LL = 0x0201;
    private const int WM_RBUTTONDOWN_LL = 0x0204;
    private const int WM_MBUTTONDOWN_LL = 0x0207;
    private const int WM_XBUTTONDOWN_LL = 0x020B;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    // 预定义光标资源 ID（MAKEINTRESOURCE），只能交给 LoadCursorW；
    // SetCursor 必须传 LoadCursorW 返回的真实 HCURSOR，直接传 ID 会静默失败。
    private static readonly IntPtr IDC_ARROW = new(32512);
    private static readonly IntPtr IDC_HAND = new(32649);

    private static IntPtr _arrowCursor;
    private static IntPtr _handCursor;

    private static IntPtr ArrowCursor =>
        _arrowCursor != IntPtr.Zero ? _arrowCursor : (_arrowCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW));

    private static IntPtr HandCursor =>
        _handCursor != IntPtr.Zero ? _handCursor : (_handCursor = LoadCursorW(IntPtr.Zero, IDC_HAND));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);

    private static WndProc? s_wndProc;
    private static IntPtr s_hwnd;
    private static DCompResultWindow? s_instance;

    // ========================================================================
    //  2. 图形设备
    // ========================================================================

    private ID3D11Device? _d3dDevice;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _resDc;         // 仅用于创建 D2D 位图资源
    private IDCompositionDevice? _dcomp;
    private IDCompositionDesktopDevice? _desktop;
    private IDCompositionTarget? _target;
    private IDCompositionVisual? _rootVisual;
    private IDCompositionVisual3? _rootVisual3;

    private IDCompositionSurface? _surface;     // 整窗唯一 surface（物理像素）
    private IDCompositionVisual? _surfaceVisual;
    private int _surfaceW;
    private int _surfaceH;

    // ========================================================================
    //  3. 图层与布局状态
    // ========================================================================

    /// <summary>渲染图层：预乘 Alpha 的 D2D 位图 + DIP 变换 + 不透明度。</summary>
    private sealed class Layer
    {
        public required ID2D1Bitmap1 Bitmap;
        public double Width;                     // DIP
        public double Height;                    // DIP
        public Matrix3x2 Transform = Matrix3x2.Identity;
        public float Opacity;
        public BitmapSource? Source;             // 仅用于离线诊断导出
    }

    private readonly List<Layer> _layers = new();     // 绘制顺序：索引 0 在最底层
    private Layer? _panelLayer;                       // 面板
    private Layer? _hintLayer;
    private readonly List<Layer> _cardLayers = new();
    private readonly List<Layer> _nameLayers = new();
    private readonly List<(double X, double Y)> _cardCenters = new();

    private enum Phase { Idle, Opening, Ready, Closing }

    private Phase _phase = Phase.Idle;
    private List<CalledStudent> _students = new();
    private PickResultDialogConfig _props = new();
    private readonly Stopwatch _clock = new();
    private double _phaseStartMs;
    private System.Threading.Timer? _tickTimer;
    private int _tickPending;               // WM_TIMER 在途标志（0=无在途，1=已投递未处理）
    private bool _canClose;
    private readonly ConcurrentQueue<Action> _uiQueue = new();

    // 全屏点击检测（WH_MOUSE_LL 低级钩子，安装在专职消息泵线程，回调微秒级返回）
    private LowLevelMouseProc? _mouseHookProc;   // 防 GC 回收委托
    private IntPtr _mouseHook = IntPtr.Zero;
    private Thread? _hookThread;                 // 专职钩子线程（保证回调及时，不拖累鼠标输入）
    private volatile bool _hookReady;            // 就绪标志（钩子线程读取，volatile 保证可见性）
    private uint _hookThreadId;                  // 钩子线程 Win32 线程 ID（用于投递 WM_QUIT 结束泵）

    // 窗口 / 面板（DIP）
    private double _dpiScale = 1.0;
    private double _winW = 900;
    private double _winH = 620;
    private double _winX;
    private double _winY;
    private double _panelW;
    private double _panelH;
    private double _panelLeft;
    private double _panelTop;
    private double _hintW;
    private double _hintX;
    private double _hintY;

    // 布局常量（与 WPF 原版一致）
    private const double CARD_W = 196;
    private const double CARD_H = 147;
    private const double COL_GAP = 32;
    private const double ROW_GAP = 44;
    private const double PANEL_BAND_X = 48 * 2 + 4 * 2;
    private const double PANEL_BAND_Y = 24 + 20 + 4 * 2;
    private const double HINT_BAND = 32 + 24 + 2;
    private const double HINT_H = 32;
    private const double HINT_GAP = 16;     // 提示条与面板的间距（提示条位于面板外正下方）
    private const double MARGIN_X = 160;
    private const double MARGIN_Y = 120;
    private const int NAME_PAD = 12;

    // 动画常量
    private const double PANEL_MS = 700;
    private const double CARD_MS = 600;
    private const double CARD_STAGGER_MS = 120;
    private const double NAME_MS = 300;
    private const double CLOSE_MS = 200;
    private const double CARD_BASE_SCALE = 152.0 / 264.0;   // 阴影图 264×304 → 显示 152×175

    // 音频
    private MediaPlayer? _sfxPlayer;
    private MediaPlayer? _bgmPlayer;
    private double _bgmTargetVolume;
    private double _bgmCurrentVolume;
    private double _bgmFadeSec = 1.5;
    private bool _bgmFadingIn;
    private bool _bgmFadingOut;

    // ========================================================================
    //  4. 生命周期
    // ========================================================================

    public DCompResultWindow()
    {
        s_instance = this;
        if (Environment.GetEnvironmentVariable("BLUERANDOM_DCOMP_FRW_DEMO") != "1")
        {
            IpcBroker.Subscribe(IpcModule.Frw, OnIpcMessageReceived);
        }
    }

    /// <summary>在当前 STA 线程启动窗口与消息循环（阻塞）。</summary>
    public void Run()
    {
        try
        {
            s_wndProc = WndProcImpl;
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                hInstance = GetModuleHandleW(null),
                hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),   // 关键：显式箭头光标
                hbrBackground = IntPtr.Zero,
                lpszClassName = "BlueRandomDCompResult",
            };
            RegisterClassExW(ref wc);

            s_hwnd = CreateWindowExW(
                WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP,
                "BlueRandomDCompResult", "Blue Random Result",
                WS_POPUP,
                (1920 - 900) / 2, (1080 - 620) / 2, 900, 620,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

            if (s_hwnd == IntPtr.Zero)
            {
                AppLogger.Error("DCompFRW", "CreateWindowEx 失败");
                return;
            }

            InitGraphics();

            if (Environment.GetEnvironmentVariable("BLUERANDOM_DCOMP_FRW_DEMO") == "1")
            {
                var demo = Enumerable.Range(1, 10).Select(i => new CalledStudent
                {
                    Name = "测试学生" + i,
                    LetterColor = i % 3 == 0 ? "gold" : (i % 3 == 1 ? "blue" : "rainbow"),
                }).ToList();
                DisplayResults(demo, new PickResultDialogConfig());
            }

            timeBeginPeriod(1);
            while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            timeEndPeriod(1);

            Cleanup();
        }
        catch (Exception ex)
        {
            AppLogger.Error("DCompFRW", "结果浮窗线程异常", ex);
        }
    }

    private void InitGraphics()
    {
        var levels = new[]
        {
            D3DFeatureLevel.Level_11_1,
            D3DFeatureLevel.Level_11_0,
            D3DFeatureLevel.Level_10_1,
            D3DFeatureLevel.Level_10_0,
        };

        try
        {
            _d3dDevice = D3D11.D3D11CreateDevice(D3DDriverType.Hardware, DeviceCreationFlags.BgraSupport, levels);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("DCompFRW", $"D3D11 硬件设备创建失败，回退 WARP: {ex.Message}");
        }
        _d3dDevice ??= D3D11.D3D11CreateDevice(D3DDriverType.Warp, DeviceCreationFlags.BgraSupport, levels);

        using var dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
        _d2dDevice = D2D1.D2D1CreateDevice(dxgiDevice, null);
        _resDc = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);

        _dcomp = DComp.DCompositionCreateDevice2<IDCompositionDevice>(_d2dDevice);
        _desktop = _dcomp.QueryInterface<IDCompositionDesktopDevice>();
        _desktop.CreateTargetForHwnd(s_hwnd, true, out _target);
        _rootVisual = _dcomp.CreateVisual();
        _rootVisual3 = _rootVisual.QueryInterface<IDCompositionVisual3>();
        _target.SetRoot(_rootVisual);
        _dcomp.Commit();
    }

    private void Cleanup()
    {
        StopTickTimer();
        UninstallMouseHook();
        try { StopBgmImmediate(); } catch { }

        ReleaseLayers();
        ReleaseSurface();

        _rootVisual3?.Dispose();
        _rootVisual?.Dispose();
        _target?.Dispose();
        _desktop?.Dispose();
        _dcomp?.Dispose();
        _resDc?.Dispose();
        _d2dDevice?.Dispose();
        _d3dDevice?.Dispose();
    }

    // ========================================================================
    //  5. IPC
    // ========================================================================

    private void OnIpcMessageReceived(IpcMessage msg)
    {
        _uiQueue.Enqueue(() => HandleIpc(msg));
        var hwnd = s_hwnd;
        if (hwnd != IntPtr.Zero) PostMessageW(hwnd, WM_APP_EXEC, IntPtr.Zero, IntPtr.Zero);
    }

    private void HandleIpc(IpcMessage msg)
    {
        switch (msg.Command)
        {
            case "ShowWindow":
                if (msg.Payload is FrwShowPayload showPayload)
                {
                    DisplayResults(showPayload.CalledStudents, ConfigCache.GetFRWProps());
                }
                break;

            case "Destroy":
                PostQuitMessage(0);
                break;
        }
    }

    // ========================================================================
    //  6. 展示
    // ========================================================================

    private void DisplayResults(List<CalledStudent> students, PickResultDialogConfig props)
    {
        _students = students ?? new List<CalledStudent>();
        _props = props ?? new PickResultDialogConfig();
        _phase = Phase.Opening;
        _canClose = false;
        _phaseStartMs = 0;

        uint dpi = GetDpiForWindow(s_hwnd);
        _dpiScale = dpi == 0 ? 1.0 : dpi / 96.0;

        BuildLayout();

        // 关键：先彻底释放上一轮资源，保证不残留旧结果
        ReleaseLayers();
        ReleaseSurface();

        BuildLayers();
        EnsureSurface();

        SetWindowPos(s_hwnd, new IntPtr(-1),
            (int)Math.Round(_winX * _dpiScale), (int)Math.Round(_winY * _dpiScale),
            (int)Math.Round(_winW * _dpiScale), (int)Math.Round(_winH * _dpiScale),
            0x0040 /* SWP_SHOWWINDOW */);
        ShowWindow(s_hwnd, SW_SHOW);
        SetForegroundWindow(s_hwnd);
        SetFocus(s_hwnd);

        _clock.Restart();
        StartTickTimer();
        RenderFrame();

        InstallMouseHook();     // 就绪后全屏点击关闭（低级钩子全局检测）
        ProbeBusyCursor();      // 窗口直接出现在静止光标下时纠正继承的忙光标

        PlaySfx(_props);
        PlayBgm(_props);
    }

    /// <summary>
    /// 启动专职钩子线程并安装 WH_MOUSE_LL 低级鼠标钩子：就绪后任意屏幕位置点击均关闭。
    /// 关键：LL 钩子回调依赖安装线程及时泵消息——若装在渲染线程，60fps 渲染会让
    /// 系统级鼠标输入排队等待（表现为忙光标），回调超时还会被系统跳过（全屏点击失效）。
    /// 专职线程只泵消息，回调仅做标志检查 + PostMessage，微秒级返回。
    /// </summary>
    private void InstallMouseHook()
    {
        if (_hookThread != null) return;
        _hookReady = false;
        var readyEvent = new ManualResetEventSlim(false);
        _hookThread = new Thread(() =>
        {
            _hookThreadId = GetCurrentThreadId();
            _mouseHookProc = MouseHookCallback;
            _mouseHook = SetWindowsHookExW(WH_MOUSE_LL, _mouseHookProc, GetModuleHandleW(null), 0);
            if (_mouseHook == IntPtr.Zero)
            {
                AppLogger.Warn("DCompFRW", $"低级鼠标钩子安装失败: {Marshal.GetLastWin32Error()}");
                readyEvent.Set();
                return;
            }
            readyEvent.Set();
            // 专职消息泵：线程存活期间钩子回调始终及时
            while (GetMessageW(out _, IntPtr.Zero, 0, 0)) { }
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        })
        {
            Name = "FRW_MouseHook",
            IsBackground = true,
        };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
        readyEvent.Wait(1000);
    }

    private void UninstallMouseHook()
    {
        _hookReady = false;
        if (_hookThread != null)
        {
            // 向钩子线程自身投递 WM_QUIT 结束消息泵，泵退出后线程自行 Unhook
            var t = _hookThread;
            _hookThread = null;
            if (t != null && t.IsAlive && _hookThreadId != 0)
            {
                PostThreadMessageW(_hookThreadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
            }
            _hookThreadId = 0;
        }
        _mouseHookProc = null;
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _hookReady)
        {
            int msg = wParam.ToInt32();
            bool isDown = msg is WM_LBUTTONDOWN_LL or WM_RBUTTONDOWN_LL or WM_MBUTTONDOWN_LL or WM_XBUTTONDOWN_LL;
            if (isDown)
            {
                // 就绪：吞掉点击（不透传下层），投递到渲染线程关闭浮窗
                PostMessageW(s_hwnd, WM_LBUTTONDOWN, IntPtr.Zero, IntPtr.Zero);
                return new IntPtr(1);
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 统一刷新光标状态。SetCursor 只接受真实 HCURSOR：
    /// 直接把 IDC_ARROW / IDC_HAND 资源 ID 传给 SetCursor 会返回 NULL 并静默失败，
    /// 导致窗口继承的 IDC_WAIT（系统启动等待光标）始终无法被纠正。
    /// </summary>
    private static void RefreshCursor()
    {
        var cursor = (s_instance != null && s_instance.IsReadyToClose()) ? HandCursor : ArrowCursor;
        if (cursor != IntPtr.Zero) SetCursor(cursor);
    }

    /// <summary>
    /// 忙光标探测：窗口可能直接出现在静止的光标下方，没有新的鼠标移动事件。
    /// 主动取当前光标位置，若在本窗口客户区内则立即按状态设置光标。
    /// </summary>
    private void ProbeBusyCursor()
    {
        try
        {
            if (GetCursorPos(out POINT pt) && WindowFromPoint(pt) == s_hwnd)
            {
                RefreshCursor();
            }
        }
        catch { }
    }

    private void StartTickTimer()
    {
        // SetTimer 在该窗口/线程组合下不产生周期 WM_TIMER，改用线程池定时器投递。
        // _tickPending 合并：若上一条 WM_TIMER 尚未被处理则跳过投递，
        // 防止渲染耗时 > 16ms 时消息队列积压、鼠标消息被饿死（表现为光标一直忙）。
        StopTickTimer();
        Volatile.Write(ref _tickPending, 0);
        _tickTimer = new System.Threading.Timer(
            _ =>
            {
                var h = s_hwnd;
                if (h != IntPtr.Zero && Interlocked.CompareExchange(ref _tickPending, 1, 0) == 0)
                {
                    try { PostMessageW(h, WM_TIMER, (IntPtr)TIMER_ANIM, IntPtr.Zero); } catch { }
                }
            },
            null, 0, 16);
    }

    private void StopTickTimer()
    {
        _tickTimer?.Dispose();
        _tickTimer = null;
    }

    // ========================================================================
    //  7. 布局
    // ========================================================================

    private void BuildLayout()
    {
        int count = _students.Count;
        int rows = count > 5 ? 2 : 1;
        int cols = Math.Clamp(count, 1, 5);

        // 面板本体不再包含提示条区域（HINT_BAND 已移出）
        _panelW = Math.Max(420, cols * CARD_W + Math.Max(cols - 1, 0) * COL_GAP + PANEL_BAND_X);
        _panelH = rows * CARD_H + Math.Max(rows - 1, 0) * ROW_GAP + PANEL_BAND_Y;

        // 内容 = 面板 + 间距 + 提示条（提示条在面板外正下方）
        double contentW = _panelW;
        double contentH = _panelH + HINT_GAP + HINT_H;

        var workArea = SystemParameters.WorkArea;
        _winW = Math.Min(contentW + MARGIN_X * 2, workArea.Width * 0.95);
        _winH = Math.Min(contentH + MARGIN_Y * 2, workArea.Height * 0.95);
        _winX = workArea.Left + (workArea.Width - _winW) / 2;
        _winY = workArea.Top + (workArea.Height - _winH) / 2;

        double contentTop = (_winH - contentH) / 2;
        _panelLeft = (_winW - _panelW) / 2;
        _panelTop = contentTop;

        _hintW = Math.Clamp(_panelW * 0.60, 220, _panelW - PANEL_BAND_X);
        _hintX = _panelLeft + (_panelW - _hintW) / 2;
        _hintY = _panelTop + _panelH + HINT_GAP;
    }

    // ========================================================================
    //  8. 图层构建
    // ========================================================================

    private void BuildLayers()
    {
        int count = _students.Count;
        int cols = Math.Clamp(count, 1, 5);

        // 1. 面板
        _panelLayer = CreateLayer(RenderPanelBitmap(), _panelW, _panelH);

        // 2. 信封 + 姓名卡
        double rowSpanW = cols * CARD_W + Math.Max(cols - 1, 0) * COL_GAP;
        double startX = _panelLeft + (_panelW - rowSpanW) / 2.0;
        double startY = _panelTop + 4 + 24;

        for (int i = 0; i < count; i++)
        {
            var student = _students[i];
            int row = i / 5;
            int col = i % 5;
            double cx = startX + CARD_W / 2.0 + col * (CARD_W + COL_GAP);
            double cy = startY + CARD_H / 2.0 + row * (CARD_H + ROW_GAP);

            var envSource = ResourceCache.GetEnvelopeImage(student.LetterColor) as BitmapSource;
            if (envSource == null) continue;

            // 原始素材 + 运行时渲染阴影：画布 = 原始尺寸 + 2×PAD（与旧阴影图同尺寸，布局不变）
            var envBitmap = RenderEnvelopeBitmap(envSource);
            double envDipW = envSource.PixelWidth + ENV_SHADOW_PAD * 2;
            double envDipH = envSource.PixelHeight + ENV_SHADOW_PAD * 2;
            var cardLayer = CreateLayer(envBitmap, envDipW, envDipH);
            _cardLayers.Add(cardLayer);

            var nameBitmap = RenderNameBitmap(student.Name);
            var nameLayer = CreateLayer(nameBitmap, 156 + NAME_PAD * 2, 94 + NAME_PAD * 2);
            _nameLayers.Add(nameLayer);

            _cardCenters.Add((cx, cy));
        }

        // 4. 固定关闭提示：始终显示同一条提示，不再展示忙碌状态
        _hintLayer = CreateLayer(RenderHintBitmap("点击任意处关闭", _hintW), _hintW, HINT_H);

        ApplyInitialState();
    }

    /// <summary>把所有图层置于动画起始状态。</summary>
    private void ApplyInitialState()
    {
        if (_panelLayer != null)
        {
            _panelLayer.Transform = Matrix3x2.CreateTranslation((float)_panelLeft, (float)_panelTop);
            _panelLayer.Opacity = 0f;
        }
        if (_hintLayer != null)
        {
            _hintLayer.Transform = Matrix3x2.CreateTranslation((float)_hintX, (float)_hintY);
            _hintLayer.Opacity = 0f;
        }
        for (int i = 0; i < _cardLayers.Count; i++)
        {
            var (cx, cy) = _cardCenters[i];
            SetCardTransform(_cardLayers[i], cx, cy, 2.5, -24);
            _cardLayers[i].Opacity = 0f;
        }
        for (int i = 0; i < _nameLayers.Count; i++)
        {
            var (cx, cy) = _cardCenters[i];
            SetNameTransform(_nameLayers[i], cx, cy, 0.96, 12);
            _nameLayers[i].Opacity = 0f;
        }
    }

    // ========================================================================
    //  9. 逐帧动画
    // ========================================================================

    private void OnTick()
    {
        if (_phase == Phase.Idle) return;

        double t = _clock.Elapsed.TotalMilliseconds;

        if (_phase == Phase.Opening)
        {
            UpdateOpening(t);
        }
        else if (_phase == Phase.Closing)
        {
            UpdateClosing(t - _phaseStartMs);
        }

        UpdateBgm();
        RenderFrame();
        RefreshCursor();    // 每帧自愈：窗口出现时继承的忙光标不依赖下一次鼠标移动

        // 就绪且 BGM 淡入完成后停止计时（省电）
        if (_phase == Phase.Ready && !_bgmFadingIn && !_bgmFadingOut)
        {
            _tickTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        }
    }

    private void UpdateOpening(double t)
    {
        // 面板：0.7s ease-out 缩放 0.85→1，上移 16→0，0.4s 淡入
        double pp = EaseOutCubic(Clamp01(t / PANEL_MS));
        double ps = 0.85 + 0.15 * pp;
        double pdy = 16 * (1 - pp);
        double pop = Clamp01(t / 400);

        if (_panelLayer != null)
        {
            double cy = _panelTop + _panelH / 2;
            _panelLayer.Transform =
                Matrix3x2.CreateTranslation((float)(-_panelW / 2), (float)(-_panelH / 2 + pdy)) *
                Matrix3x2.CreateScale((float)ps) *
                Matrix3x2.CreateTranslation((float)(_panelLeft + _panelW / 2), (float)cy);
            _panelLayer.Opacity = (float)pop;
        }

        // 信封：0.6s ease-out，交错 120ms
        int totalDelay = Math.Max(_students.Count - 1, 0) * (int)CARD_STAGGER_MS + (int)CARD_MS;
        for (int i = 0; i < _cardLayers.Count; i++)
        {
            double delay = i * CARD_STAGGER_MS;
            double cp = EaseOutCubic(Clamp01((t - delay) / CARD_MS));
            var (cx, cy) = _cardCenters[i];
            SetCardTransform(_cardLayers[i], cx, cy, 2.5 - 1.5 * cp, -24 * (1 - cp));
            _cardLayers[i].Opacity = (float)Clamp01((t - delay) / 400);
        }

        // 姓名卡：信封全部就位后逐张弹出
        for (int i = 0; i < _nameLayers.Count; i++)
        {
            double delay = totalDelay + 100 + i * CARD_STAGGER_MS;
            double np = EaseOutCubic(Clamp01((t - delay) / NAME_MS));
            var (cx, cy) = _cardCenters[i];
            SetNameTransform(_nameLayers[i], cx, cy, 0.96 + 0.04 * np, 12 * (1 - np));
            _nameLayers[i].Opacity = (float)Clamp01((t - delay) / 260);
        }

        // 最后一张姓名卡完成时刻（胶囊切换与就绪判定共用）
        int namesEnd = totalDelay + 100 + Math.Max(0, _nameLayers.Count - 1) * (int)CARD_STAGGER_MS + (int)NAME_MS;

        // 固定提示条：动画开始后短暂淡入，文案始终为关闭提示
        float hintOp = (float)Clamp01((t - 200) / 300);
        if (_hintLayer != null) _hintLayer.Opacity = hintOp;

        // 进入就绪（最后一张姓名卡完成后再缓冲 200ms）
        if (!_canClose && t >= namesEnd + 200)
        {
            _canClose = true;
            _phase = Phase.Ready;
            _hookReady = true;      // 低级钩子开始吞点击（全屏任意位置关闭）

            if (Environment.GetEnvironmentVariable("BLUERANDOM_DCOMP_DUMP") == "1")
            {
                DumpFrame(Path.Combine(Path.GetTempPath(), "br_frw_dump.png"));
            }
        }
    }

    private void UpdateClosing(double elapsed)
    {
        double p = Clamp01(elapsed / CLOSE_MS);
        float op = (float)(1.0 - p);

        if (_rootVisual != null && _rootVisual3 != null)
        {
            double s = 1.0 - 0.1 * p;
            double dy = 8 * p;
            _rootVisual.SetTransform(
                Matrix3x2.CreateTranslation((float)(-_winW / 2), (float)(-_winH / 2)) *
                Matrix3x2.CreateScale((float)s) *
                Matrix3x2.CreateTranslation((float)(_winW / 2), (float)(_winH / 2 + dy)));
            _rootVisual3.SetOpacity(op);
        }

        if (elapsed >= CLOSE_MS) FinishClose();
    }

    private static double Clamp01(double v) => Math.Clamp(v, 0.0, 1.0);
    private static double EaseOutCubic(double p) => 1 - Math.Pow(1 - p, 3);

    private static void SetCardTransform(Layer layer, double cx, double cy, double animScale, double dy)
    {
        double w = layer.Width;
        double h = layer.Height;
        double scale = CARD_BASE_SCALE * animScale;
        float rad = (float)(15.0 * Math.PI / 180.0);
        layer.Transform =
            Matrix3x2.CreateTranslation((float)(-w / 2), (float)(-h / 2 + dy)) *
            Matrix3x2.CreateScale((float)scale) *
            Matrix3x2.CreateRotation(rad) *
            Matrix3x2.CreateTranslation((float)cx, (float)cy);
    }

    private static void SetNameTransform(Layer layer, double cx, double cy, double scale, double dy)
    {
        double w = layer.Width;
        double h = layer.Height;
        float rad = (float)(15.0 * Math.PI / 180.0);
        layer.Transform =
            Matrix3x2.CreateTranslation((float)(-w / 2), (float)(-h / 2 + dy)) *
            Matrix3x2.CreateScale((float)scale) *
            Matrix3x2.CreateRotation(rad) *
            Matrix3x2.CreateTranslation((float)cx, (float)cy);
    }

    // ========================================================================
    //  10. 渲染
    // ========================================================================

    /// <summary>创建整窗 surface 与其唯一 Visual（尺寸 = 窗口物理像素）。</summary>
    private void EnsureSurface()
    {
        int w = Math.Max(1, (int)Math.Round(_winW * _dpiScale));
        int h = Math.Max(1, (int)Math.Round(_winH * _dpiScale));

        if (_surface != null && _surfaceW == w && _surfaceH == h) return;

        ReleaseSurface();

        _dcomp!.CreateSurface((uint)w, (uint)h, Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied, out var surface);
        _surface = surface;
        _surfaceW = w;
        _surfaceH = h;

        var visual = _dcomp.CreateVisual();
        visual.SetContent(surface);
        // 实测该 PerMonitorV2 窗口下 DComp 视觉坐标与 surface 像素为 1:1，保持 Identity
        visual.SetTransform(Matrix3x2.Identity);
        _surfaceVisual = visual;
        _rootVisual!.AddVisual(visual, true, null);
    }

    private void ReleaseSurface()
    {
        if (_surfaceVisual != null)
        {
            try { _rootVisual?.RemoveVisual(_surfaceVisual); } catch { }
            try { _surfaceVisual.Dispose(); } catch { }
            _surfaceVisual = null;
        }
        try { _surface?.Dispose(); } catch { }
        _surface = null;
        _surfaceW = _surfaceH = 0;
    }

    private void ReleaseLayers()
    {
        foreach (var layer in _layers)
        {
            try { layer.Bitmap.Dispose(); } catch { }
        }
        _layers.Clear();
        _cardLayers.Clear();
        _nameLayers.Clear();
        _cardCenters.Clear();
        _panelLayer = _hintLayer = null;
    }

    /// <summary>即时模式渲染一帧：清屏 → 按图层顺序（底→顶）绘制 → EndDraw → Commit。</summary>
    private void RenderFrame()
    {
        if (_surface == null || _dcomp == null) return;

        try
        {
            _surface.BeginDraw<ID2D1DeviceContext>(null, out var dc, out _);
            using (dc)
            {
                dc.Clear(new D2DColor4(0f, 0f, 0f, 0f));

                var dpi = Matrix3x2.CreateScale((float)_dpiScale);
                foreach (var layer in _layers)
                {
                    if (layer.Opacity <= 0.002f) continue;
                    // 行向量顺序：先 DIP 图层变换，再整体换算物理像素
                    dc.Transform = layer.Transform * dpi;
                    var dest = new RawRectF(0f, 0f, (float)layer.Width, (float)layer.Height);
                    var identity = Matrix4x4.Identity;
                    dc.DrawBitmap(layer.Bitmap, layer.Opacity, InterpolationMode.Linear, dest, ref identity);
                }
                dc.Transform = Matrix3x2.Identity;
            }
            _surface.EndDraw();
            _dcomp.Commit();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("DCompFRW", $"渲染帧失败: {ex.Message}");
        }
    }

    // ========================================================================
    //  11. 位图资源
    // ========================================================================

    private Layer CreateLayer(BitmapSource bitmap, double width, double height)
    {
        var layer = new Layer
        {
            Bitmap = CreateD2DBitmap(_resDc!, bitmap),
            Width = Math.Max(1, width),
            Height = Math.Max(1, height),
            Source = bitmap,
            Opacity = 0f,
        };
        _layers.Add(layer);
        return layer;
    }

    private static ID2D1Bitmap1 CreateD2DBitmap(ID2D1DeviceContext dc, BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        int w = converted.PixelWidth;
        int h = converted.PixelHeight;
        int stride = w * 4;
        byte[] pixels = new byte[stride * h];
        converted.CopyPixels(pixels, stride, 0);

        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var props = new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied));
            return dc.CreateBitmap(new SizeI(w, h), handle.AddrOfPinnedObject(), (uint)stride, props);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>DIP → 位图像素（当前实现按 1 DIP = 1 px，D2D 侧用变换做 DPI 缩放）。</summary>
    private int Px(double dip) => Math.Max(1, (int)Math.Round(dip));

    /// <summary>
    /// 预渲染位图：像素尺寸等于 DIP 尺寸。
    /// （若改为按物理像素创建，D2D 会按位图自带 DPI 再换算一次，导致各图层尺寸比例错乱）
    /// </summary>
    private BitmapSource RenderBitmap(double widthDip, double heightDip, Action<DrawingContext> draw, bool highQuality = false)
    {
        var visual = new DrawingVisual();
        if (highQuality) RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            draw(dc);
        }
        var rtb = new RenderTargetBitmap(Px(widthDip), Px(heightDip), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>信封阴影内边距（DIP）：预留高斯模糊与投影溢出区。</summary>
    private const int ENV_SHADOW_PAD = 24;

    /// <summary>
    /// 预渲染信封：原始素材（216×256）+ 运行时 DropShadowEffect 烘烙成位图。
    /// 输出画布 = 原始尺寸 + 2×PAD，信封居中，显示时按 CARD_BASE_SCALE 缩放到 124×147 本体。
    /// </summary>
    private BitmapSource RenderEnvelopeBitmap(BitmapSource source)
    {
        double wDip = source.PixelWidth + ENV_SHADOW_PAD * 2;
        double hDip = source.PixelHeight + ENV_SHADOW_PAD * 2;

        var visual = new DrawingVisual
        {
            // 阴影仅在此处一次性栅格化，结果烘焙进位图，动画期间零重算
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(0x06, 0x16, 0x30),
                BlurRadius = 16,
                ShadowDepth = 7,
                Direction = 270,
                Opacity = 0.30,
                RenderingBias = RenderingBias.Performance,
            },
        };
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);

        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(source, new Rect(ENV_SHADOW_PAD, ENV_SHADOW_PAD, source.PixelWidth, source.PixelHeight));
        }

        var rtb = new RenderTargetBitmap(Px(wDip), Px(hDip), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>
    /// 预渲染面板位图：半透明底 + 4px 蔚蓝边框 + 24px 圆角。
    /// </summary>
    private BitmapSource RenderPanelBitmap()
    {
        int w = (int)Math.Round(_panelW);
        int panelH = (int)Math.Round(_panelH);

        return RenderBitmap(w, panelH, dc =>
        {
            // 面板：半透明底 + 4px 蔚蓝边框 + 24px 圆角
            double opacity = Math.Clamp(_props.PanelOpacity, 0.2, 1.0);
            Color bg = Colors.White;
            if (!string.IsNullOrEmpty(_props.PanelBgColor))
            {
                try { bg = (Color)ColorConverter.ConvertFromString(_props.PanelBgColor); } catch { }
            }
            Color border = Color.FromRgb(0x66, 0xCC, 0xFF);
            if (!string.IsNullOrEmpty(_props.PanelBorderColor))
            {
                try { border = (Color)ColorConverter.ConvertFromString(_props.PanelBorderColor); } catch { }
            }
            dc.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), bg.R, bg.G, bg.B)),
                new Pen(new SolidColorBrush(border), 4),
                new Rect(2, 2, w - 4, panelH - 4), 24, 24);
        }, highQuality: true);
    }

    private BitmapSource RenderNameBitmap(string name)
    {
        int w = 156;
        int h = 94;
        int tw = w + NAME_PAD * 2;
        int th = h + NAME_PAD * 2;
        return RenderBitmap(tw, th, dc =>
        {
            // 多层圆角矩形逼近高斯阴影（一次性烘焙，动画期间零重算）
            for (int i = 7; i >= 1; i--)
            {
                double spread = i * 1.7;
                byte alpha = (byte)(255 * Math.Max(0.004, 0.055 - i * 0.005));
                dc.DrawRoundedRectangle(
                    new SolidColorBrush(Color.FromArgb(alpha, 0x06, 0x16, 0x30)), null,
                    new Rect(NAME_PAD - spread, NAME_PAD + 4 - spread / 2, w + spread * 2, h + spread),
                    12 + spread / 2, 12 + spread / 2);
            }
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(NAME_PAD, NAME_PAD, w, h), 12, 12);

            double size = name.Length > 8 ? 18.0 : (name.Length > 5 ? 21.0 : 24.0);
            var face = new Typeface(ResourceCache.CustomFont, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var ft = new FormattedText(
                name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                face, size, new SolidColorBrush(Color.FromRgb(0x1C, 0x27, 0x41)), 1.0)
            {
                MaxTextWidth = 140,
                TextAlignment = TextAlignment.Center,
                MaxLineCount = 2,
            };
            dc.DrawText(ft, new Point(NAME_PAD + 8, NAME_PAD + (h - ft.Height) / 2));
        });
    }

    private BitmapSource RenderHintBitmap(string text, double width)
    {
        int w = (int)Math.Round(width);
        int h = (int)HINT_H;
        return RenderBitmap(w, h, dc =>
        {
            dc.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromRgb(0x66, 0xCC, 0xFF)), null,
                new Rect(0, 0, w, h), 16, 16);

            var face = new Typeface(ResourceCache.CustomFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var ft = new FormattedText(
                text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                face, 14, Brushes.White, 1.0)
            {
                TextAlignment = TextAlignment.Center,
                MaxTextWidth = w,
            };
            dc.DrawText(ft, new Point(0, (h - ft.Height) / 2));
        });
    }

    /// <summary>离线导出当前帧（诊断用，环境变量 BLUERANDOM_DCOMP_DUMP=1 开启）。</summary>
    private void DumpFrame(string path)
    {
        try
        {
            int w = Math.Max(1, (int)Math.Round(_winW));
            int h = Math.Max(1, (int)Math.Round(_winH));
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(40, 44, 52)), null, new Rect(0, 0, w, h));
                foreach (var layer in _layers)
                {
                    if (layer.Source == null || layer.Opacity <= 0.001f) continue;
                    dc.PushOpacity(layer.Opacity);
                    var m = layer.Transform;
                    dc.PushTransform(new MatrixTransform(new System.Windows.Media.Matrix(m.M11, m.M12, m.M21, m.M22, m.M31, m.M32)));
                    dc.DrawImage(layer.Source, new Rect(0, 0, layer.Width, layer.Height));
                    dc.Pop();
                    dc.Pop();
                }
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            enc.Save(fs);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("DCompFRW", $"导出帧失败: {ex.Message}");
        }
    }

    // ========================================================================
    //  12. 关闭
    // ========================================================================

    /// <summary>开始关闭动画（仅底部胶囊或 Esc 触发）。</summary>
    public void Dismiss()
    {
        if (_phase == Phase.Closing || _phase == Phase.Idle) return;
        _phase = Phase.Closing;
        _canClose = false;
        _hookReady = false;     // 关闭动画期间停止吞点击
        _phaseStartMs = _clock.Elapsed.TotalMilliseconds;
        _bgmFadingOut = true;
        _tickTimer?.Change(0, 16);   // Ready 后计时器已暂停，这里恢复
    }

    private void FinishClose()
    {
        _phase = Phase.Idle;
        _tickTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        UninstallMouseHook();   // 关闭后立即停止全屏点击检测（结束钩子线程）

        if (_rootVisual != null && _rootVisual3 != null)
        {
            _rootVisual.SetTransform(Matrix3x2.Identity);
            _rootVisual3.SetOpacity(1f);
        }

        if (Environment.GetEnvironmentVariable("BLUERANDOM_DCOMP_DUMP") == "1")
        {
            DumpFrame(Path.Combine(Path.GetTempPath(), "br_frw_dump.png"));
        }

        SetCursor(ArrowCursor);
        ShowWindow(s_hwnd, SW_HIDE);
        StopBgmImmediate();
        ReleaseLayers();
        // 随关闭事件上报本轮名单，供"结果暂留浮窗"复用
        IpcBroker.Send(IpcMessage.Create(IpcAction.Post, IpcModule.Frw, "WindowClosed", _students));
    }

    // ========================================================================
    //  13. 窗口消息
    // ========================================================================

    private static IntPtr WndProcImpl(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_APP_EXEC:
                if (s_instance != null)
                {
                    while (s_instance._uiQueue.TryDequeue(out var action)) action();
                }
                return IntPtr.Zero;

            case WM_TIMER:
                if (s_instance != null) Volatile.Write(ref s_instance._tickPending, 0);
                try { s_instance?.OnTick(); }
                catch (Exception ex) { AppLogger.Warn("DCompFRW", $"动画帧异常: {ex.Message}"); }
                return IntPtr.Zero;

            case WM_SETCURSOR:
                // 不再交给 DefWindowProcW：它可能恢复线程之前继承的忙光标。
                // 无论命中测试结果如何，都由结果窗明确接管光标。
                RefreshCursor();
                return new IntPtr(1);

            case WM_NCHITTEST:
                // DComp 内容不参与传统窗口命中测试，显式声明整窗为客户区，
                // 保证 WM_SETCURSOR / WM_MOUSEMOVE 始终经过本窗口。
                return new IntPtr(HTCLIENT);

            case WM_MOUSEMOVE:
                // 光标自愈：若仍处于继承的忙态（App Starting 光标），强制纠正为箭头/手型
                RefreshCursor();
                return IntPtr.Zero;

            case WM_LBUTTONDOWN:
                // 窗口内点击（含低级钩子投递的全屏点击）→ 就绪后关闭
                s_instance?.OnMouseDown(GetX(lParam), GetY(lParam));
                return IntPtr.Zero;

            case WM_KEYDOWN:
                if (wParam.ToInt32() == VK_ESCAPE) s_instance?.Dismiss();
                return IntPtr.Zero;

            case WM_ERASEBKGND:
                return new IntPtr(1);

            case WM_PAINT:
                return IntPtr.Zero;

            case WM_CLOSE:
                s_instance?.Dismiss();
                return IntPtr.Zero;

            case WM_DESTROY:
                s_instance?.StopTickTimer();
                PostQuitMessage(0);
                return IntPtr.Zero;

            default:
                return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    private static int GetX(IntPtr lParam) => (short)(lParam.ToInt32() & 0xFFFF);
    private static int GetY(IntPtr lParam) => (short)((lParam.ToInt32() >> 16) & 0xFFFF);

    /// <summary>是否已就绪（就绪后窗口任意区域点击均可关闭）。</summary>
    private bool IsReadyToClose() => _canClose && _phase == Phase.Ready;

    /// <summary>就绪后点击任意位置关闭（含低级钩子投递的全屏点击；盖示动画未完成时无效）。</summary>
    private void OnMouseDown(int x, int y)
    {
        if (!IsReadyToClose()) return;
        Dismiss();
    }

    // ========================================================================
    //  14. 音频
    // ========================================================================

    private void PlaySfx(PickResultDialogConfig props)
    {
        if (!props.DefaultPlayGachaSound) return;
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "BlueRandom_Audio");
            Directory.CreateDirectory(dir);
            string? path = null;

            if (!string.IsNullOrEmpty(props.GachaSoundCustomId))
            {
                var custom = CustomsStore.LoadCustom(props.GachaSoundCustomId);
                if (custom != null && !string.IsNullOrEmpty(custom.Base64))
                {
                    byte[] bytes = Convert.FromBase64String(custom.Base64);
                    string ext = Path.GetExtension(custom.FileName);
                    if (string.IsNullOrEmpty(ext)) ext = ".wav";
                    path = Path.Combine(dir, $"{custom.Id}{ext}");
                    if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
                    {
                        File.WriteAllBytes(path, bytes);
                    }
                }
            }

            if (string.IsNullOrEmpty(path))
            {
                byte[]? bytes = ResourceCache.GetAudioBytes("button_click.wav");
                if (bytes != null)
                {
                    path = Path.Combine(dir, "button_click.wav");
                    if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
                    {
                        File.WriteAllBytes(path, bytes);
                    }
                }
            }

            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            _sfxPlayer ??= new MediaPlayer();
            _sfxPlayer.Stop();
            _sfxPlayer.Close();
            _sfxPlayer.Volume = Math.Clamp(props.SoundVolume / 100.0, 0.0, 1.0);
            _sfxPlayer.Open(new Uri(path));
            _sfxPlayer.Play();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("DCompFRW", $"播放抽卡音效失败: {ex.Message}");
        }
    }

    private void PlayBgm(PickResultDialogConfig props)
    {
        StopBgmImmediate();
        if (!props.PlayMusic || string.IsNullOrEmpty(props.BgmCustomId)) return;

        try
        {
            var custom = CustomsStore.LoadCustom(props.BgmCustomId);
            if (custom == null || string.IsNullOrEmpty(custom.Base64)) return;

            string dir = Path.Combine(Path.GetTempPath(), "BlueRandom_Audio");
            Directory.CreateDirectory(dir);
            byte[] bytes = Convert.FromBase64String(custom.Base64);
            string ext = Path.GetExtension(custom.FileName);
            if (string.IsNullOrEmpty(ext)) ext = ".mp3";
            string path = Path.Combine(dir, $"{custom.Id}{ext}");
            if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
            {
                File.WriteAllBytes(path, bytes);
            }

            _bgmPlayer = new MediaPlayer();
            _bgmTargetVolume = Math.Clamp(props.MusicVolume / 100.0, 0.0, 1.0);
            _bgmFadeSec = Math.Max(0.1, props.BgmFadeDuration);
            _bgmCurrentVolume = 0.0;
            _bgmFadingIn = true;
            _bgmFadingOut = false;

            _bgmPlayer.MediaEnded -= OnBgmEnded;
            _bgmPlayer.MediaEnded += OnBgmEnded;
            _bgmPlayer.Volume = 0.0;
            _bgmPlayer.Open(new Uri(path));
            _bgmPlayer.Position = TimeSpan.FromSeconds(Math.Max(0.0, props.BgmStartTime));
            _bgmPlayer.Play();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("DCompFRW", $"播放 BGM 失败: {ex.Message}");
        }
    }

    private void OnBgmEnded(object? sender, EventArgs e)
    {
        try
        {
            if (_bgmPlayer != null && !_bgmFadingOut)
            {
                var props = ConfigCache.GetFRWProps();
                _bgmPlayer.Position = TimeSpan.FromSeconds(Math.Max(0.0, props.BgmStartTime));
                _bgmPlayer.Play();
            }
        }
        catch { }
    }

    private void UpdateBgm()
    {
        if (_bgmPlayer == null) return;

        double step = 16.0 / Math.Max(1.0, _bgmFadeSec * 1000.0);
        if (_bgmFadingIn && !_bgmFadingOut)
        {
            _bgmCurrentVolume = Math.Min(_bgmTargetVolume, _bgmCurrentVolume + _bgmTargetVolume * step);
            _bgmPlayer.Volume = _bgmCurrentVolume;
            if (_bgmCurrentVolume >= _bgmTargetVolume - 0.001) _bgmFadingIn = false;
        }
        else if (_bgmFadingOut)
        {
            _bgmCurrentVolume = Math.Max(0.0, _bgmCurrentVolume - Math.Max(0.0001, _bgmCurrentVolume * step));
            _bgmPlayer.Volume = _bgmCurrentVolume;
            if (_bgmCurrentVolume <= 0.001) StopBgmImmediate();
        }
    }

    private void StopBgmImmediate()
    {
        if (_bgmPlayer == null) return;
        try
        {
            _bgmPlayer.Volume = 0.0;
            _bgmPlayer.Stop();
            _bgmPlayer.Close();
        }
        catch { }
        _bgmPlayer = null;
        _bgmCurrentVolume = 0.0;
        _bgmFadingIn = false;
        _bgmFadingOut = false;
    }
}
