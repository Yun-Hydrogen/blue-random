// ==============================================================================
// 文件: CustomIconLoader.cs
// 命名空间: BlueRandom.Modules.ResourceProvider
// 作用: 悬浮按钮图标资源加载与回退解析器。
//       支持从 CustomsStore 读取自定义用户图标解码为冻结的 WPF BitmapImage，
//       并提供向系统内置图标 (BlueRandom.png / app.ico) 的多级平滑回退。
// ==============================================================================

using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BlueRandom.Modules.ResourceProvider;

/// <summary>
/// 悬浮按钮图标加载与解析器。
/// </summary>
internal static class CustomIconLoader
{
    /// <summary>
    /// 从 CustomsStore 资产库中根据 ID 加载并解码自定义位图。
    /// </summary>
    /// <param name="customIconId">自定义图标资源唯一 ID。</param>
    /// <returns>成功解码并冻结的 ImageSource；失败或不存在时返回 null。</returns>
    public static ImageSource? LoadCustomIcon(string? customIconId)
    {
        if (string.IsNullOrEmpty(customIconId))
        {
            return null;
        }

        var custom = CustomsStore.LoadCustom(customIconId);
        if (custom != null)
        {
            byte[]? bytes = custom.Bytes;
            if (bytes != null && bytes.Length > 0)
            {
                try
                {
                    using var ms = new MemoryStream(bytes);
                    ms.Position = 0;
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("FCB", $"加载自定义图标解码异常: {ex.Message}");
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 依优先级解析悬浮按钮最终图标：优先加载用户自定义图标，缺失时自动回退为内置图标。
    /// </summary>
    /// <param name="customIconId">自定义图标资源唯一 ID。</param>
    /// <returns>已就绪的 ImageSource 实例。</returns>
    public static ImageSource? ResolveButtonIcon(string? customIconId)
    {
        var customIcon = LoadCustomIcon(customIconId);
        if (customIcon != null)
        {
            return customIcon;
        }

        return ResourceCache.GetImage("BlueRandom.png") ?? ResourceCache.GetImage("app.ico");
    }
}
