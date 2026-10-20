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
      '@': path.resolve(__dirname, 'src/renderer'),
      'riz-ui': path.resolve(__dirname, 'node_modules/riz-ui/src/index.js')
    }
  },
  build: {
    outDir: 'dist',
    minify: 'esbuild',
    target: 'chrome134',
    rollupOptions: {
      output: {
        manualChunks(id) {
          if (id.includes('node_modules/vue') || id.includes('node_modules/vue-router')) {
            return 'vendor-vue'
          }
          if (id.includes('node_modules/js-yaml')) {
            return 'vendor-yaml'
          }
          if (id.includes('node_modules/axios')) {
            return 'vendor-axios'
          }
          if (id.includes('rizui_mdeditor') || id.includes('rizui_code')) {
            return 'rizui-md'
          }
          if (id.includes('node_modules/riz-ui')) {
            return 'vendor-rizui'
          }
          if (id.includes('TabLogs.vue')) {
            return 'tab-logs'
          }
        }
      }
    }
  },
  server: {
    port: 5173
  },
  cacheDir: 'node_modules/.vite'
})
