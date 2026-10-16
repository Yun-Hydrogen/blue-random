// ==============================================================================
// 文件: AppVersion.cs
// 命名空间: BlueRandom.Core
// 作用: 统一管理应用全局语义化版本号（Version）与发布代号（Codename）。
//       具备程序集内嵌资源与开发环境上溯目录探测的双通道加载能力。
// ==============================================================================

using System;
using System.IO;
using System.Text.Json;

namespace BlueRandom.Core;

/// <summary>
/// 应用版本与代号信息管理类。
/// 动态自 package.json 读取 version 与自定义版本代号 codename，保证各前端与后端显示一致。
/// </summary>
public static class AppVersion
{
    /// <summary>
    /// 当前版本号，遵循 YY.MM.PATCH 格式（如 "26.10.02"）。
    /// </summary>
    public static string Version { get; private set; } = "26.10.02";

    /// <summary>
    /// 当前版本发布代号（如 "Shiroko"），用于 UI 标识和 GitHub Release Tag 匹配。
    /// </summary>
    public static string Codename { get; private set; } = "Shiroko";

    /// <summary>
    /// 静态构造函数，在类型首次初始化时触发 package.json 的智能探测与解析。
    /// </summary>
    static AppVersion()
    {
        TryLoadFromPackageJson();
    }

    /// <summary>
    /// 尝试从内嵌清单资源或物理磁盘路径中定位并加载 package.json。
    /// 优先级策略：
    /// 1. 优先读取编译期嵌入在程序集中的清单资源（适用于 Release 单文件打包）；
    /// 2. 若未嵌入，则逐级向父目录回溯探测（最多回溯 4 层），以便开发与单元测试环境下即时生效。
    /// </summary>
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

    /// <summary>
    /// 解析 JSON 文本中的 version 与 codename 键值，并更新全局属性。
    /// 若键不存在或为空，则保持当前值不变（具备向下容错性）。
    /// </summary>
    /// <param name="json">package.json 文本内容。</param>
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
