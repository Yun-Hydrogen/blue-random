/*
================================================================================
  文件：src/renderer/composables/useConfigPanel.js
  类型：Vue 3 Composable（组合式函数）
  所属：配置面板（ConfigPanel.vue）的全部 JavaScript 逻辑
  父组件：src/renderer/views/ConfigPanel.vue

================================================================================
  一、什么是 Composable？
================================================================================

  Vue 3 的 Composable 是一种"把逻辑从组件中抽离"的模式。你可以把它理解成：
    - 一个"工具箱函数"，调用它会返回一组变量和方法
    - ConfigPanel.vue 在 <script setup> 中调用 useConfigPanel()，
      拿到所有需要的状态和函数，模板直接使用
    - 好处：视图（.vue）只负责渲染，逻辑（.js）独立维护

  本文件导出了两样东西：
    export const tabs        — 标签页定义（静态数据，不依赖 Vue 实例）
    export function useConfigPanel()  — 组合式函数（返回所有响应式状态 + 方法）

================================================================================
  二、整体架构（数据流图）
================================================================================

  ┌──────────────────────────────────────────────────────────────────┐
  │  ConfigPanel.vue（视图层）                                        │
  │                                                                   │
  │  const { tabs, activeTab, draft, ... } = useConfigPanel()        │
  │                                                                   │
  │  模板使用：                                                       │
  │    v-for="tab in tabs"              → 渲染 5 个标签按钮          │
  │    :class="{ active: tab.id === activeTab }"  → 高亮当前标签     │
  │    draft.studentList                → 名单数据（双向绑定）        │
  │    draft.floatingButton.sizePercent → 悬浮按钮大小（双向绑定）    │
  │    @apply / @cancel                 → 保存 / 取消（调用 IPC）     │
  └──────────────┬───────────────────────────────────────────────────┘
                 │ 调用 useConfigPanel()
                 ▼
  ┌──────────────────────────────────────────────────────────────────┐
  │  useConfigPanel()（本文件 — 逻辑层）                              │
  │                                                                   │
  │  ┌─────────────────┐  ┌──────────────────┐  ┌─────────────────┐ │
  │  │ Tab 导航 & 滑块  │  │ 全局状态（draft） │  │ IPC 操作封装    │ │
  │  │                  │  │                  │  │                 │ │
  │  │ activeTab        │  │ draft.studentList│  │ fetchAppInfo()  │ │
  │  │ switchTab()      │  │ draft.admin      │  │ saveConfig()    │ │
  │  │ updateSlider()   │  │ draft.floating.. │  │ resetConfig()   │ │
  │  │ sliderStyle      │  │ draft.pickResult │  │ checkUpdate()   │ │
  │  └─────────────────┘  └──────────────────┘  └─────────────────┘ │
  │                                                                   │
  │  ┌─────────────────┐  ┌──────────────────┐  ┌─────────────────┐ │
  │  │ 主题色跟随       │  │ 关闭 & 应用      │  │ 生命周期        │ │
  │  │                  │  │                  │  │                 │ │
  │  │ panelBorderStyle │  │ handleApply()    │  │ onMounted()     │ │
  │  │ applyBtnStyle    │  │ handleCancel()   │  │  → 加载配置     │ │
  │  │ sliderStyle      │  │ closeWithAnim..  │  │  → 更新滑块     │ │
  │  └─────────────────┘  └──────────────────┘  └─────────────────┘ │
  └──────────────┬───────────────────────────────────────────────────┘
                 │ 通过 window.configPanelApi 通信
                 ▼
  ┌──────────────────────────────────────────────────────────────────┐
  │  主进程（Electron Main Process）                                  │
  │  config.js（配置读写）+ ipc.js（IPC 注册）+ admin.js（提权）     │
  └──────────────────────────────────────────────────────────────────┘

================================================================================
  三、IPC API 映射表（window.configPanelApi → 主进程 ipcMain.handle）
================================================================================

  Composable 方法              | preload API                    | IPC 通道
  ─────────────────────────────┼────────────────────────────────┼────────────────────────────
  fetchAppInfo()               | configPanelApi.getAppInfo()    | config-panel:get-app-info
  handleApply() → saveConfig() | configPanelApi.saveConfig()   | config-panel:save-config
  resetConfig()                | configPanelApi.resetConfig()   | config-panel:reset-config
  createStartupTask()          | configPanelApi.createStartup.. | config-panel:create-startup-task
  adminElevate()               | configPanelApi.adminElevate()  | config-panel:admin-elevate
  appRestart()                 | configPanelApi.restart()       | config-panel:restart
  checkUpdate()                | configPanelApi.checkUpdate()   | config-panel:check-update
  openConfigFile()             | configPanelApi.openConfigFile()| config-panel:open-config-file
  showInExplorer()             | configPanelApi.openConfigDir() | config-panel:open-config-dir
  closeWithAnimation()         | configPanelApi.close()         | config-panel:close（send）

  注：configPanelApi 由 src/preload/preload.js 通过 contextBridge 注入。

================================================================================
  四、draft 状态对象结构（与 config.yml / DEFAULT_CONFIG 一一对应）
================================================================================

  draft = {
    studentList:     Array<{name, weight}>  — 学生名单
    allowRepeatDraw: boolean                — 是否允许重复抽取
    floatingButton:  {                      — 悬浮按钮配置
      sizePercent, alwaysOnTop, position,
      iconDataUrl, iconSize, borderColor
    }
    pickCountDialog: { defaultCount }       — 人数选择弹窗配置
    pickResultDialog: {                     — 抽奖结果弹窗配置
      defaultPlayGachaSound, panelOpacity,
      panelBgColor, panelBorderColor,
      playMusic, soundVolume, musicVolume,
      bgmStartTime, bgmFadeDuration
    }
    admin: {                                — 高级设置（管理员权限等）
      adminAutoStartTaskName, adminAutoStartAdmin,
      requireAdminOnLaunch, uiAccessEnabled
    }
  }

================================================================================
  五、维护注意事项
================================================================================

  - 新增配置字段时，需在 3 处同步更新：
      1. src/main/config.js 的 DEFAULT_CONFIG + normalizeConfig
      2. 本文件的 draft 初始值
      3. 对应的子组件（TabRoster / TabFloating / TabResult / TabAdvanced）
  - draft 使用 reactive() 包裹，深层属性也是响应式的，可直接 draft.xxx.yyy = zzz
  - handleApply() 中 JSON.parse(JSON.stringify(draft)) 用于深拷贝，
    避免将 Vue 响应式代理对象传给主进程（主进程不认识 Proxy）
  - closeWithAnimation() 的 200ms 延迟用于让 CSS 关闭动画播放完毕
  - 标签页的 SVG icon 硬编码在 tabs 数组中（内联 SVG），不依赖外部图标库

================================================================================
  六、更新记录
================================================================================
  2026-08-28
    - 新增 saveVersion（ref）：每次“应用”保存成功后 +1，ConfigPanel 透传给
      TabRoster，TabRoster 据此自动重载班级列表（修复新建班级保存后切走再切回
      名单为空、需手动刷新的问题）
    - 新增 toast / showToast()：基于 rizui_toast 的全局通知（info/warn/error），
      由 ConfigPanel.vue 统一渲染
    - handleApply() 增加结果反馈：保存成功 → “配置已保存”；失败 → error toast
    - 新增 clearCache()：调用 config-panel:clear-cache 清理 Chromium 会话/缓存，
      结果通过 toast 反馈（高级设置 → 清理缓存）
  2026-08-29
    - draft.floatingButton 新增 customIconId（customs/ 资源引用），
      iconDataUrl 标记废弃（仅兼容旧配置读取）
    - draft.pickResultDialog 新增 bgmCustomId（背景音乐 customs 资源引用）
    - draft.pickResultDialog 新增 gachaSoundCustomId（抽取音效 customs 资源引用）

  最后更新：2026-08-29
================================================================================
*/

// ============================================================
//  Vue 3 依赖导入
//
//  ref()       — 创建单个响应式值（.value 读写）
//  reactive()  — 创建深层响应式对象（直接 .属性 读写）
//  computed()  — 创建计算属性（依赖变化时自动重新求值）
//  onMounted() — 组件挂载到 DOM 后执行的回调
//  nextTick()  — 等待下一次 DOM 更新完成（类似 await 版 $nextTick）
// ============================================================
import { ref, reactive, computed, onMounted, nextTick } from 'vue'

// ============================================================
//  第 1 部分：标签页（Tab）定义
//
//  这是静态数据，不依赖任何 Vue 实例，因此放在 useConfigPanel()
//  函数外部，使用 export const 导出。所有调用方共享同一份数据。
//
//  每个标签页包含：
//    id    — 唯一标识，对应 activeTab 的值
//    label — 中文显示名
//    color — 主题色（硬编码，用于高亮/边框/阴影等）
//    icon  — 内联 SVG 字符串（不依赖图标库，离线可用）
//
//  5 个标签页与对应的子组件：
//    roster   → TabRoster.vue   （名单管理）
//    floating → TabFloating.vue （悬浮按钮）
//    result   → TabResult.vue   （结果显示）
//    advanced → TabAdvanced.vue （高级设置）
//    about   → ConfigPanel.vue 内联（关于应用）
// ============================================================
export const tabs = [
  {
    id: 'roster',
    label: '名单管理',
    color: '#66ccff',
    /* SVG 图标：列表（三条横线 + 三个圆点） */
    icon: '<svg viewBox="0 0 24 24" class="tab-svg"><line x1="8" y1="6" x2="20" y2="6"/><line x1="8" y1="12" x2="20" y2="12"/><line x1="8" y1="18" x2="20" y2="18"/><circle cx="4" cy="6" r="1.5"/><circle cx="4" cy="12" r="1.5"/><circle cx="4" cy="18" r="1.5"/></svg>'
  },
  {
    id: 'floating',
    label: '悬浮按钮',
    color: '#39c5bb',
    icon: '<svg viewBox="0 0 24 24" class="tab-svg"><circle cx="12" cy="12" r="9"/><circle cx="12" cy="12" r="4"/><circle cx="12" cy="12" r="1.5"/></svg>'
  },
  {
    id: 'result',
    label: '结果浮窗',
    color: '#55cc99',
    icon: '<svg viewBox="0 0 24 24" class="tab-svg"><polygon points="12,2 15,9 22,9 16,14 18,21 12,17 6,21 8,14 2,9 9,9"/></svg>'
  },
  {
    id: 'advanced',
    label: '高级设置',
    color: '#aa88dd',
    icon: '<svg viewBox="0 0 24 24" class="tab-svg"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.68 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.68a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/></svg>'
  },
  {
    id: 'about',
    label: '关于应用',
    color: '#ff9966',
    /* SVG 图标：信息圆圈（i） */
    icon: '<svg viewBox="0 0 24 24" class="tab-svg"><circle cx="12" cy="12" r="10"/><line x1="12" y1="16" x2="12" y2="12"/><line x1="12" y1="8" x2="12.01" y2="8"/></svg>'
  }
]

// ============================================================================
//  useConfigPanel() —— 配置面板全部业务逻辑
//
//  这是本文件的唯一导出函数。ConfigPanel.vue 在 <script setup> 中调用它，
//  解构拿到所有需要的状态和方法。
//
//  返回值（约 30 个字段/方法）分 8 大类：
//    1. Tab 导航 & 滑块动画
//    2. 全局状态草稿（draft）+ 应用信息（appInfo）
//    3. 芯片 tooltip（名单管理 Tab 的学生悬浮提示）
//    4. IPC 操作封装（保存、重置、更新检查、提权等）
//    5. 关闭 & 应用（带动画延迟）
//    6. 主题色跟随（标签高亮色动态绑定到面板/按钮）
//    7. 加载状态 & 生命周期
//    8. 返回对象（集中导出，一目了然）
//
//  为什么使用函数而非直接写顶层代码：
//    Composable 模式确保每次调用都创建独立的状态副本。
//    虽然 ConfigPanel 只会被实例化一次，但这个模式让代码
//    更容易测试和复用。
// ============================================================================
export function useConfigPanel() {

  // ============================================================
  //  第 2 部分：Tab 导航 & 滑块动画
  //
  //  滑块（slider）是标签栏下方的高亮指示条，切换标签时平滑滑动。
  //
  //  核心变量：
  //    activeTab     — 当前选中的标签 ID（初始 'about'）
  //    tabBarRef     — 标签栏 DOM 元素引用（模板 ref）
  //    sliderLeft    — 滑块的水平偏移（px）
  //    sliderWidth   — 滑块的宽度（px，固定 80px）
  //    currentTab    — 当前标签的完整对象（含 color、label 等）
  //
  //  滑块定位算法（updateSlider）：
  //    1. 找到当前激活标签的 DOM 元素（.tab-item.active）
  //    2. 获取标签栏和激活标签的屏幕坐标（getBoundingClientRect）
  //    3. 计算偏移 = 激活标签中心 - 标签栏左边界 - 滑块半宽
  //    4. 如果标签宽度 > 滑块宽度，滑块居中于标签内
  // ============================================================
  const activeTab = ref('about')
  const tabBarRef = ref(null)
  const sliderLeft = ref(0)
  const sliderWidth = ref(0)
  const currentTab = computed(() => tabs.find(t => t.id === activeTab.value))

  /*
   * updateSlider() —— 重新计算滑块位置
   *
   * 调用时机：
   *   - 组件挂载后（onMounted）
   *   - 用户切换标签后（switchTab → nextTick → updateSlider）
   *
   * 原理：
   *   通过对比标签栏和激活标签的 DOM 矩形（getBoundingClientRect），
   *   算出滑块应该出现的位置。sliderWidth 固定 80px。
   *
   * 为什么用 getBoundingClientRect 而非 CSS transition：
   *   CSS transition 只能做固定起止点的动画，无法响应标签宽度变化。
   *   JavaScript 手动计算 + Vue 响应式绑定可以实现任意标签宽度下的精确对齐。
   */
  function updateSlider() {
    if (!tabBarRef.value) return
    const activeEl = tabBarRef.value.querySelector('.tab-item.active')
    if (!activeEl) return
    const barRect = tabBarRef.value.getBoundingClientRect()
    const elRect = activeEl.getBoundingClientRect()
    const sliderW = 80
    const elWidth = elRect.width
    const offset = elWidth > sliderW ? (elWidth - sliderW) / 2 : 0
    sliderLeft.value = elRect.left - barRect.left + offset
    sliderWidth.value = sliderW
  }

  /*
   * switchTab(id) —— 切换到指定标签页
   *
   * 执行流程：
   *   1. 更新 activeTab（Vue 响应式系统自动重渲染对应子组件）
   *   2. await nextTick() 等待 DOM 更新完成
   *   3. 调用 updateSlider() 重新定位滑块
   *
   * 为什么需要 nextTick：
   *   activeTab 改变后，Vue 不会立即更新 DOM，而是等到下一个"微任务"。
   *   如果不等 nextTick 就调 updateSlider，此时 DOM 中的 .active 类还没加上，
   *   querySelector('.tab-item.active') 会找不到元素。
   */
  async function switchTab(id) {
    activeTab.value = id
    await nextTick()
    updateSlider()
  }

  // ============================================================
  //  第 3 部分：全局状态 — 配置草稿（draft）+ 应用信息（appInfo）
  //
  //  draft 是配置面板的核心数据容器。用户在面板中做的所有修改
  //  都直接反映在 draft 中（双向绑定）。点击"应用"时，draft
  //  被深拷贝后通过 IPC 发送到主进程保存。
  //
  //  为什么用 reactive 而非 ref：
  //    draft 是一个深层嵌套对象。reactive 让所有嵌套属性都自动
  //    变成响应式的，不需要在模板中写 .value。
  //
  //  appInfo 存储主进程采集的系统信息（只读，展示用）。
  // ============================================================
  const draft = reactive({
    studentList: [],
    allowRepeatDraw: true,
    activeClassId: '',
    floatingButton: {
      sizePercent: 100,
      alwaysOnTop: true,
      showInTaskbar: false,
      position: { x: null, y: null },
      customIconId: '',       // 自定义图标引用（customs/ 目录下的资源 id）
      iconDataUrl: '',        // 已废弃（兼容旧配置读取）
      iconSize: 48,
      borderColor: '#ffffff'
    },
    pickCountDialog: { defaultCount: 1 },
    pickResultDialog: {
      defaultPlayGachaSound: true,
      gachaSoundCustomId: '', // 抽取音效资源引用（customs/ 目录，空=未上传音效）
      panelOpacity: 0.9,
      panelBgColor: '#ffffff',
      panelBorderColor: '#66ccff',
      showDeco: true,
      playMusic: false,
      bgmCustomId: '',       // BGM 资源引用（customs/ 目录，空=未上传背景音乐）
      soundVolume: 80,
      musicVolume: 60,
      bgmStartTime: 0,
      bgmFadeDuration: 1.5
    },
    admin: {
      adminAutoStartPath: '',
      adminAutoStartTaskName: 'Blue Random (Admin)',
      adminAutoStartAdmin: true,
      /* 启动时请求管理员权限；开启后 UIAccess 选项才可见 */
      requireAdminOnLaunch: false,
      uiAccessEnabled: false,
      renderingBackend: 'd3d9',
      disableDirectComposition: true,
      disableHardwareAcceleration: false
    }
  })

  /*
   * appInfo —— 主进程采集的系统/环境信息（只读）
   *
   * 字段说明：
   *   isAdmin           — 当前进程是否以管理员权限运行
   *   isUiAccess        — 当前进程是否通过 UIAccess 方式启动
   *   isWindows         — 操作系统是否为 Windows
   *   uiAccessDllExists — uiaccess.dll 是否存在于 exe 同目录
   *   configPath        — config.yml 的完整磁盘路径
   *   configDir         — config.yml 所在目录
   *   exePath           — 应用自身的 .exe 路径
   *   version           — 应用版本号（来自 package.json）
   */
  const appInfo = reactive({
    isAdmin: false,
    isUiAccess: false,
    isWindows: false,
    uiAccessDllExists: false,
    configPath: '',
    configDir: '',
    exePath: '',
    version: ''
  })

  // ============================================================
  //  第 4 部分：芯片 tooltip（全局 fixed 定位）
  //
  //  在"名单管理"Tab 中，学生以"芯片"（chip）形式展示。
  //  鼠标悬停时显示 tooltip，包含学生姓名和权重。
  //
  //  为什么用 fixed 定位而非 CSS absolute：
  //    tooltip 需要跟随鼠标且不被父容器裁剪（overflow: hidden）。
  //    fixed 相对于视口定位，不受任何父元素限制。
  //    JS 手动计算位置确保 tooltip 始终居中于芯片上方。
  // ============================================================
  const tooltip = reactive({ visible: false, text: '', x: 0, y: 0 })

  /*
   * showChipTooltip(e, s) —— 显示学生芯片的悬浮提示
   *
   * 参数：
   *   e — 鼠标事件对象（用于获取芯片元素的位置）
   *   s — 学生对象 { name: string, weight: number }
   *
   * 流程：
   *   1. 从事件对象获取芯片元素的屏幕坐标
   *   2. 设置 tooltip 文字（"张三 · 权重 1.5"）
   *   3. 先设置 visible = true, x = 0, y = 0（触发 DOM 创建）
   *   4. nextTick 后获取 tooltip 自身宽度，重新计算居中位置
   *
   * 为什么 x/y 先设为 0 再修正：
   *   在 tooltip 元素渲染到 DOM 之前，无法获取它的 offsetWidth。
   *   需要先让它出现在 DOM 中（visible=true），nextTick 后再读取宽度并定位。
   */
  function showChipTooltip(e, s) {
    const rect = e.target.getBoundingClientRect()
    tooltip.text = s.name + ' · 权重 ' + s.weight.toFixed(1)
    tooltip.visible = true
    tooltip.x = 0
    tooltip.y = 0
    nextTick(() => {
      const el = document.querySelector('.chip-tooltip')
      const tw = el ? el.offsetWidth : 80
      tooltip.x = rect.left + rect.width / 2 - tw / 2
      tooltip.y = rect.bottom + 6
    })
  }

  /* hideChipTooltip() —— 隐藏芯片 tooltip（鼠标移出时调用） */
  function hideChipTooltip() {
    tooltip.visible = false
  }

  // ============================================================
  //  第 5 部分：Tab 4 高级设置 — IPC 操作封装
  //
  //  这些函数封装了渲染进程 → 主进程的 IPC 调用。
  //  所有调用都通过 window.configPanelApi 进行，
  //  configPanelApi 由 preload.js 通过 contextBridge 注入。
  //
  //  安全设计：
  //    渲染进程不能直接访问 Node.js API（fs、child_process 等），
  //    必须通过 preload 暴露的有限接口与主进程通信。
  //    这保证了即使渲染进程被 XSS 攻击，也无法直接操作系统。
  // ============================================================

  /*
   * fetchAppInfo() —— 从主进程获取系统信息
   *
   * 调用时机：onMounted 中与配置加载并行执行
   *
   * Object.assign 的作用：
   *   将主进程返回的字段合并到已有的 reactive appInfo 对象中。
   *   这样不需要替换整个对象，已有的引用保持有效。
   */
  async function fetchAppInfo() {
    if (window.configPanelApi?.getAppInfo) {
      Object.assign(appInfo, await window.configPanelApi.getAppInfo() || {})
    }
  }

  /* openConfigFile() —— 用系统默认编辑器打开 config.yml */
  async function openConfigFile() {
    await window.configPanelApi?.openConfigFile()
  }

  /* openConfigDir() —— 在资源管理器中打开配置目录 */
  async function openConfigDir() {
    await window.configPanelApi?.openConfigDir()
  }

  /*
   * clearCache() —— 清理 Chromium 会话/缓存数据（userData/session 目录）
   *
   * 调用主进程 config-panel:clear-cache，删除 sessionData 下的缓存
   * （Cache、Code Cache、GPUCache、Local Storage、Network 等）。
   * 缓存均为可丢弃数据，删除不影响任何配置；结果通过 toast 反馈。
   */
  async function clearCache() {
    try {
      const r = await window.configPanelApi?.clearCache?.()
      if (r && r.ok) {
        showToast('info', '缓存已清理', r.message || '')
      } else {
        showToast('error', '清理缓存失败', (r && r.message) || '未知错误')
      }
    } catch (error) {
      console.error('清理缓存失败', error)
      showToast('error', '清理缓存失败', String((error && error.message) || error))
    }
  }

  /* adminElevate() —— 请求以管理员权限重启应用 */
  async function adminElevate() {
    await window.configPanelApi?.adminElevate()
  }

  /* appRestart() —— 普通重启应用 */
  async function appRestart() {
    await window.configPanelApi?.restart()
  }

  /*
   * createStartupTask() —— 创建/更新 Windows 计划任务
   *
   * 从 draft.admin 中读取计划任务的配置（路径、名称、是否管理员运行），
   * 通过 IPC 发送给主进程，由 admin.js 调用 schtasks 命令执行。
   *
   * exePath 的 fallback 逻辑：
   *   如果用户没有手动指定路径（adminAutoStartPath 为空），
   *   使用 appInfo.exePath（应用自身的路径）。
   */
  async function createStartupTask() {
    return await window.configPanelApi?.createStartupTask({
      exePath: draft.admin.adminAutoStartPath || appInfo.exePath,
      taskName: draft.admin.adminAutoStartTaskName,
      admin: draft.admin.adminAutoStartAdmin
    })
  }

  /*
   * resetConfig() —— 重置整个配置目录为默认值
   *
   * 流程：
   *   1. 调用主进程 resetConfig（security.resetAllConfig：清空密码/班级/自定义资源 +
   *      写入默认 config.yml）
   *   2. 调用主进程 getConfig（读取刚写入的默认配置）
   *   3. JSON.parse(JSON.stringify(cfg)) 深拷贝后填充到 draft
   *      （深拷贝是为了剥离 Vue 响应式代理，避免嵌套 Proxy 问题）
   *   4. showToast 提示已清除哪些内容
   */
  async function resetConfig() {
    if (window.configPanelApi) {
      const r = await window.configPanelApi.resetConfig()
      const cfg = await window.configPanelApi.getConfig()
      if (cfg) Object.assign(draft, JSON.parse(JSON.stringify(cfg)))
      if (r?.ok) showToast('info', (r && r.message) || '已重置所有配置')
    }
  }

  /* showInExplorer() —— 在资源管理器中打开配置目录 */
  function showInExplorer() {
    if (window.configPanelApi) window.configPanelApi.openConfigDir?.()
  }

  /*
   * fetchLogs(maxLines) —— 从主进程拉取磁盘日志文件内容。
   *
   * 这是 TabLogs 组件的核心数据源。替代了旧的 SSE 方案。
   *
   * 参数：
   *   maxLines — 最多返回的行数（可选，默认 500）
   *
   * 返回值：Promise<Array<logEntry>>
   *   日志条目数组，按时间倒序（最新的在前）。
   *   如果 IPC 不可用（非 Electron 环境），返回空数组。
   *
   * 竞态保护：
   *   主进程端（logging.js）在读取前会等待所有待处理写入完成，
   *   确保读到的内容包含之前所有 pushLog 的数据。
   */
  async function fetchLogs(maxLines) {
    if (!window.configPanelApi?.getLogs) return []
    try {
      return await window.configPanelApi.getLogs(maxLines)
    } catch {
      return []
    }
  }

  // ---- 更新检查状态 ----
  /*
   * 更新检查相关状态：
   *   updateLoading — 是否正在请求 GitHub API
   *   updateStatus  — 'update'（有新版本）/ 'error'（出错）/ ''（无结果）
   *   updateTitle   — 更新标题（如 "发现新版本 v2.0.0"）
   *   updateDetail  — 更新详情（版本说明、下载链接等）
   *   updateReleaseUrl — GitHub Releases 页面链接（无论是否有更新都可跳转）
   */
  const updateLoading = ref(false)
  const updateStatus = ref('')
  const updateTitle = ref('')
  const updateDetail = ref('')
  const updateReleaseUrl = ref('')

  /*
   * checkUpdate() —— 检查 GitHub Releases 是否有新版本
   *
   * 流程：
   *   1. 设置 loading = true，清空之前的结果
   *   2. 调用主进程 checkUpdate（IPC → update.js → GitHub API）
   *   3. 主进程返回 { status, title, detail, releaseUrl }
   *   4. 更新对应的 ref，loading = false
   */
  async function checkUpdate() {
    updateLoading.value = true
    updateStatus.value = ''
    updateTitle.value = ''
    updateDetail.value = ''
    updateReleaseUrl.value = ''
    const r = await window.configPanelApi?.checkUpdate()
    updateLoading.value = false
    if (r) {
      updateStatus.value = r.status || ''
      updateTitle.value = r.title || ''
      updateDetail.value = r.detail || ''
      /* 兜底：未返回 releaseUrl 时仍给出 Releases 最新页（“无论是否有更新均可跳转”） */
      updateReleaseUrl.value = r.releaseUrl || 'https://github.com/Yun-Hydrogen/blue-random/releases/latest'
    }
  }

  // ============================================================
  //  第 6 部分：关闭 & 应用
  //
  //  配置面板的关闭有两种情况：
  //    1. 点击"应用" → 先保存配置，再关闭（saved = true）
  //    2. 点击"取消" / 按 Esc  → 直接关闭（saved = false）
  //
  //  关闭动画：
  //    closeWithAnimation 先设置 isClosing = true，
  //    触发 CSS 退出动画（如 fadeOut、scaleDown），
  //    延迟 200ms 后实际关闭窗口，确保动画播放完毕。
  //    isClosing 标志防止重复点击"应用/取消"导致多次关闭。
  // ============================================================
  const isClosing = ref(false)

  /*
   * closeWithAnimation(saved) —— 带退出动画的关闭
   *
   * 参数：
   *   saved — 是否已保存配置（true = 应用, false = 取消）
   *
   * 防重复机制：
   *   如果 isClosing 已经为 true（动画正在播放中），
   *   直接 return 忽略后续点击。
   */
  function closeWithAnimation(saved) {
    if (isClosing.value) return
    isClosing.value = true
    setTimeout(() => {
      if (window.configPanelApi) window.configPanelApi.close(saved)
    }, 200)
  }

  /* handleCancel() —— 取消按钮：不保存，直接关闭 */
  function handleCancel() {
    closeWithAnimation(false)
  }

  /* 保存版本号：每次"应用"保存成功后 +1，用于通知 TabRoster 刷新班级列表 */
  const saveVersion = ref(0)

  /* 全局 toast 通知（rizui_toast：挂载即显示、倒计时自动关闭） */
  const toast = reactive({ show: false, type: 'info', title: '', hint: '' })
  function showToast(type, title, hint = '') {
    toast.type = type
    toast.title = title
    toast.hint = hint
    toast.show = true
  }

  /*
   * handleApply() —— 应用按钮：保存配置（不关闭窗口）
   *
   * 保存成功后：
   *   1. saveVersion++ —— TabRoster 监听到后重新加载班级列表，
   *      解决"新班级保存后切走再切回名单为空（需手动刷新）"的问题
   *   2. toast 通知"配置已保存"
   */
  async function handleApply() {
    const payload = JSON.parse(JSON.stringify(draft))
    try {
      if (window.configPanelApi) {
        const r = await window.configPanelApi.saveConfig(payload)
        if (r && r.ok) {
          saveVersion.value++
          showToast('info', '配置已保存','更改的配置已写入配置文件')
        } else {
          showToast('error', '保存失败', (r && r.message) || '未知错误')
        }
      }
    } catch (error) {
      console.error('保存配置失败', error)
      showToast('error', '保存失败', String((error && error.message) || error))
    }
  }

  // ============================================================
  //  第 7 部分：主题色跟随
  //
  //  面板边框、应用按钮、标签滑块的颜色跟随当前激活标签的主题色。
  //  使用 Vue computed 属性，当前标签切换时自动更新。
  //
  //  设计理念：
  //    每个标签页有不同的主题色（名单蓝、悬浮青、结果绿、高级紫、日志灰）。
  //    面板的视觉装饰（边框、按钮、滑块）应该与当前页面的主题色一致，
  //    给用户清晰的视觉反馈——"我现在在哪个页面"。
  // ============================================================

  /* 面板外层边框颜色 = 当前标签主题色，fallback #66ccff */
  const panelBorderStyle = computed(() => ({
    borderColor: currentTab.value?.color || '#66ccff'
  }))

  /* "应用"按钮背景色 = 当前标签主题色 */
  const applyBtnStyle = computed(() => ({
    background: currentTab.value?.color || '#66ccff'
  }))

  /* 标签栏滑块样式（left, width, background 三项） */
  const sliderStyle = computed(() => ({
    left: `${sliderLeft.value}px`,
    width: `${sliderWidth.value}px`,
    background: currentTab.value?.color || '#66ccff'
  }))

  /* 颜色选择器等控件的边框颜色跟随主题 */
  const trackBorderStyle = computed(() => ({
    borderColor: currentTab.value?.color || '#66ccff'
  }))

  /*
   * tabItemStyle(tab) —— 单个标签按钮的内联样式
   *
   * 当前激活的标签文字为白色，未激活的标签文字为自身主题色。
   * 这样激活标签在彩色背景（滑块 + 渐变遮罩）下清晰可读。
   */
  function tabItemStyle(tab) {
    return {
      color: activeTab.value === tab.id ? '#fff' : tab.color
    }
  }

  // ============================================================
  //  第 8 部分：加载状态 & 生命周期
  //
  //  onMounted 是组件挂载完成后执行的初始化逻辑。
  //  这里做了两件事：
  //    1. 立即播放滑块动画（不等数据，让 UI 快速响应）
  //    2. 从主进程加载配置数据和应用信息
  //
  //  加载策略：
  //    - 滑块动画和数据加载是并行的（Promise.all）
  //    - 这样即使用户的 config.yml 很大，标签栏也不会"卡住"
  //    - loading 标志控制骨架屏/加载动画的显示
  // ============================================================
  const loading = ref(true)

  // ============================================================
  //  密码保护（安全管理）：启用时打开配置面板需先解锁
  //
  //  流程：
  //    onMounted → refreshSecurity() 查询是否已开启
  //      未开启 → 直接加载配置（loadInitialData）
  //      已开启 → security.enabled=true, unlocked=false → 显示锁屏遮罩
  //              用户输入密码 → unlock() 验证 → 加载配置
  //              忘记密码 → forgetReset() 清除所有配置并解锁
  // ============================================================
  const security = reactive({ enabled: false, unlocked: false, busy: false, error: '' })

  /* 查询主进程：是否已开启密码保护 */
  async function refreshSecurity() {
    try {
      const r = await window.configPanelApi?.getSecurityStatus?.()
      security.enabled = Boolean(r?.enabled)
      security.unlocked = !security.enabled
    } catch (_) {
      security.enabled = false
      security.unlocked = true
    }
  }

  /* 锁屏解锁：验证密码成功后加载配置 */
  async function unlock(password) {
    if (security.busy) return
    security.busy = true
    security.error = ''
    try {
      const r = await window.configPanelApi?.verifyPassword?.(password)
      if (r?.ok) {
        security.unlocked = true
        await loadInitialData()
      } else {
        security.error = r?.message || '密码错误'
      }
    } catch (error) {
      security.error = String((error && error.message) || '解锁失败')
    } finally {
      security.busy = false
    }
  }

  /* 忘记密码：清除所有配置并解锁 */
  async function forgetReset() {
    if (security.busy) return
    security.busy = true
    security.error = ''
    try {
      const r = await window.configPanelApi?.resetAllConfig?.()
      if (r?.ok) {
        security.enabled = false
        security.unlocked = true
        await loadInitialData()
        showToast('info', r.message || '已清除所有配置')
      } else {
        security.error = (r && r.message) || '清除失败'
      }
    } catch (error) {
      security.error = String((error && error.message) || '清除失败')
    } finally {
      security.busy = false
    }
  }

  /* 加载配置与应用信息（解锁后 / 未启用保护时调用） */
  async function loadInitialData() {
    const [cfg] = await Promise.all([
      window.configPanelApi
        ? window.configPanelApi.getConfig().catch(() => null)
        : null,
      fetchAppInfo()
    ])
    if (cfg) Object.assign(draft, JSON.parse(JSON.stringify(cfg)))
    loading.value = false
  }

  onMounted(async () => {
    /*
     * 步骤 ①：立即播放滑块动画
     *
     * 不等待任何数据（配置、应用信息），先让 UI 活起来。
     * 这给用户"应用已经就绪"的心理暗示，不依赖网络或磁盘 I/O。
     */
    await nextTick()
    updateSlider()

    /*
     * 步骤 ②：安全门禁
     * 启用密码保护时先锁定（显示遮罩），解锁后再加载配置。
     * 未启用则直接加载。
     */
    await refreshSecurity()
    if (security.unlocked) {
      loadInitialData()
    }
  })

  // ============================================================
  //  第 9 部分：返回对象 — 所有供 ConfigPanel.vue 模板使用的内容
  //
  //  返回的是一个普通对象（非响应式），但内部的值（ref、reactive、
  //  computed）都是响应式的。Vue 模板可以直接使用。
  //
  //  分类：
  //    标签导航    → tabs, activeTab, switchTab, updateSlider, sliderStyle ...
  //    状态数据    → draft, appInfo, loading
  //    芯片提示    → tooltip, showChipTooltip, hideChipTooltip
  //    IPC 操作    → 10 个 async 函数（含 fetchLogs）
  //    关闭/应用   → isClosing, closeWithAnimation, handleCancel, handleApply
  //    主题样式    → panelBorderStyle, applyBtnStyle, trackBorderStyle, tabItemStyle
  //    更新检查    → updateLoading, updateStatus, updateTitle, updateDetail, updateReleaseUrl, checkUpdate
  // ============================================================
  return {
    /* ---- 标签导航 ---- */
    tabs,
    activeTab,
    tabBarRef,
    sliderLeft,
    sliderWidth,
    currentTab,
    updateSlider,
    switchTab,

    /* ---- 状态数据 ---- */
    draft,
    appInfo,

    /* ---- 芯片 tooltip ---- */
    tooltip,
    showChipTooltip,
    hideChipTooltip,

    /* ---- IPC 操作（高级设置 Tab 使用）---- */
    fetchAppInfo,
    openConfigFile,
    openConfigDir,
    clearCache,
    adminElevate,
    appRestart,
    createStartupTask,
    resetConfig,
    showInExplorer,
    fetchLogs,

    /* ---- 更新检查 ---- */
    updateLoading,
    updateStatus,
    updateTitle,
    updateDetail,
    updateReleaseUrl,
    checkUpdate,

    /* ---- 关闭 & 应用 ---- */
    isClosing,
    closeWithAnimation,
    handleCancel,
    handleApply,
    saveVersion,

    /* ---- 全局 toast 通知 ---- */
    toast,
    showToast,

    /* ---- 加载状态 ---- */
    loading,

    /* ---- 密码保护（安全管理） ---- */
    security,
    refreshSecurity,
    unlock,
    forgetReset,

    /* ---- 主题色跟随 ---- */
    panelBorderStyle,
    applyBtnStyle,
    sliderStyle,
    trackBorderStyle,
    tabItemStyle
  }
}

