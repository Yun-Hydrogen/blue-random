using System;
using System.IO;
using System.Text;

namespace BlueRandom.Core.Logging;

/// <summary>
/// 统一日志记录器
/// 负责控制台格式化输出，并将日志同步写入 %LocalAppData%\BlueRandom\log.txt
/// </summary>
public static class AppLogger
{
    private static readonly object _lock = new();
    private static string? _logFilePath;

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

    public static void Info(string module, string message) => Log("INFO", module, message);
    public static void Warn(string module, string message) => Log("WARN", module, message);
    public static void Error(string module, string message, Exception? ex = null)
    {
        string fullMessage = ex == null ? message : $"{message} | 异常: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
        Log("ERROR", module, fullMessage);
    }

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
