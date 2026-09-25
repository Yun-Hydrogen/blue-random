/*
技术文档：src/renderer/main.js
职责：Vue 3 配置面板前端入口。

核心功能：
- 初始化 WebView2 宿主桥接 (nativeBridgeAdapter)。
- 创建 Vue 应用并挂载路由。
- 劫持 console 输出并转发到宿主统一日志系统。
- 默认路由跳转到配置面板页面。
*/
import { createApp } from 'vue'
import App from './App.vue'
import router from './router'

import './style.css'
import '@fortawesome/fontawesome-free/css/all.min.css'

import { setupNativeBridgeAdapter } from './bridge/nativeBridgeAdapter'

// 初始化 WebView2 宿主桥接
setupNativeBridgeAdapter()

// 将渲染进程日志转发到主进程日志面板
const logToMain = (level, text) => {
  if (window.logApi && typeof window.logApi.send === 'function') {
    window.logApi.send(level, text)
  }
}

// 统一拦截 console：仅把 warn/error 转发到主进程日志（避免普通日志刷屏）
['log', 'info', 'warn', 'error'].forEach((method) => {
  const original = console[method].bind(console)
  console[method] = (...args) => {
    const text = args.map(arg => {
      if (typeof arg === 'string') return arg
      try {
        return JSON.stringify(arg)
      } catch (_error) {
        return String(arg)
      }
    }).join(' ')
    if (method === 'warn' || method === 'error') logToMain(method, text)
    original(...args)
  }
})

// 若未指定哈希路由，默认跳转到配置面板页面
if (window.location.hash === '') {
  router.push('/config-panel');
}

// 挂载应用
createApp(App).use(router).mount('#app')
