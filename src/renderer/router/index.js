/*
================================================================================
  文件：src/renderer/router/index.js
  类型：Vue Router 路由配置
  所属：配置面板 WebUI 路由中枢 (承载于 WebView2)
================================================================================
*/
import { createRouter, createWebHashHistory } from 'vue-router'
import ConfigPanel from '../views/ConfigPanel.vue'

const routes = [
  { path: '/', component: ConfigPanel },
  { path: '/config-panel', component: ConfigPanel }
]

const router = createRouter({
  history: createWebHashHistory(),
  routes
})

export default router
