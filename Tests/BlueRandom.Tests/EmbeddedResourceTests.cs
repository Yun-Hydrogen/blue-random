// ============================================================================
// 文件名称: EmbeddedResourceTests.cs
// 文件说明: 校验编译期内嵌资源命名与新目录结构保持一致。
// 核心职责: 资源名不匹配不会导致编译失败，只会在运行期静默丢失图片/字体/音效，
//           因此以单元测试形式固化 Resources.* 与 dist.* 的清单约定。
// ============================================================================

using System.Linq;
using System.Reflection;
using BlueRandom.Modules.ResourceProvider;
using Xunit;

namespace BlueRandom.Tests;

public class EmbeddedResourceTests
{
    /// <summary>
    /// 将清单中的资源名统一为点分隔形式，兼容 MSBuild 在 Windows 下生成的反斜杠分隔。
    /// </summary>
    private static string[] NormalizedResourceNames()
    {
        var asm = typeof(ResourceCache).Assembly;
        return asm.GetManifestResourceNames()
            .Select(name => name.Replace('\\', '.'))
            .ToArray();
    }

    [Theory]
    [InlineData("Resources.fonts.UI.ttf")]
    [InlineData("Resources.image.app.ico")]
    [InlineData("Resources.image.BlueRandom.png")]
    [InlineData("Resources.sound.button_click.wav")]
    [InlineData("dist.index.html")]
    [InlineData("package.json")]
    [InlineData("changelog.md")]
    public void ExpectedEmbeddedResource_IsPresent(string expectedName)
    {
        Assert.Contains(expectedName, NormalizedResourceNames());
    }

    /// <summary>
    /// 验证 WebUI 构建产物可在运行期从嵌入清单装配进资源缓存（单文件发布路径）。
    /// </summary>
    [Fact]
    public void WebUiAsset_IndexHtml_IsLoadableFromEmbeddedManifest()
    {
        Assert.NotNull(ResourceCache.GetWebUiAsset("index.html"));
    }
}
