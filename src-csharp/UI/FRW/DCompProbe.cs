// ==============================================================================
// 文件: DCompProbe.cs
// 命名空间: BlueRandom.UI.FRW
// 作用: DirectComposition + D3D11 + D2D 透明窗口最小原型（方案 B 验证）。
//
// 验证目标：
//   1. 非 layered 普通 Win32 窗口 + DirectComposition（每像素 alpha）能否透出桌面；
//   2. D2D 绘制内容能否由 GPU 合成；
//   3. 定时器移动 visual 是否流畅（GPU 层面平移，CPU 仅提交一次 SetOffset + Commit）；
//   4. UIAccess 模式下是否可见（后续在 UIAccess 环境运行验证）。
//
// 触发方式（不影响正常启动）：
//   set BLUERANDOM_DCOMP_PROBE=1
//   publish\BlueRandom.exe
//   会额外弹出一个 900×560 的半透明窗口，内含一个来回移动的测试色块。
//
// 备注：
//   - 本文件仅为技术验证，验证通过后可将 ResultWindow 的渲染层迁移到该架构；
//   - 依赖 Vortice.DirectComposition / Vortice.Direct3D11 / Vortice.Direct2D1。
// ==============================================================================

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using D3DFeatureLevel = Vortice.Direct3D.FeatureLevel;
using D3DDriverType = Vortice.Direct3D.DriverType;

namespace BlueRandom.UI.FRW;

/// <summary>
/// DirectComposition 透明窗口原型。
/// </summary>
public static class DCompProbe
{
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;
    private const int WM_DESTROY = 0x0002;
    private const int WM_ERASEBKGND = 0x0014;
    private const int WM_PAINT = 0x000F;
    private const int WM_TIMER = 0x0113;
    private const uint TIMER_ID = 1;
    private const int WIN_W = 900;
    private const int WIN_H = 560;

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
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
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

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

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
    private static extern void DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hWnd, UIntPtr uIDEvent);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    private static WndProc? s_wndProc;
    private static IDCompositionDevice? s_dcomp;
    private static IDCompositionDesktopDevice? s_desktop;
    private static IDCompositionTarget? s_target;
    private static IDCompositionVisual? s_visual;
    private static int s_tick;

    /// <summary>
    /// 启动原型窗口（独立 STA 线程）。
    /// </summary>
    public static void Start()
    {
        var thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "DCompProbe"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>
    /// 原型专用诊断日志（不依赖 AppLogger 初始化时机）。
    /// </summary>
    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "br_dcomp_probe.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { /* 忽略 */ }
    }

    private static void Run()
    {
        try
        {
            s_wndProc = WndProcImpl;
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                style = 0,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                hInstance = GetModuleHandleW(null),
                hCursor = IntPtr.Zero,
                hbrBackground = IntPtr.Zero,
                lpszMenuName = null,
                lpszClassName = "BlueRandomDCompProbe",
                hIcon = IntPtr.Zero,
                hIconSm = IntPtr.Zero
            };
            RegisterClassExW(ref wc);

            int x = (1920 - WIN_W) / 2;
            int y = (1080 - WIN_H) / 2;
            IntPtr hwnd = CreateWindowExW(
                WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP,
                "BlueRandomDCompProbe", "DComp Probe",
                WS_POPUP | WS_VISIBLE,
                x, y, WIN_W, WIN_H,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

            if (hwnd == IntPtr.Zero)
            {
                Log("CreateWindowEx 失败");
                return;
            }

            Log($"窗口创建成功 hwnd=0x{hwnd.ToInt64():X}");
            InitDComp(hwnd);
            Log("DirectComposition 原型就绪");
            SetTimer(hwnd, (UIntPtr)TIMER_ID, 16, IntPtr.Zero);

            while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }

            s_visual?.Dispose();
            s_target?.Dispose();
            s_desktop?.Dispose();
            s_dcomp?.Dispose();
        }
        catch (Exception ex)
        {
            Log("原型启动异常: " + ex);
        }
    }

    private static void InitDComp(IntPtr hwnd)
    {
        Log("InitDComp 开始");
        var levels = new[]
        {
            D3DFeatureLevel.Level_11_1,
            D3DFeatureLevel.Level_11_0,
            D3DFeatureLevel.Level_10_1,
            D3DFeatureLevel.Level_10_0
        };

        ID3D11Device? d3dDevice = null;
        try
        {
            d3dDevice = D3D11.D3D11CreateDevice(D3DDriverType.Hardware, DeviceCreationFlags.BgraSupport, levels);
            Log("D3D11 硬件设备创建成功");
        }
        catch (Exception ex)
        {
            Log("D3D11 硬件设备创建失败，尝试 WARP: " + ex.Message);
        }

        if (d3dDevice == null)
        {
            d3dDevice = D3D11.D3D11CreateDevice(D3DDriverType.Warp, DeviceCreationFlags.BgraSupport, levels);
            Log("D3D11 WARP 设备创建成功");
        }

        using (d3dDevice)
        using (var dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>())
        using (var d2dDevice = D2D1.D2D1CreateDevice(dxgiDevice, null))
        {
            Log("D2D 设备创建成功");
            s_dcomp = DComp.DCompositionCreateDevice2<IDCompositionDevice>(d2dDevice);
            Log("DComp 设备创建成功");
            s_desktop = s_dcomp.QueryInterface<IDCompositionDesktopDevice>();
            s_desktop.CreateTargetForHwnd(hwnd, true, out s_target);
            s_visual = s_dcomp.CreateVisual();
            s_target.SetRoot(s_visual);
            Log("DComp target / visual 创建成功");

            s_dcomp.CreateSurface(WIN_W, WIN_H, Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied, out IDCompositionSurface surface);
            using (surface)
            {
                Log("DComp surface 创建成功");
                surface.BeginDraw<ID2D1DeviceContext>(null, out ID2D1DeviceContext dc, out Int2 updateOffset);
                using (dc)
                {
                    dc.Clear(new Color4(0f, 0f, 0f, 0f));
                    using var brush = dc.CreateSolidColorBrush(new Color4(0.25f, 0.65f, 1.0f, 0.85f));
                    dc.FillRectangle(new RawRectF(updateOffset.X + 300, updateOffset.Y + 180, updateOffset.X + 600, updateOffset.Y + 380), brush);
                }

                surface.EndDraw();
                s_visual.SetContent(surface);
            }

            s_dcomp.Commit();
            Log("DComp Commit 完成");
        }
    }

    private static IntPtr WndProcImpl(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_TIMER:
                if (s_visual != null && s_dcomp != null)
                {
                    s_tick++;
                    double t = s_tick / 60.0;
                    float offset = (float)(Math.Sin(t * 2.0) * 120.0);
                    s_visual.SetOffsetX(offset + 40f);
                    s_dcomp.Commit();
                }
                return IntPtr.Zero;

            case WM_ERASEBKGND:
                return new IntPtr(1);

            case WM_PAINT:
                return IntPtr.Zero;

            case WM_DESTROY:
                KillTimer(hwnd, (UIntPtr)TIMER_ID);
                PostQuitMessage(0);
                return IntPtr.Zero;

            default:
                return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }
}
