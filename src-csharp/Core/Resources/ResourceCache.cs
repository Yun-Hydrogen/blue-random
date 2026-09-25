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
/// 资源缓存中心 (Resource Cache)
/// 在程序启动或按需预加载 WebUI 界面、字体、信封纹理、角色立绘与音频流，所有内置资源全内存托管，零磁盘释放与零磁盘 IO 延迟
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
    /// 全局 UI 字体（南西新圆体）
    /// </summary>
    public static FontFamily CustomFont => _customFont ?? new FontFamily("NanxiXinyuanti Regualr, 南西新圆体, Microsoft YaHei UI, Segoe UI");

    /// <summary>
    /// 阶段 -1 极速硬加载：在任何原生窗口（包括 Splash 启动器）实例化前同步硬加载 UI.ttf 字体并私有注册至进程字体表，同时预载应用 Logo
    /// </summary>
    public static void EarlyInit(string baseDir)
    {
        EnsureFont(baseDir);
        LoadSingleImage(baseDir, "BlueRandom.png");
        LoadSingleImage(baseDir, "app.ico");
    }

    /// <summary>
    /// 同步确保字体已加载并私有注册至 Windows 进程字体表 (优先全内存载入，避免磁盘写文件)
    /// </summary>
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
    /// 初始化并预热资源 (将前端资产、字体、图标与音效全部载入 ResCache 内存中)
    /// </summary>
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
    /// 预加载 WebUI 界面产物至内存字典
    /// </summary>
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
    /// 从 ResCache 内存中检索 WebUI 资源
    /// </summary>
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
    /// 获取已缓存的图片纹理
    /// </summary>
    public static ImageSource? GetImage(string key)
    {
        return _images.TryGetValue(key, out var img) ? img : null;
    }

    /// <summary>
    /// 获取信封图片 (rainbow / gold / blue)
    /// </summary>
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
    /// 获取音频二进制流
    /// </summary>
    public static byte[]? GetAudioBytes(string key)
    {
        return _audioBytes.TryGetValue(key, out var bytes) ? bytes : null;
    }
}
