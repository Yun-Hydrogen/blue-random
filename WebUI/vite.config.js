import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import path from 'path'

const webUiRoot = __dirname
const configPanelRoot = path.resolve(webUiRoot, 'ConfigPanel')

export default defineConfig({
  root: configPanelRoot,
  base: './', // 生产构建使用相对路径，兼容 WebView2 本地文件加载
  publicDir: path.resolve(webUiRoot, '../Resources'),
  plugins: [
    vue()
  ],
  resolve: {
    // 渲染进程路径别名
    alias: {
      '@': configPanelRoot,
      'riz-ui': path.resolve(webUiRoot, 'node_modules/riz-ui/src/index.js')
    }
  },
  build: {
    outDir: path.resolve(webUiRoot, 'dist'),
    emptyOutDir: true,
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
  cacheDir: path.resolve(webUiRoot, 'node_modules/.vite')
})
