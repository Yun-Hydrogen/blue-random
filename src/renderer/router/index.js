/*
================================================================================
  文件：src/renderer/router/index.js
  类型：Vue Router 路由配置
  所属：渲染进程的页面导航中枢

================================================================================
  一、什么是 Vue Router？
================================================================================

  Vue Router 是 Vue.js 的官方路由库。它的作用是：
    - 根据浏览器地址栏的 URL 决定显示哪个页面组件
    - 在 <router-view /> 占位符处渲染匹配的组件
    - 支持前进/后退导航（浏览器历史记录）

  在 Electron 环境中，路由的含义与 Web 略有不同：
    - 不是服务器端路由（没有 HTTP 请求）
    - 是客户端路由（纯 JS 切换组件，不刷新页面）
    - URL 只在渲染进程内部有效

================================================================================
  二、为什么使用 Hash 路由（createWebHashHistory）？
================================================================================

  Electron 通过 file:// 协议加载本地 HTML 文件。这意味着：
    - 没有 Web 服务器（没有 Nginx / Apache）
    - 不能使用 HTML5 History 模式（需要服务端 fallback 配置）
    - 刷新页面时，file:// 协议无法正确处理路径重写

  Hash 路由的原理：
    URL 中的 # 号及之后的内容不会发送到服务器（或文件系统）。
    例如：file:///.../index.html#/config-panel
          ~~~~~~~~文件路径~~~~~~~~  ~~~~~~~~~~ Hash 部分~~~~~~~~~~
    Electron 加载的始终是 index.html，# 后面的部分由 Vue Router 在
    浏览器端解析，决定渲染哪个组件。

  Hash 路由格式示例：
    实际 URL                           → 匹配路由   → 渲染组件
    ─────────────────────────────────────────────────────────────
    index.html#/                       → /          → Floating.vue
    index.html#/pick-result            → /pick-result → PickResult.vue
    index.html#/config-panel           → /config-panel → ConfigPanel.vue

================================================================================
  三、路由表（Routes）
================================================================================

  路径（path）           | 组件（component）      | 功能描述
  ───────────────────────┼────────────────────────┼──────────────────────────
  /                      | Floating.vue           | 悬浮按钮主视图
  /pick-result           | PickResult.vue         | 抽奖结果展示窗口
  /config-panel          | ConfigPanel.vue        | 配置面板（5 个标签页）

  注意：
    - 路由配置的顺序不敏感（Vue Router 按精确匹配而非注册顺序）
    - 没有定义 404 页面（所有路由都应通过 IPC 由主进程精确导航）
    - 新增路由需同时确保主进程中创建了对应的 BrowserWindow

================================================================================
  四、路由与窗口的关系
================================================================================

  本应用有多个 Electron BrowserWindow，每个窗口加载不同的路由：

    窗口名称          | 加载的路由      | 创建位置
    ──────────────────┼────────────────┼──────────────────────────
    悬浮按钮窗口       | /              | windows.js createFloatingButtonWindow()
    人数选择窗口       | （独立窗口）    | windows.js createPickerWindow()
    抽奖结果窗口       | /pick-result   | windows.js createPickResultWindow()
    配置面板窗口       | /config-panel  | windows.js createConfigPanelWindow()

  每个窗口都有自己独立的 Vue 应用实例和 Router 实例。
  它们之间不共享路由状态，切换窗口内的"页面"不是通过路由，
  而是主进程关闭/创建新的 BrowserWindow。

================================================================================
  五、维护注意事项
================================================================================

  - 新增页面组件时：
      1. 在此文件中 import + 添加 route
      2. 在主进程 windows.js 中创建对应的 BrowserWindow
      3. 在 preload.js 中添加对应的 IPC API（如有需要）
  - 删除页面时同步清理以上三处
  - 不要使用动态路由（如 /user/:id），当前应用没有此需求
  - createWebHashHistory() 不接受 base 参数（Hash 模式不需要）

  最后更新：2026-06-28
================================================================================
*/

/*
 * Vue Router 依赖导入
 *
 * createRouter            — 创建路由实例（整个应用只有一个）
 * createWebHashHistory    — Hash 模式的历史记录管理器
 *                           URL 格式：index.html#/some-path
 */
import { createRouter, createWebHashHistory } from 'vue-router'

/*
 * 页面组件导入（懒加载无必要——应用体积小，3 个组件首屏即用）
 *
 * Floating.vue    — 悬浮按钮视图（默认首页）
 * PickResult.vue  — 抽奖结果动画窗口
 * ConfigPanel.vue — 配置面板（名额管理 / 悬浮 / 结果 / 高级 / 日志）
 */
import Floating from '../views/Floating.vue'
import PickResult from '../views/PickResult.vue'
import ConfigPanel from '../views/ConfigPanel.vue'

/*
 * routes 数组 —— 路由映射表
 *
 * 每个 route 对象包含：
 *   path      — URL 路径（Hash 模式下是 # 后面的部分）
 *   component — 匹配成功时渲染的 Vue 组件
 *
 * / 路由（根路径）是悬浮按钮窗口的默认视图。
 * 它作为"首页"是因为 app 启动时首先创建的是悬浮按钮窗口。
 */
const routes = [
  { path: '/',             component: Floating },
  { path: '/pick-result',  component: PickResult },
  { path: '/config-panel', component: ConfigPanel }
]

/*
 * router 实例创建
 *
 * createRouter 接受一个配置对象：
 *   history — 历史记录模式（Hash 模式，见上文解释）
 *   routes  — 路由映射表
 *
 * 创建后通过 export default 导出，在 main.js 中 .use(router) 注册。
 */
const router = createRouter({
  history: createWebHashHistory(),
  routes
})

export default router
