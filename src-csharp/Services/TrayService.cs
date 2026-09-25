using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BlueRandom.Core.Ipc;
using BlueRandom.Core.Logging;

namespace BlueRandom.Services;

/// <summary>
/// 原生系统托盘服务 (Pure Win32 Shell_NotifyIcon + 现代化精致 Fluent ContextMenu)
/// 独立 STA 线程运行，自动锚定 Default 桌面，零依赖且 100% 稳定
/// </summary>
public static class TrayService
{
    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;

    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 1024;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_NULL = 0x0000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenDesktop(string lpszDesktop, int dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static Thread? _trayThread;
    private static Dispatcher? _trayDispatcher;
    private static HwndSource? _hwndSource;
    private static NOTIFYICONDATA _nid;
    private static ContextMenu? _contextMenu;
    private static bool _isCreated = false;

    public static void Initialize()
    {
        _trayThread = new Thread(() =>
        {
            try
            {
                // 尝试绑定 Default 交互桌面（针对自动化/沙盒环境确保能访问 Shell_TrayWnd）
                try
                {
                    IntPtr hDesk = OpenDesktop("Default", 0, false, 0x01FF);
                    if (hDesk != IntPtr.Zero)
                    {
                        SetThreadDesktop(hDesk);
                    }
                }
                catch
                {
                    // 忽略桌面切换异常，继续尝试默认环境挂载
                }

                _trayDispatcher = Dispatcher.CurrentDispatcher;
                CreateTraySinkAndIcon();
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Tray", "托盘线程异常", ex);
            }
        })
        {
            Name = "Tray_UI_Thread",
            IsBackground = true
        };
        _trayThread.SetApartmentState(ApartmentState.STA);
        _trayThread.Start();
    }

    private static void CreateTraySinkAndIcon()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string iconPath = Path.Combine(baseDir, "public", "image", "app.ico");

        // 构建现代化精美托盘右键菜单
        _contextMenu = BuildModernContextMenu();

        // 优先从 exe 提取内嵌的 App.ico，其次从磁盘加载，最后使用系统默认图标
        System.Drawing.Icon? trayIcon = null;
        string exePath = Environment.ProcessPath ?? "";
        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
        {
            try
            {
                trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Tray", $"从 exe 提取图标失败: {ex.Message}");
            }
        }

        if (trayIcon == null && File.Exists(iconPath))
        {
            try
            {
                trayIcon = new System.Drawing.Icon(iconPath);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Tray", $"从磁盘加载图标失败: {ex.Message}");
            }
        }

        IntPtr hIcon = (trayIcon ?? System.Drawing.SystemIcons.Application).Handle;

        // 创建隐藏的消息接收窗口
        var parameters = new HwndSourceParameters("BlueRandomTraySink")
        {
            Width = 0,
            Height = 0,
            PositionX = -10000,
            PositionY = -10000,
            WindowStyle = 0x800000
        };
        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);

        // 构造 NOTIFYICONDATA 结构体挂载托盘
        _nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwndSource.Handle,
            uID = 2026,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = hIcon,
            szTip = "Blue Random | 蔚蓝点名"
        };

        bool success = Shell_NotifyIcon(NIM_ADD, ref _nid);
        if (success)
        {
            _isCreated = true;
            AppLogger.Info("Tray", "系统托盘图标已成功挂载并显示");
        }
        else
        {
            int err = Marshal.GetLastWin32Error();
            AppLogger.Warn("Tray", $"Shell_NotifyIcon(NIM_ADD) 未就绪 (Win32 Error: {err})，托盘服务保持待命");
        }
    }

    private static ContextMenu BuildModernContextMenu()
    {
        // 1. 自定义 ContextMenu 外观容器 (圆角、柔和阴影、纯白背景)
        var menuTemplate = new ControlTemplate(typeof(ContextMenu));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xFC, 0xFD, 0xFF)));
        borderFactory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0xCD, 0xE2, 0xF5)));
        borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        borderFactory.SetValue(Border.PaddingProperty, new Thickness(4));

        var shadow = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 18,
            ShadowDepth = 4,
            Direction = 270,
            Color = Color.FromRgb(0x18, 0x30, 0x50),
            Opacity = 0.20
        };
        borderFactory.SetValue(Border.EffectProperty, shadow);

        var stackFactory = new FrameworkElementFactory(typeof(StackPanel));
        stackFactory.SetValue(StackPanel.IsItemsHostProperty, true);
        borderFactory.AppendChild(stackFactory);

        menuTemplate.VisualTree = borderFactory;

        var menu = new ContextMenu
        {
            Template = menuTemplate,
            SnapsToDevicePixels = true,
            MinWidth = 180
        };

        // 2. 品牌 Header 标头条
        var headerBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF7, 0xFF)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(2, 2, 2, 4)
        };
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconTb = new TextBlock
        {
            Text = "✨",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        Grid.SetColumn(iconTb, 0);
        headerGrid.Children.Add(iconTb);

        var titleTb = new TextBlock
        {
            Text = "蔚蓝点名",
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x12, 0x8A, 0xFA)),
            FontFamily = new FontFamily("Segoe UI Variable, Microsoft YaHei UI, sans-serif"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleTb, 1);
        headerGrid.Children.Add(titleTb);

        var badgeBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xE1, 0xF2, 0xFE)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            VerticalAlignment = VerticalAlignment.Center
        };
        var badgeTb = new TextBlock
        {
            Text = "NEXT",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x02, 0x88, 0xD1)),
            FontFamily = new FontFamily("Segoe UI Variable, Microsoft YaHei UI, sans-serif")
        };
        badgeBorder.Child = badgeTb;
        Grid.SetColumn(badgeBorder, 2);
        headerGrid.Children.Add(badgeBorder);

        headerBorder.Child = headerGrid;
        menu.Items.Add(headerBorder);

        // 3. 配置面板菜单项
        menu.Items.Add(CreateStyledMenuItem("配置面板", "⚙️", "S", () =>
        {
            IpcBroker.Send(IpcMessage.Create(IpcAction.Get, IpcModule.Cp, "ShowConfigPanel"));
        }));

        // 4. 显示悬浮球菜单项
        menu.Items.Add(CreateStyledMenuItem("显示悬浮球", "🎯", "F", () =>
        {
            IpcBroker.Send(IpcMessage.Create(IpcAction.Get, IpcModule.Fcb, "ShowWindow"));
        }));

        // 分割线
        menu.Items.Add(CreateStyledSeparator());

        // 5. 重启应用菜单项
        menu.Items.Add(CreateStyledMenuItem("重启应用", "🔄", "R", () =>
        {
            string exePath = Environment.ProcessPath ?? "";
            if (!string.IsNullOrEmpty(exePath))
            {
                Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true });
            }
            Dispose();
            Environment.Exit(0);
        }));

        // 6. 退出程序菜单项
        menu.Items.Add(CreateStyledMenuItem("退出程序", "❌", "X", () =>
        {
            AppLogger.Info("Tray", "用户从托盘点击退出");
            IpcBroker.Send(IpcMessage.Create(IpcAction.Dest, IpcModule.Ipc, "Destroy"));
            Dispose();
            Environment.Exit(0);
        }));

        return menu;
    }

    private static MenuItem CreateStyledMenuItem(string title, string iconGlyph, string shortcut, Action onClick)
    {
        var item = new MenuItem
        {
            Header = title,
            InputGestureText = shortcut,
            Height = 34,
            Cursor = Cursors.Hand
        };

        var template = new ControlTemplate(typeof(MenuItem));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.Name = "ItemBorder";
        borderFactory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        borderFactory.SetValue(Border.PaddingProperty, new Thickness(8, 0, 10, 0));
        borderFactory.SetValue(Border.MarginProperty, new Thickness(1, 1, 1, 1));

        var gridFactory = new FrameworkElementFactory(typeof(Grid));

        var colIcon = new FrameworkElementFactory(typeof(ColumnDefinition));
        colIcon.SetValue(ColumnDefinition.WidthProperty, new GridLength(24));
        gridFactory.AppendChild(colIcon);

        var colTitle = new FrameworkElementFactory(typeof(ColumnDefinition));
        colTitle.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        gridFactory.AppendChild(colTitle);

        var colShortcut = new FrameworkElementFactory(typeof(ColumnDefinition));
        colShortcut.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
        gridFactory.AppendChild(colShortcut);

        // 图标 / Glyph
        var glyphFactory = new FrameworkElementFactory(typeof(TextBlock));
        glyphFactory.SetValue(TextBlock.TextProperty, iconGlyph);
        glyphFactory.SetValue(TextBlock.FontSizeProperty, 13.5);
        glyphFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        glyphFactory.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        glyphFactory.SetValue(Grid.ColumnProperty, 0);
        gridFactory.AppendChild(glyphFactory);

        // 标题
        var textFactory = new FrameworkElementFactory(typeof(TextBlock));
        textFactory.Name = "TitleText";
        textFactory.SetValue(TextBlock.TextProperty, title);
        textFactory.SetValue(TextBlock.FontSizeProperty, 12.5);
        textFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.Medium);
        textFactory.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x2C, 0x3E, 0x50)));
        textFactory.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe UI Variable, Microsoft YaHei UI, sans-serif"));
        textFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        textFactory.SetValue(Grid.ColumnProperty, 1);
        gridFactory.AppendChild(textFactory);

        // 快捷键
        if (!string.IsNullOrEmpty(shortcut))
        {
            var shortcutFactory = new FrameworkElementFactory(typeof(TextBlock));
            shortcutFactory.SetValue(TextBlock.TextProperty, shortcut);
            shortcutFactory.SetValue(TextBlock.FontSizeProperty, 11.0);
            shortcutFactory.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x8F, 0xA3, 0xB8)));
            shortcutFactory.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe UI Variable, Microsoft YaHei UI, sans-serif"));
            shortcutFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            shortcutFactory.SetValue(Grid.ColumnProperty, 2);
            gridFactory.AppendChild(shortcutFactory);
        }

        borderFactory.AppendChild(gridFactory);
        template.VisualTree = borderFactory;

        // 鼠标悬停高亮触发器 (柔和浅蓝背景 + 主题蓝字体)
        var highlightTrigger = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
        highlightTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xED, 0xF6, 0xFF)), "ItemBorder"));
        highlightTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x12, 0x8A, 0xFA)), "TitleText"));
        template.Triggers.Add(highlightTrigger);

        item.Template = template;
        item.Click += (_, _) => onClick();
        return item;
    }

    private static Separator CreateStyledSeparator()
    {
        var sep = new Separator
        {
            Height = 1,
            Margin = new Thickness(6, 4, 6, 4)
        };
        var sepTemplate = new ControlTemplate(typeof(Separator));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.HeightProperty, 1.0);
        borderFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xEA, 0xF1, 0xF8)));
        sepTemplate.VisualTree = borderFactory;
        sep.Template = sepTemplate;
        return sep;
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            int eventId = lParam.ToInt32();
            if (eventId == WM_RBUTTONUP)
            {
                if (_contextMenu != null && _hwndSource != null)
                {
                    SetForegroundWindow(_hwndSource.Handle);
                    _contextMenu.Placement = PlacementMode.MousePoint;
                    _contextMenu.IsOpen = true;
                    PostMessage(_hwndSource.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
                }
                handled = true;
            }
            else if (eventId == WM_LBUTTONUP || eventId == WM_LBUTTONDBLCLK)
            {
                IpcBroker.Send(IpcMessage.Create(IpcAction.Get, IpcModule.Cp, "ShowConfigPanel"));
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public static void Dispose()
    {
        if (_trayDispatcher != null && !_trayDispatcher.HasShutdownStarted)
        {
            _trayDispatcher.Invoke(() =>
            {
                if (_isCreated)
                {
                    Shell_NotifyIcon(NIM_DELETE, ref _nid);
                    _isCreated = false;
                }

                if (_hwndSource != null)
                {
                    _hwndSource.RemoveHook(WndProc);
                    _hwndSource.Dispose();
                    _hwndSource = null;
                }

                _contextMenu = null;
                _trayDispatcher.InvokeShutdown();
            });
        }
    }
}
