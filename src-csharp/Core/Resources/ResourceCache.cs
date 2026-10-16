// ==============================================================================
// 文件: ResourceCache.cs
// 命名空间: BlueRandom.Core.Resources
// 作用: 全局全内存静态资源缓存中心（Resource Cache）。
//       提供零磁盘写盘、零 IO 延迟的前端 WebUI 产物、WPF 原生位图、私有 GDI+ 字体（UI.ttf）
//       以及 WAV 音效流内存托管服务，深度集成 WebView2 虚拟域拦截器。
// ==============================================================================

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BlueRandom.Core.Logging;
using BlueRandom.Services;

namespace BlueRandom.Core.Resources;

/// <summary>
/// 资源缓存中心 (Resource Cache)。
/// 在程序启动或按需预加载 WebUI 界面、字体、信封纹理、角色立绘与音频流，
/// 所有内置资源全内存托管，实现零临时文件落盘与亚毫秒级访问。
/// </summary>
public static class ResourceCache
{
    [DllImport("gdi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int AddFontResourceEx(string lpszFilename, uint fl, IntPtr pdv);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, [In] ref uint pcFonts);

    private const uint FR_PRIVATE = 0x10;

    private static readonly ConcurrentDictionary<string, BitmapImage> _images = new();
    private static readonly ConcurrentDictionary<string, byte[]> _audioBytes = new();
    private static readonly ConcurrentDictionary<string, byte[]> _webuiAssets = new(StringComparer.OrdinalIgnoreCase);
    private static FontFamily? _customFont;

    /// <summary>
    /// 全局 UI 字体（南西新圆体）。
    /// 若未完成自定义加载，则安全回退至系统字体备选链。
    /// </summary>
    public static FontFamily CustomFont => _customFont ?? new FontFamily("NanxiXinyuanti Regualr, 南西新圆体, Microsoft YaHei UI, Segoe UI");

    /// <summary>
    /// 阶段 -1 极速硬加载：
    /// 在任何原生 WPF 窗体（包括启动器 SplashWindow）初始化前同步执行，
    /// 将 UI.ttf 私有注册至当前进程字体表，并预载主程序徽标与图标，杜绝首次渲染字体变形与白块闪烁。
    /// </summary>
    /// <param name="baseDir">应用程序基目录 AppDomain.CurrentDomain.BaseDirectory。</param>
    public static void EarlyInit(string baseDir)
    {
        EnsureFont(baseDir);
        LoadSingleImage(baseDir, "BlueRandom.png");
        LoadSingleImage(baseDir, "app.ico");
    }

    /// <summary>
    /// 确保应用全局 UI.ttf（南西新圆体）已正确注入至当前进程字体表。
    /// <para>
    /// 加载策略：
    /// 1. 优先探测本地发布目录或开发源码目录下的 public/fonts/UI.ttf；
    ///    若存在，调用 Win32 GDI API <c>AddFontResourceEx(..., FR_PRIVATE, ...)</c> 将其私有映射至进程，避免污染宿主系统字体库；
    /// 2. 若处于单文件打包环境或文件不存在，直接从 Assembly Manifest 嵌入资源流中读取字体二进制字节，
    ///    调用 <c>AddFontMemResourceEx</c> 注入系统 GDI 内存字体句柄，全程无需向磁盘释放任何临时 .ttf 文件。
    /// </para>
    /// </summary>
    /// <param name="baseDir">基准路径。</param>
    public static void EnsureFont(string baseDir)
    {
        if (_customFont != null) return;

        string fontPath = Path.Combine(baseDir, "public", "fonts", "UI.ttf");
        if (!File.Exists(fontPath))
        {
            string devFont = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "public", "fonts", "UI.ttf"));
            if (File.Exists(devFont)) fontPath = devFont;
        }

        if (File.Exists(fontPath))
        {
            try
            {
                // 本地磁盘存在：将 TTF 私有注册到当前进程字体表
                AddFontResourceEx(fontPath, FR_PRIVATE, IntPtr.Zero);
                var uri = new Uri(Path.GetFullPath(fontPath));
                var families = Fonts.GetFontFamilies(uri);
                var family = families.FirstOrDefault();
                _customFont = family ?? new FontFamily(uri, "./#NanxiXinyuanti Regualr, NanxiXinyuanti Regualr, 南西新圆体, Microsoft YaHei UI, Segoe UI");
                AppLogger.Info("ResourceCache", $"已硬加载内置字体: {fontPath} ({_customFont.Source})");
                return;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ResourceCache", $"字体加载失败，使用系统后备字体: {ex.Message}");
            }
        }

        // 单文件/嵌入资源模式：全内存硬加载字体，无需释放临时文件到磁盘
        try
        {
            var asm = typeof(ResourceCache).Assembly;
            using var s = asm.GetManifestResourceStream("public.fonts.UI.ttf")
                          ?? asm.GetManifestResourceStream("public.fonts\\UI.ttf");
            if (s != null)
            {
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                byte[] fontBytes = ms.ToArray();
                IntPtr pFont = Marshal.AllocCoTaskMem(fontBytes.Length);
                Marshal.Copy(fontBytes, 0, pFont, fontBytes.Length);
                uint dummy = 0;
                AddFontMemResourceEx(pFont, (uint)fontBytes.Length, IntPtr.Zero, ref dummy);
                Marshal.FreeCoTaskMem(pFont);
                _customFont = new FontFamily("NanxiXinyuanti Regualr, 南西新圆体, Microsoft YaHei UI, Segoe UI");
                AppLogger.Info("ResourceCache", "已直接从内存硬加载内置 UI.ttf 字体 (零磁盘释放)");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("ResourceCache", $"内存硬加载字体提示: {ex.Message}");
        }
    }

    /// <summary>
    /// 加载单张图片资源并缓存入并发字典。
    /// 优先从本地磁盘检索，不存在时回退至程序集嵌入资源；
    /// 无论何种途径，均通过 <c>BitmapCacheOption.OnLoad</c> 预先解码并将图片实例 <c>Freeze()</c> 冻结，
    /// 保证位图对象可以跨 WPF STA 渲染线程安全共享而无需二次封送。
    /// </summary>
    /// <param name="baseDir">基准目录路径。</param>
    /// <param name="imgName">目标图片文件名（如 BlueRandom.png、Letter_Rainbow.png）。</param>
    private static void LoadSingleImage(string baseDir, string imgName)
    {
        if (_images.ContainsKey(imgName)) return;

        string imgPath = Path.Combine(baseDir, "public", "image", imgName);
        if (!File.Exists(imgPath))
        {
            string devImg = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "public", "image", imgName));
            if (File.Exists(devImg)) imgPath = devImg;
        }

        if (File.Exists(imgPath))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(Path.GetFullPath(imgPath));
                bitmap.EndInit();
                bitmap.Freeze();
                _images[imgName] = bitmap;
                return;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ResourceCache", $"预载图片失败 [{imgName}]: {ex.Message}");
            }
        }
        else
        {
            try
            {
                var asm = typeof(ResourceCache).Assembly;
                using var s = asm.GetManifestResourceStream($"public.image.{imgName}")
                              ?? asm.GetManifestResourceStream($"public.image\\{imgName}");
                if (s != null)
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = s;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    _images[imgName] = bitmap;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ResourceCache", $"嵌入资源预载图片失败 [{imgName}]: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 全局资源全量异步预热入口（Stage 1）。
    /// <para>
    /// 执行步骤：
    /// 0. 安全清除历史版本残留于磁盘 ~/.blue random/webui 的旧目录缓存，实现零磁盘静态污染；
    /// 1. 将 Vite 构建产物（HTML/CSS/JS/SVG）全量灌入内存并发字典 _webuiAssets；
    /// 2. 加载并注册 UI.ttf 字体；
    /// 3. 并行解码并冻结三色信封纹理、阿罗娜/普拉娜立绘、托盘图标；
    /// 4. 预载并缓存抽卡点击核心 WAV 音效。
    /// </para>
    /// </summary>
    /// <param name="baseDir">应用程序根目录路径。</param>
    public static void Preload(string baseDir)
    {
        AppLogger.Info("ResourceCache", "开始预热 ResCache 全内存素材与 WebUI 前端资产...");

        // 0. 清理历史磁盘 webui 提取残留（保证零磁盘游离缓存）
        try
        {
            string oldWebUiDir = Path.Combine(YamlConfigService.GetUserDataDir(), "webui");
            if (Directory.Exists(oldWebUiDir))
            {
                Directory.Delete(oldWebUiDir, true);
                AppLogger.Info("ResourceCache", "已自动清理历史磁盘 webui 残留目录");
            }
        }
        catch { }

        // 1. 预载 WebUI 前端资产到 ResCache 内存中
        PreloadWebUiAssets(baseDir);

        // 2. 加载字体 (支持本地与单文件嵌入资源)
        EnsureFont(baseDir);

        // 3. 预热信封纹理与图标 (支持本地与单文件嵌入资源)
        string[] imageNames =
        {
            "Letter_Rainbow.png",
            "Letter_Gold.png",
            "Letter_Blue.png",
            "Arona_Empty.png",
            "Arona_Plana.png",
            "BlueRandom.png",
            "app.ico",
            "tray.png"
        };

        foreach (var imgName in imageNames)
        {
            LoadSingleImage(baseDir, imgName);
        }

        // 4. 预热音频文件 (支持本地与单文件嵌入资源)
        string sfxPath = Path.Combine(baseDir, "public", "sound", "button_click.wav");
        if (!File.Exists(sfxPath))
        {
            string devSfx = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "public", "sound", "button_click.wav"));
            if (File.Exists(devSfx)) sfxPath = devSfx;
        }

        if (File.Exists(sfxPath))
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(sfxPath);
                _audioBytes["button_click.wav"] = bytes;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ResourceCache", $"预载音效失败: {ex.Message}");
            }
        }
        else
        {
            try
            {
                var asm = typeof(ResourceCache).Assembly;
                using var s = asm.GetManifestResourceStream("public.sound.button_click.wav")
                              ?? asm.GetManifestResourceStream("public.sound\\button_click.wav");
                if (s != null)
                {
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    _audioBytes["button_click.wav"] = ms.ToArray();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ResourceCache", $"嵌入资源预载音效失败: {ex.Message}");
            }
        }

        AppLogger.Info("ResourceCache", $"ResCache 资源预热完成，已缓存 {_webuiAssets.Count} 个 WebUI 静态文件、{_images.Count} 张图片、{_audioBytes.Count} 个音频");
    }

    /// <summary>
    /// 将前端 Vite 打包产物（dist 目录或嵌入清单流）完整加载至内存映射字典。
    /// 无论运行于标准文件夹还是单文件独立打包（Single-File Executable），均保证虚拟域能够直接读取二进制字节流。
    /// </summary>
    /// <param name="baseDir">程序运行根目录。</param>
    private static void PreloadWebUiAssets(string baseDir)
    {
        _webuiAssets.Clear();

        string distDir = Path.Combine(baseDir, "dist");
        if (!Directory.Exists(distDir) || !File.Exists(Path.Combine(distDir, "index.html")))
        {
            string devCandidate = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "dist"));
            if (Directory.Exists(devCandidate) && File.Exists(Path.Combine(devCandidate, "index.html")))
            {
                distDir = devCandidate;
            }
            else
            {
                distDir = string.Empty;
            }
        }

        if (!string.IsNullOrEmpty(distDir) && Directory.Exists(distDir))
        {
            try
            {
                foreach (string file in Directory.GetFiles(distDir, "*", SearchOption.AllDirectories))
                {
                    string relPath = Path.GetRelativePath(distDir, file).Replace('\\', '/').TrimStart('/');
                    _webuiAssets[relPath] = File.ReadAllBytes(file);
                }
                AppLogger.Info("ResourceCache", $"已向 ResCache 预热注入 {_webuiAssets.Count} 个 WebUI 前端资源 (来自本地 dist 目录)");
                return;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ResourceCache", $"从本地 dist 加载 WebUI 异常: {ex.Message}");
            }
        }

        // 单文件发布模式 / 无法从本地磁盘读取时：从 Assembly Manifest 嵌入资源载入内存
        try
        {
            var asm = typeof(ResourceCache).Assembly;
            foreach (string resName in asm.GetManifestResourceNames())
            {
                if (resName.StartsWith("dist."))
                {
                    string relPath = resName.Substring("dist.".Length).Replace('\\', '/');
                    using var stream = asm.GetManifestResourceStream(resName);
                    if (stream != null)
                    {
                        using var ms = new MemoryStream();
                        stream.CopyTo(ms);
                        _webuiAssets[relPath] = ms.ToArray();
                    }
                }
            }
            AppLogger.Info("ResourceCache", $"已向 ResCache 预热注入 {_webuiAssets.Count} 个 WebUI 前端静态资源 (单文件嵌入资源，零磁盘释放)");
        }
        catch (Exception ex)
        {
            AppLogger.Error("ResourceCache", "从嵌入资源加载 WebUI 资产失败", ex);
        }
    }

    /// <summary>
    /// 从内存字典检索 WebUI 资源二进制内容。
    /// 当客户端请求空路径或根路径时，自动归一化解析为 "index.html"。
    /// </summary>
    /// <param name="relativePath">相对 URI 路径（如 "assets/index.js"）。</param>
    /// <returns>资源二进制字节数组；若未命中则返回 null。</returns>
    public static byte[]? GetWebUiAsset(string relativePath)
    {
        if (_webuiAssets.IsEmpty)
        {
            PreloadWebUiAssets(AppDomain.CurrentDomain.BaseDirectory);
        }
        string key = relativePath.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrEmpty(key)) key = "index.html";
        return _webuiAssets.TryGetValue(key, out var bytes) ? bytes : null;
    }

    /// <summary>
    /// 从内存预热池中快速获取已冻结（Frozen）的 WPF 原生位图资源。
    /// </summary>
    /// <param name="key">图片键名（如 "BlueRandom.png"、"Arona_Plana.png"）。</param>
    /// <returns>可跨线程直接绑定的 ImageSource；未找到时返回 null。</returns>
    public static ImageSource? GetImage(string key)
    {
        return _images.TryGetValue(key, out var img) ? img : null;
    }

    /// <summary>
    /// 根据抽卡结果的信封颜色品阶，获取对应的信封纹理贴图。
    /// </summary>
    /// <param name="letterColor">信封等级代号（"rainbow" 三星彩、"gold" 二星金、"blue" 一星蓝）。</param>
    /// <returns>对应的信封 ImageSource。</returns>
    public static ImageSource? GetEnvelopeImage(string letterColor)
    {
        string key = (letterColor?.ToLowerInvariant()) switch
        {
            "gold" => "Letter_Gold.png",
            "blue" => "Letter_Blue.png",
            _ => "Letter_Rainbow.png"
        };
        return GetImage(key);
    }

    /// <summary>
    /// 获取内存中已缓存的音频二进制数据。
    /// </summary>
    /// <param name="key">音频文件名（如 "button_click.wav"）。</param>
    /// <returns>音频二进制字节数组；不存在时返回 null。</returns>
    public static byte[]? GetAudioBytes(string key)
    {
        return _audioBytes.TryGetValue(key, out var bytes) ? bytes : null;
    }
}
