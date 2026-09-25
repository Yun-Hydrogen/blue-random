import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import path from 'path'

export default defineConfig({
  base: './', // 生产构建使用相对路径，兼容 WebView2 本地文件加载
  plugins: [
    vue()
  ],
  resolve: {
    // 渲染进程路径别名
    alias: {
      '@': path.resolve(__dirname, 'src/renderer')
    }
  },
  build: {
    outDir: 'dist',
    minify: 'esbuild',
    target: 'chrome134'
  },
  server: {
    port: 5173
  },
  cacheDir: 'node_modules/.vite'
})
