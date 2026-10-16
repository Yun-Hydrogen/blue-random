// ==============================================================================
// 文件: AppLogger.cs
// 命名空间: BlueRandom.Core.Logging
// 作用: 全局统一诊断日志记录器，支持控制台彩色高亮输出与用户目录文件持久化落盘，
//       为跨线程并发调用提供线程安全锁保障。
// ==============================================================================

using System;
using System.IO;
using System.Text;

namespace BlueRandom.Core.Logging;

/// <summary>
/// 统一日志记录器。
/// 负责控制台格式化色彩输出，并将运行日志同步写入用户目录下的 log.txt。
/// </summary>
public static class AppLogger
{
    private static readonly object _lock = new();
    private static string? _logFilePath;

    /// <summary>
    /// 初始化日志记录器并指定工作日志目录。
    /// 每次冷启动时会创建目录并重置 log.txt 文件内容，打入启动时间戳头。
    /// </summary>
    /// <param name="userDataDir">用户数据根目录（通常为 ~/.blue random）。</param>
    public static void Initialize(string userDataDir)
    {
        try
        {
            Directory.CreateDirectory(userDataDir);
            _logFilePath = Path.Combine(userDataDir, "log.txt");
            // 每次启动清空日志文件
            File.WriteAllText(_logFilePath, $"[INIT] Blue Random 启动日志 - {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AppLogger] 初始化日志文件失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 记录普通信息级别日志（INFO）。
    /// </summary>
    /// <param name="module">日志来源模块标签（如 IPC、Config、WinSys 等）。</param>
    /// <param name="message">日志文本详情。</param>
    public static void Info(string module, string message) => Log("INFO", module, message);

    /// <summary>
    /// 记录警告级别日志（WARN）。
    /// </summary>
    /// <param name="module">日志来源模块标签。</param>
    /// <param name="message">警告详情说明。</param>
    public static void Warn(string module, string message) => Log("WARN", module, message);

    /// <summary>
    /// 记录错误及异常调用栈信息（ERROR）。
    /// </summary>
    /// <param name="module">日志来源模块标签。</param>
    /// <param name="message">错误概要描述。</param>
    /// <param name="ex">捕获到的关联异常对象（包含堆栈追踪）。</param>
    public static void Error(string module, string message, Exception? ex = null)
    {
        string fullMessage = ex == null ? message : $"{message} | 异常: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
        Log("ERROR", module, fullMessage);
    }

    /// <summary>
    /// 内部核心落盘与控制台打印实现。
    /// 包含控制台终端前景色切换与异常安全的文件追加写机制。
    /// </summary>
    /// <param name="level">日志级别（INFO / WARN / ERROR）。</param>
    /// <param name="module">模块名。</param>
    /// <param name="message">格式化后的文本内容。</param>
    private static void Log(string level, string module, string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        string line = $"[{timestamp}] [{level}] [{module}] {message}";

        // 控制台着色输出
        lock (_lock)
        {
            var oldColor = Console.ForegroundColor;
            Console.ForegroundColor = level switch
            {
                "ERROR" => ConsoleColor.Red,
                "WARN" => ConsoleColor.Yellow,
                _ => ConsoleColor.Cyan
            };
            Console.WriteLine(line);
            Console.ForegroundColor = oldColor;

            // 写入日志文件
            if (!string.IsNullOrEmpty(_logFilePath))
            {
                try
                {
                    File.AppendAllText(_logFilePath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // 忽略写日志冲突
                }
            }
        }
    }
}
