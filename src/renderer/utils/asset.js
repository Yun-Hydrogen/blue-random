/*
================================================================================
  工具：src/renderer/utils/asset.js
  职责：把 public 静态资源的路径解析为「当前协议下可用的完整 URL」。
  适用：/image/...、/sound/...、/fonts/... 等 public 目录资源。

  为什么需要它：
    开发模式经 http:// 加载，/image/x.png 解析到站点根目录，正常。
    打包后经 file:// 加载，/image/x.png 会解析到「盘符根目录」（如 C:/image/...），
    无法命中 dist/image/x.png，导致图片/音频在打包后消失。
    本函数在 file:// 下改用"当前页面所在目录"作为基准，拼接出正确路径。

  用法：
    import { resolveAssetUrl } from '@/utils/asset'
    <img :src="resolveAssetUrl('/image/Letter_Rainbow.png')" />
    fetch(resolveAssetUrl('/sound/button_click.wav'))

  说明：
    - 入参可带或不带前导斜杠，内部会统一去除。
    - 仅在渲染进程使用（依赖 window.location）。
================================================================================
*/
export function resolveAssetUrl(relativePath) {
  const base = window.location.protocol === 'file:'
    ? new URL('.', window.location.href).toString()
    : `${window.location.origin}/`
  return new URL(relativePath.replace(/^\/+/, ''), base).toString()
}
