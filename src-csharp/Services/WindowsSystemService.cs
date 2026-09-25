using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using BlueRandom.Core.Logging;

namespace BlueRandom.Services;

/// <summary>
/// Windows 系统权限与能力服务
/// 处理管理员提权、UIAccess、计划任务与防遮挡置顶
/// </summary>
public static class WindowsSystemService
{
    private const string ArgElevatedAdmin = "--elevatedadmin";
    private const string ArgElevatedUia = "--elevateduia";

    static WindowsSystemService()
    {
        if (OperatingSystem.IsWindows())
        {
            NativeLibrary.SetDllImportResolver(typeof(WindowsSystemService).Assembly, (libraryName, assembly, searchPath) =>
            {
                if (libraryName.Equals("uiaccess.dll", StringComparison.OrdinalIgnoreCase) ||
                    libraryName.Equals("uiaccess", StringComparison.OrdinalIgnoreCase))
                {
                    string candidate = GetUiAccessDllPath();
                    if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                    {
                        if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
                        {
                            return handle;
                        }
                    }
                }
                return IntPtr.Zero;
            });
        }
    }

    /// <summary>
    /// 获取 uiaccess.dll 的绝对路径 (多路径智能检索 + 单文件内嵌自释放)
    /// </summary>
    public static string GetUiAccessDllPath()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "uiaccess.dll"),
            Path.Combine(baseDir, "third_party", "uiaccess.dll"),
            Path.Combine(Environment.CurrentDirectory, "uiaccess.dll"),
            Path.Combine(Environment.CurrentDirectory, "third_party", "uiaccess.dll"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "uiaccess.dll")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "third_party", "uiaccess.dll")),
        ];

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // 检查用户数据目录 ~/.blue random/uiaccess.dll
        string userDataDir = YamlConfigService.GetUserDataDir();
        string userDll = Path.Combine(userDataDir, "uiaccess.dll");
        if (File.Exists(userDll) && new FileInfo(userDll).Length > 0)
        {
            return userDll;
        }

        // 若外部无 DLL，从单文件内置 EmbeddedResource 自动提取
        try
        {
            var asm = typeof(WindowsSystemService).Assembly;
            using var stream = asm.GetManifestResourceStream("BlueRandom.uiaccess.dll")
                               ?? asm.GetManifestResourceStream("uiaccess.dll");
            if (stream != null)
            {
                Directory.CreateDirectory(userDataDir);
                using var fs = new FileStream(userDll, FileMode.Create, FileAccess.Write, FileShare.Read);
                stream.CopyTo(fs);
                AppLogger.Info("WinSys", $"已从单文件内置资源提取 uiaccess.dll -> {userDll}");
                return userDll;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("WinSys", "提取内置 uiaccess.dll 异常", ex);
        }

        return userDll;
    }

    /// <summary>
    /// 检测系统中是否存在可用的 uiaccess.dll（包含单文件内置资源检测）
    /// </summary>
    public static bool DoesUiAccessDllExist()
    {
        string path = GetUiAccessDllPath();
        if (!string.IsNullOrEmpty(path) && File.Exists(path)) return true;

        var asm = typeof(WindowsSystemService).Assembly;
        return asm.GetManifestResourceInfo("BlueRandom.uiaccess.dll") != null ||
               asm.GetManifestResourceInfo("uiaccess.dll") != null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOACTIVATE = 0x0010;

    // RunUIAccess 动态库导出函数定义
    [DllImport("uiaccess.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool StartUIAccessProcess(string appName, string cmdLine, uint flag, IntPtr pPid, uint dwSession);

    [DllImport("uiaccess.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    private static extern bool IsUIAccess();

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    /// <summary>
    /// 当前进程是否以管理员权限运行
    /// </summary>
    public static bool IsProcessElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            AppLogger.Error("WinSys", "检测管理员权限异常", ex);
            return false;
        }
    }

    /// <summary>
    /// 当前进程是否以 UIAccess 权限运行
    /// </summary>
    public static bool IsProcessUiAccess()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return IsUIAccess();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 检查并按需执行管理员提权（带防死循环检测）
    /// </summary>
    /// <returns>若已触发重启退出返回 true，继续运行返回 false</returns>
    public static bool HandleAdminElevation(string[] args, bool requireAdmin)
    {
        if (!requireAdmin) return false;
        if (IsProcessElevated()) return false; // 已具备管理员权限

        // 防死循环判断：若带有已提权标志但仍无权限，说明被用户拒绝
        if (Array.Exists(args, a => string.Equals(a, ArgElevatedAdmin, StringComparison.OrdinalIgnoreCase)))
        {
            AppLogger.Warn("WinSys", "检测到 --elevatedadmin 标志但未获得管理员权限，用户已拒绝 UAC 提权请求，继续以普通权限运行。");
            return false;
        }

        try
        {
            string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return false;

            AppLogger.Info("WinSys", "配置要求管理员启动，正在发起 UAC 提权申请...");

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = ArgElevatedAdmin,
                UseShellExecute = true,
                Verb = "runas"
            };

            Process.Start(psi);
            return true; // 发起成功，当前实例需退出
        }
        catch (Exception ex)
        {
            AppLogger.Warn("WinSys", $"用户取消或 UAC 提权失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 检查并按需执行 UIAccess 重启（需前置具备管理员权限，带防死循环检测）
    /// </summary>
    public static bool HandleUiAccessElevation(string[] args, bool requireUia)
    {
        if (!requireUia) return false;
        if (IsProcessUiAccess()) return false; // 已具备 UIA 权限

        // 若带有 --elevateduia 但仍未具备，则静默继续并记录日志，严禁死循环
        if (Array.Exists(args, a => string.Equals(a, ArgElevatedUia, StringComparison.OrdinalIgnoreCase)))
        {
            AppLogger.Warn("WinSys", "检测到 --elevateduia 标志但未获取到 UIAccess 权限，跳过继续运行。");
            return false;
        }

        if (!IsProcessElevated())
        {
            AppLogger.Warn("WinSys", "UIAccess 需要前置管理员权限，当前非管理员，跳过。");
            return false;
        }

        try
        {
            string exePath = Environment.ProcessPath ?? "";
            if (!File.Exists(exePath)) return false;

            string dllPath = GetUiAccessDllPath();
            if (!File.Exists(dllPath))
            {
                AppLogger.Warn("WinSys", $"未找到 uiaccess.dll: {dllPath}，跳过 UIAccess。");
                return false;
            }

            uint sessionId = WTSGetActiveConsoleSessionId();
            string cmdLine = $"\"{exePath}\" {ArgElevatedUia}";

            AppLogger.Info("WinSys", "正在通过 uiaccess.dll 启动 UIAccess 进程...");
            bool ok = StartUIAccessProcess(exePath, cmdLine, 0, IntPtr.Zero, sessionId);
            if (ok)
            {
                AppLogger.Info("WinSys", "UIAccess 进程已成功拉起，退出当前进程。");
                return true;
            }
            else
            {
                int err = Marshal.GetLastWin32Error();
                AppLogger.Error("WinSys", $"StartUIAccessProcess 失败，错误码: {err}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("WinSys", "UIAccess 提权调用异常", ex);
        }

        return false;
    }

    /// <summary>
    /// 为指定原生窗口刷新置顶层级
    /// </summary>
    public static void RefreshTopMost(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        try
        {
            SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW | SWP_NOACTIVATE);
            BringWindowToTop(hWnd);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("WinSys", $"刷新置顶失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 创建或更新 Windows 开机启动计划任务 (schtasks)
    /// </summary>
    public static bool CreateStartupTask(string taskName, string exePath, bool asAdmin)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(taskName)) taskName = "Blue Random (Admin)";
            if (string.IsNullOrWhiteSpace(exePath)) exePath = Environment.ProcessPath ?? "";
            if (!File.Exists(exePath)) return false;

            string rl = asAdmin ? "/rl highest" : "";
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/create /tn \"{taskName}\" /tr \"\\\"{exePath}\\\"\" /sc onlogon {rl} /f",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            bool success = p?.ExitCode == 0;
            AppLogger.Info("WinSys", $"创建计划任务结果: {success} (ExitCode: {p?.ExitCode})");
            return success;
        }
        catch (Exception ex)
        {
            AppLogger.Error("WinSys", "创建开机计划任务失败", ex);
            return false;
        }
    }
}
