/*
技术文档：vite.config.js
职责：Vite + Electron 构建配置入口。

核心功能：
- 注册 Vue、Electron 主进程、预加载与渲染进程插件。
- 配置 @ 别名到 src/renderer。
*/
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import electron from 'vite-plugin-electron/simple'
import renderer from 'vite-plugin-electron-renderer'
import path from 'path'

export default defineConfig({
  base: './',  // 生产构建使用相对路径，兼容 Electron file:// 协议
  plugins: [
    vue(),
    electron({
      main: {
        entry: 'src/main/main.js',
        vite: {
          build: {
            rollupOptions: {
              /* koffi 为原生（N-API）FFI 模块，必须外部化，运行时从 node_modules 加载 */
              external: ['koffi', '@koromix/koffi-win32-x64']
            }
          }
        }
      },
      preload: {
        input: 'src/preload/preload.js',
      },
    }),
    renderer()
  ],
  resolve: {
    // 渲染进程路径别名
    alias: {
      '@': path.resolve(__dirname, 'src/renderer')
    }
  },
  build: {
    // esbuild 压缩比 terser 快 20-40 倍
    minify: 'esbuild',
    // 目标 Chrome 版本（Electron 37 基于 Chromium 134）
    target: 'chrome134',
    // 启用构建缓存（Vite 8 持久化缓存到 node_modules/.vite）
    // 首次构建后，未修改模块直接复用缓存
  },
  server: {
    port: 5173
  },
  // 持久化缓存目录
  cacheDir: 'node_modules/.vite',
})
