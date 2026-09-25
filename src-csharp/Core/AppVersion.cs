using System;
using System.IO;
using System.Text.Json;

namespace BlueRandom.Core;

/// <summary>
/// 应用版本与代号信息管理
/// 动态自 package.json 读取 version 与自定义版本代号 codename
/// </summary>
public static class AppVersion
{
    public static string Version { get; private set; } = "26.10.02";
    public static string Codename { get; private set; } = "Shiroko";

    static AppVersion()
    {
        TryLoadFromPackageJson();
    }

    private static void TryLoadFromPackageJson()
    {
        try
        {
            // 1. 优先尝试从程序集内嵌资源读取 package.json (单文件或发布模式)
            var asm = typeof(AppVersion).Assembly;
            using var stream = asm.GetManifestResourceStream("package.json")
                            ?? asm.GetManifestResourceStream("BlueRandom.package.json");
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                ParseJson(reader.ReadToEnd());
                return;
            }

            // 2. 本地开发调试时，向上探测磁盘上的 package.json
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] possiblePaths =
            [
                Path.Combine(baseDir, "package.json"),
                Path.Combine(baseDir, "..", "package.json"),
                Path.Combine(baseDir, "..", "..", "package.json"),
                Path.Combine(baseDir, "..", "..", "..", "package.json"),
                Path.Combine(baseDir, "..", "..", "..", "..", "package.json")
            ];

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    ParseJson(File.ReadAllText(path));
                    return;
                }
            }
        }
        catch
        {
            // 忽略异常，保持默认 fallback
        }
    }

    private static void ParseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("version", out var v) && v.GetString() is string ver && !string.IsNullOrWhiteSpace(ver))
        {
            Version = ver.Trim();
        }

        if (root.TryGetProperty("codename", out var c) && c.GetString() is string code && !string.IsNullOrWhiteSpace(code))
        {
            Codename = code.Trim();
        }
    }
}
