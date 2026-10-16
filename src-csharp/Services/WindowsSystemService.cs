// ==============================================================================
// 文件: WindowsSystemService.cs
// 命名空间: BlueRandom.Services
// 作用: Windows 系统级安全权限、UAC 提权、UIAccess 桌面穿透与计划任务自启服务。
//       提供嵌入式 uiaccess.dll 自动解析与解压加载器（DllImportResolver）、
//       防死循环标志自愈仲裁（--elevatedadmin / --elevateduia）以及 Win32 顶层 Z 序保活。
// ==============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using BlueRandom.Core.Logging;

namespace BlueRandom.Services;

/// <summary>
/// Windows 系统权限与能力服务。
/// 处理管理员提权、UIAccess、计划任务与防遮挡置顶，提供深层次系统交互能力。
/// </summary>
public static class WindowsSystemService
{
    private const string ArgElevatedAdmin = "--elevatedadmin";
    private const string ArgElevatedUia = "--elevateduia";

    /// <summary>
    /// 静态构造函数。
    /// 注册当前程序集的 DllImportResolver，拦截对 uiaccess.dll 的 P/Invoke 动态加载请求，
    /// 自动重定向至通过 GetUiAccessDllPath 探测或提取的物理文件路径。
    /// </summary>
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
    /// 获取 uiaccess.dll 的绝对物理文件路径。
    /// <para>
    /// 检索与自愈流程：
    /// 1. 扫描当前工作目录、程序目录及第三方 third_party 候选路径；
    /// 2. 检查用户数据目录 ~/.blue random/uiaccess.dll；
    /// 3. 若物理磁盘均不存在，自动从当前程序集清单嵌入资源（EmbeddedResource）中流式读取，
    ///    并释放至 ~/.blue random/uiaccess.dll，实现单文件部署模式下的无感运行。
    /// </para>
    /// </summary>
    /// <returns>uiaccess.dll 的绝对路径。</returns>
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
    /// 检测系统中是否存在可用的 uiaccess.dll（覆盖外部物理磁盘检索与程序集内置嵌入资源两重探测）。
    /// </summary>
    /// <returns>存在或可自动解压释放返回 true，完全缺失返回 false。</returns>
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
    /// 检查当前进程 Primary Token 是否已拥有 Windows 内置管理员角色（Administrator）。
    /// </summary>
    /// <returns>以高权限（Elevated）运行返回 true，普通标准权限返回 false。</returns>
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
    /// 检查当前进程是否已具备 UIAccess（User Interface Access）特权。
    /// UIAccess 特权允许窗口置顶于普通 Administrator 窗口及全屏游戏之上，防止悬浮球被抢占遮挡。
    /// </summary>
    /// <returns>已激活 UIAccess 模式返回 true，否则返回 false。</returns>
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
    /// 检查并按需执行管理员 UAC 提权重启流程。
    /// <para>
    /// 关键自愈机制（死循环拦截）：
    /// 1. 若当前配置未开启 requireAdmin，直接跳过；
    /// 2. 若当前已具备管理员权限，直接跳过；
    /// 3. 若检测到命令行参数中已存在 <c>--elevatedadmin</c> 标志但仍然未拥有管理员权限，
    ///    证明上一轮弹出的 Windows UAC 提权对话框已被用户主动点击“否”拒绝；
    ///    此时立即中止重复提权尝试，降级为普通权限继续运行，彻底杜绝死循环弹窗；
    /// 4. 首次提权时附带 <c>--elevatedadmin</c> 参数并使用 "runas" 动词重启进程，当前低权限实例退出。
    /// </para>
    /// </summary>
    /// <param name="args">当前实例接收到的原始命令行参数数组。</param>
    /// <param name="requireAdmin">配置是否要求以管理员权限启动。</param>
    /// <returns>若已成功唤起新提权进程且当前实例需退出返回 true；无需提权或用户拒绝后继续运行返回 false。</returns>
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
    /// 检查并按需执行 UIAccess 特权进程拉起重启流程。
    /// <para>
    /// 核心原理与安全防护：
    /// 1. UIAccess 要求当前发起方必须先行具备 Administrator 权限；若非管理员则安全放弃；
    /// 2. 死循环防护：若已携带 <c>--elevateduia</c> 参数但未能成功获得 UIA 标志，跳过提权平稳运行；
    /// 3. 获取活动控制台会话 ID（WTSGetActiveConsoleSessionId），通过 uiaccess.dll 导出的 StartUIAccessProcess
    ///    将当前可执行程序注入该 Session 并附加 UIA 令牌；
    /// 4. 成功拉起后当前进程正常退出。
    /// </para>
    /// </summary>
    /// <param name="args">命令行参数数组。</param>
    /// <param name="requireUia">配置是否开启 UIAccess 桌面穿透置顶。</param>
    /// <returns>若成功拉起新 UIAccess 实例且当前进程应退出返回 true，否则返回 false。</returns>
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
    /// 为指定的原生 HWND 窗口句柄强制重设 HWND_TOPMOST 置顶 Z 序。
    /// 组合使用 SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW | SWP_NOACTIVATE，
    /// 在不抢夺当前用户键盘输入焦点与活动窗口的前提下，保证悬浮球恒处于最上层。
    /// </summary>
    /// <param name="hWnd">目标窗口的 Win32 HWND 句柄。</param>
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
    /// 创建或覆盖更新 Windows 登录自启动计划任务（schtasks）。
    /// <para>
    /// 机制优势：
    /// 采用 Windows Task Scheduler（/sc onlogon）替代传统 Run 注册表启动项；
    /// 当配合 /rl highest（以最高管理员权限运行）时，系统在用户开机登录时可静默以管理员提权启动，
    /// 完全跳过每次开机令人烦扰的 Windows UAC 弹窗提示。
    /// </para>
    /// </summary>
    /// <param name="taskName">计划任务名称（默认 "Blue Random (Admin)"）。</param>
    /// <param name="exePath">待启动的 exe 完整物理路径。</param>
    /// <param name="asAdmin">是否以管理员最高权限运行（/rl highest）。</param>
    /// <returns>创建或更新成功返回 true，执行 schtasks 异常返回 false。</returns>
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
