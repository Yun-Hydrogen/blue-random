/*
================================================================================
  文件：src/preload/preload.js
  类型：Electron Preload Script（预加载脚本）
  所属：Electron 主进程与渲染进程之间的安全桥接层

================================================================================
  一、什么是 Preload Script？
================================================================================

  在 Electron 应用中，渲染进程（网页）默认不能访问 Node.js API
  （如 fs、child_process、require），这是出于安全考虑——
  如果网页被 XSS 攻击，攻击者无法直接操作操作系统。

  Preload Script 是唯一的例外：它在渲染进程加载之前运行，
  拥有 Node.js 完整权限，并可以通过 contextBridge 向渲染进程
  "暴露"有限、可控的 API。

  安全边界示意：

  ┌─────────────────────────────────────────────────────────────────┐
  │  渲染进程（Renderer Process）                                   │
  │  ┌─────────────────────────────────────────────────────────┐    │
  │  │  Vue 应用（网页）                                       │    │
  │  │                                                         │    │
  │  │  ✅ 可以调用 window.floatingButtonApi.getConfig()       │    │
  │  │  ✅ 可以调用 window.configPanelApi.saveConfig(data)     │    │
  │  │  ❌ 不能调用 require('fs')                              │    │
  │  │  ❌ 不能调用 require('child_process')                   │    │
  │  │  ❌ 不能直接访问 ipcRenderer                            │    │
  │  └─────────────────────────────────────────────────────────┘    │
  │                          ▲                                      │
  │                          │ contextBridge.exposeInMainWorld()    │
  │                          │ （安全桥接——只暴露指定方法）         │
  └──────────────────────────┼──────────────────────────────────────┘
                             │
  ┌──────────────────────────┼──────────────────────────────────────┐
  │  Preload Script（本文件）  │                                    │
  │                          ▼                                      │
  │  ┌─────────────────────────────────────────────────────────┐    │
  │  │const { contextBridge, ipcRenderer } = require('electron')│   │
  │  │                                                          │   │
  │  │  contextBridge.exposeInMainWorld('floatingButtonApi', {  │   │
  │  │    getConfig: () => ipcRenderer.invoke('floating-button: │   │
  │  │    ...                                                   │    │
  │  │  })                                                      │   │
  │  └─────────────────────────────────────────────────────────┘    │
  └──────────────────────────┬──────────────────────────────────────┘
                             │ ipcRenderer.invoke / ipcRenderer.send
                             │ （进程间通信）
                             ▼
  ┌─────────────────────────────────────────────────────────────────┐
  │  主进程（Main Process）                                         │
  │  ipcMain.handle('config-panel:save-config', ...)                │
  │  ipcMain.on('floating-button:drag-start', ...)                  │
  └─────────────────────────────────────────────────────────────────┘

================================================================================
  二、IPC 通信的两种模式
================================================================================

  本文件使用了两种 IPC 模式，选择取决于"是否需要返回值"：

  ┌──────────────────┬─────────────────────┬──────────────────────────┐
  │ 模式             │ 使用场景             │ 技术实现                  │
  ├──────────────────┼─────────────────────┼──────────────────────────┤
  │ invoke（请求响应）│ 读取配置、保存配置   │ ipcRenderer.invoke()     │
  │                  │ 获取系统信息等       │ + ipcMain.handle()       │
  │                  │ 需要等待返回值       │ 返回 Promise             │
  ├──────────────────┼─────────────────────┼──────────────────────────┤
  │ send（单向通知）  │ 拖拽事件、关闭窗口   │ ipcRenderer.send()       │
  │                  │ 日志上报等           │ + ipcMain.on()           │
  │                  │ 不需要返回值         │ 纯通知，无回调            │
  ├──────────────────┼─────────────────────┼──────────────────────────┤
  │ on + removeListener│ 主进程推送事件     │ ipcRenderer.on()         │
  │ （事件订阅）      │ 如 pick-result:open │ + 返回取消订阅函数        │
  │                  │ 需要长期监听         │ 防止内存泄漏              │
  └──────────────────┴─────────────────────┴──────────────────────────┘

================================================================================
  三、5 个 contextBridge API 总览
================================================================================

  window 全局变量          | 用途                       | 渠道数
  ─────────────────────────┼────────────────────────────┼──────
  floatingButtonApi        | 悬浮按钮窗口的交互          | 8
  floatingPickerApi        | 环绕人数选择器的交互        | 2
  pickResultApi            | 抽奖结果窗口的交互           | 5
  logApi                   | 渲染进程日志上报             | 1
  configPanelApi           | 配置面板的所有 IPC 操作     | 12
                           │                            │
                           合计                          │ 27 个 IPC 通道

================================================================================
  四、完整的 IPC 通道清单（26 条）
================================================================================

  #   | 通道名称                          | 方向       | 模式    | 对应 API
  ────┼───────────────────────────────────┼────────────┼─────────┼──────────────────────
  1   | floating-button:get-config        | Render→Main | invoke | floatingButtonApi.getConfig()
  2   | floating-button:drag-start        | Render→Main | send   | floatingButtonApi.startDrag()
  3   | floating-button:drag-move         | Render→Main | send   | floatingButtonApi.moveDrag()
  4   | floating-button:drag-end          | Render→Main | send   | floatingButtonApi.endDrag()
  5   | floating-button:set-ignore-mouse  | Render→Main | send   | floatingButtonApi.setIgnoreMouseEvents()
  6   | floating-button:set-expanded      | Render→Main | send   | floatingButtonApi.setExpanded()
  7   | floating-button:set-shape         | Render→Main | send   | floatingButtonApi.setShape()
  8   | floating-picker:get-config        | Render→Main | invoke | floatingPickerApi.getConfig()
  9   | floating-picker:confirm           | Render→Main | send   | floatingPickerApi.confirm()
  10  | pick-result:get-results           | Render→Main | invoke | pickResultApi.getResults()
  11  | pick-result:get-config            | Render→Main | invoke | pickResultApi.getConfig()
  12  | pick-result:close                 | Render→Main | send   | pickResultApi.close()
  13  | pick-result:open                  | Main→Render | on     | pickResultApi.onOpen()
  14  | pick-result:reset                 | Main→Render | on     | pickResultApi.onReset()
  15  | renderer:log                      | Render→Main | send   | logApi.send()
  15  | config-panel:get-config           | Render→Main | invoke | configPanelApi.getConfig()
  16  | config-panel:save-config          | Render→Main | invoke | configPanelApi.saveConfig()
  17  | config-panel:close                | Render→Main | send   | configPanelApi.close()
  18  | config-panel:get-app-info         | Render→Main | invoke | configPanelApi.getAppInfo()
  19  | config-panel:admin-elevate        | Render→Main | invoke | configPanelApi.adminElevate()
  20  | config-panel:restart              | Render→Main | invoke | configPanelApi.restart()
  21  | config-panel:create-startup-task  | Render→Main | invoke | configPanelApi.createStartupTask()
  22  | config-panel:open-config-file     | Render→Main | invoke | configPanelApi.openConfigFile()
  23  | config-panel:open-config-dir      | Render→Main | invoke | configPanelApi.openConfigDir()
  24  | config-panel:check-update         | Render→Main | invoke | configPanelApi.checkUpdate()
  25  | config-panel:pick-exe-file        | Render→Main | invoke | configPanelApi.pickExeFile()
  26  | config-panel:reset-config         | Render→Main | invoke | configPanelApi.resetConfig()
  27  | floating-button:css-fade-out      | Main→Render | on     | floatingButtonApi.onCssFadeOut()
  28  | floating-button:css-fade-in       | Main→Render | on     | floatingButtonApi.onCssFadeIn()

================================================================================
  五、on* 监听函数的"取消订阅"模式
================================================================================

  pickResultApi 的 onOpen 和 onReset 使用了特殊的返回模式：

    onOpen: (callback) => {
      const listener = (_event, payload) => callback(payload)
      ipcRenderer.on('pick-result:open', listener)
      return () => {
        ipcRenderer.removeListener('pick-result:open', listener)
      }
    }

  为什么返回取消订阅函数：
    - 如果只 on 不移除，窗口关闭再打开时，旧的 listener 仍在内存中
    - 每次打开新窗口都会叠加新的 listener，导致重复回调
    - 返回取消函数让调用方在组件卸载时清理（Vue onUnmounted 中调用）

  调用方使用示例（PickResult.vue 中）：
    let unsubscribe
    onMounted(() => {
      unsubscribe = window.pickResultApi.onOpen((data) => { ... })
    })
    onUnmounted(() => {
      unsubscribe?.()  // ← 这里移除监听，防止内存泄漏
    })

================================================================================
  六、维护注意事项
================================================================================

  - 新增 IPC 通道的完整步骤：
      1. 在本文件的对应 API 组中添加方法
      2. 在 src/main/ipc.js 中注册 ipcMain.handle 或 ipcMain.on
      3. 更新本文档头部的 IPC 通道清单表
      4. 确保命名遵循 模块名:动作 约定
  - 不要在此暴露 Node 原生模块（如直接暴露 fs、child_process）
  - 所有 invoke 方法返回 Promise，调用方需要 await
  - contextBridge 只支持暴露纯对象（不能暴露函数直接作为 window 属性）
  - 不要使用 ... 展开运算符传递整个 ipcRenderer（会破坏安全边界）

================================================================================
更新记录
================================================================================
  2026-08-28  新增 configPanelApi.clearCache()：
              - 调用 config-panel:clear-cache，清理 Chromium 会话/缓存数据
  2026-08-29  新增 configPanelApi.saveCustom() / getCustom() / deleteCustom()：
              - 自定义资源（customs/ 目录）的保存、读取、删除
  2026-08-29  新增 configPanelApi.saveCustomStaged()：
              - 上传资源先暂存主进程内存（不落盘），点击“应用”才写入 customs/，
                未应用关闭面板即舍弃
  2026-08-29  新增 configPanelApi 安全相关 API（密码保护）：
              - getSecurityStatus() / verifyPassword() / setPassword() /
                disablePassword() / resetAllConfig()（参考 src/main/security.js）

  最后更新：2026-08-29
================================================================================
*/

/*
 * Electron 依赖
 *
 * contextBridge — 安全地将 API 从 Node.js 环境桥接到网页环境
 *                  使用方法：contextBridge.exposeInMainWorld('api名', { 方法对象 })
 *                  暴露后的 API 在网页中通过 window.api名 访问
 *
 * ipcRenderer   — 渲染进程的 IPC 客户端
 *                  .invoke(channel, ...args) → Promise<返回值>
 *                  .send(channel, ...args)    → 无返回值（单向通知）
 *                  .on(channel, listener)     → 监听主进程推送
 *                  .removeListener(channel, listener) → 取消监听
 */
const { contextBridge, ipcRenderer } = require('electron')

// ============================================================================
//  悬浮按钮窗口桥接 API — window.floatingButtonApi
//
//  使用方：Floating.vue（悬浮按钮自身的 Vue 组件）
//
//  功能：
//    - 获取配置（按钮大小、透明度、位置等）
//    - 拖拽支持（三阶段：start → move → end）
//    - 鼠标穿透控制（展开/收起时切换 ignore 状态）
//    - 按钮展开/收起状态切换
// ============================================================================
contextBridge.exposeInMainWorld('floatingButtonApi', {

  /* 获取悬浮按钮配置（尺寸、透明度、置顶状态、位置等） */
  getConfig: () => ipcRenderer.invoke('floating-button:get-config'),

  /* 拖拽开始：通知主进程准备响应鼠标移动事件 */
  startDrag: () => ipcRenderer.send('floating-button:drag-start'),

  /* 拖拽移动：dx/dy 是相对上一帧的鼠标偏移量（像素） */
  moveDrag: (dx, dy) => ipcRenderer.send('floating-button:drag-move', { dx, dy }),

  /* 拖拽结束：通知主进程停止移动窗口 */
  endDrag: () => ipcRenderer.send('floating-button:drag-end'),

  /*
   * 鼠标穿透切换
   *
   * 原理：
   *   Electron 的 setIgnoreMouseEvents(true) 让窗口对鼠标"透明"——
   *   点击会穿透到下方窗口。这在悬浮按钮缩小时很有用（不阻挡操作）。
   *   展开时需要设为 false，否则按钮本身也无法点击。
   */
  setIgnoreMouseEvents: (ignore) =>
    ipcRenderer.send('floating-button:set-ignore-mouse', ignore),

  /*
   * 展开/收起状态切换
   *
   * expanded — true 表示按钮已展开（显示环形菜单）
   * size     — 展开后的窗口尺寸（像素）
   */
  setExpanded: (expanded, size) =>
    ipcRenderer.send('floating-button:set-expanded', { expanded, size }),

  /*
   * 设置窗口命中区域（SetWindowRgn）
   *
   * 渲染进程计算控件包围矩形 → 主进程 SetWindowRgn 裁剪。
   * 矩形外区域自动穿透鼠标事件。
   * D3D9 下透明窗口不支持像素级命中，setShape 是唯一穿透方案。
   *
   * rects — [{ x, y, width, height }] 矩形数组
   */
  setShape: (rects) =>
    ipcRenderer.send('floating-button:set-shape', rects),

  /*
   * CSS 淡入淡出事件订阅（主进程 → 渲染进程）
   *
   * UIAccess 兼容方案：替代窗口级 setOpacity() 动画。
   * 窗口始终保持 OS 级 opacity=1.0，通过 CSS transition
   * 在渲染进程内部驱动淡入淡出，避免 DWM/GDI 合成状态损坏。
   *
   * 返回取消订阅函数，组件卸载时调用以清理监听器。
   */
  onCssFadeOut: (callback) => {
    const listener = () => callback()
    ipcRenderer.on('floating-button:css-fade-out', listener)
    return () => { ipcRenderer.removeListener('floating-button:css-fade-out', listener) }
  },
  onCssFadeIn: (callback) => {
    const listener = () => callback()
    ipcRenderer.on('floating-button:css-fade-in', listener)
    return () => { ipcRenderer.removeListener('floating-button:css-fade-in', listener) }
  },
})

// ============================================================================
//  人数环绕选择器桥接 API — window.floatingPickerApi
//
//  使用方：Floating.vue 中的内嵌人数选择器
//
//  功能：
//    - 获取弹窗配置（背景暗度、默认人数等）
//    - 确认选择的人数后通知主进程开始抽奖
// ============================================================================
contextBridge.exposeInMainWorld('floatingPickerApi', {

  /* 获取人数选择弹窗的配置 */
  getConfig: () => ipcRenderer.invoke('floating-picker:get-config'),

  /*
   * 确认人数选择
   *
   * 用户点击"抽！"按钮后，将选中的人数发送到主进程。
   * 主进程收到后关闭选择窗口，打开抽奖结果窗口。
   *
   * count — 要抽取的学生人数（1-10 的整数）
   */
  confirm: (count) => ipcRenderer.send('floating-picker:confirm', { count })
})

// ============================================================================
//  抽取结果窗口桥接 API — window.pickResultApi
//
//  使用方：PickResult.vue
//
//  功能：
//    - 获取抽奖结果列表和配置
//    - 关闭结果窗口
//    - 监听主进程推送（新的一轮抽奖结果 + 重置指令）
//
//  特殊性：这是唯一需要"主进程推送到渲染进程"的 API 组
//          onOpen 和 onReset 使用了事件订阅模式
// ============================================================================
contextBridge.exposeInMainWorld('pickResultApi', {

  /* 获取本轮抽奖结果（学生名单数组） */
  getResults: () => ipcRenderer.invoke('pick-result:get-results'),

  /* 获取结果窗口的配置（音效、动画、颜色等） */
  getConfig: () => ipcRenderer.invoke('pick-result:get-config'),

  /* 关闭结果窗口 */
  close: () => ipcRenderer.send('pick-result:close'),

  /*
   * onOpen(callback) — 监听新一轮抽奖结果的推送
   *
   * 使用场景：
   *   用户在悬浮按钮处发起抽奖 → 主进程随机选取学生
   *   → 通过此通道推送到结果窗口 → PickResult.vue 收到后播放动画
   *
   * 参数：
   *   callback(payload) — payload 包含 { results, sessionId, ... }
   *
   * 返回值：
   *   取消订阅函数 — 调用后移除 listener（组件卸载时必须调用）
   *
   * 为什么 listener 包装了 callback：
   *   ipcRenderer.on 的原始回调签名是 (event, ...args)。
   *   包装后只传递 payload（第二个参数），隐藏 Electron 内部 event 对象。
   */
  onOpen: (callback) => {
    const listener = (_event, payload) => callback(payload)
    ipcRenderer.on('pick-result:open', listener)
    return () => {
      ipcRenderer.removeListener('pick-result:open', listener)
    }
  },

  /*
   * onReset(callback) — 监听重置指令
   *
   * 使用场景：
   *   配置更改后主进程通知结果窗口重置动画状态
   *
   * 同 onOpen，返回取消订阅函数。
   */
  onReset: (callback) => {
    const listener = (_event, payload) => callback(payload)
    ipcRenderer.on('pick-result:reset', listener)
    return () => {
      ipcRenderer.removeListener('pick-result:reset', listener)
    }
  }
})

// ============================================================================
//  渲染进程日志上报 — window.logApi
//
//  使用方：所有 Vue 组件（通过注入的 logApi 调用）
//
//  功能：
//    将渲染进程的日志消息发送到主进程，由主进程统一写入日志文件。
//    这样所有日志（主进程 + 渲染进程）集中在一个文件中，
//    方便排查跨进程的时序问题。
// ============================================================================
contextBridge.exposeInMainWorld('logApi', {
  /*
   * send(level, text) — 发送一条日志
   *
   * level — 日志级别：'info' / 'warn' / 'error'
   * text  — 日志文本（任意字符串）
   */
  send: (level, text) => ipcRenderer.send('renderer:log', { level, text })
})

// ============================================================================
//  配置面板桥接 API — window.configPanelApi
//
//  使用方：useConfigPanel.js → ConfigPanel.vue（以及所有子 Tab 组件）
//
//  功能最丰富的 API 组，覆盖配置面板的全部操作：
//    - 配置的读取、保存、重置
//    - 系统信息采集（管理员状态、路径、版本等）
//    - 权限提升（管理员重启、UIAccess）
//    - 应用生命周期（重启、关闭）
//    - Windows 计划任务创建
//    - 文件对话框（选取 .exe 路径）
//    - GitHub 更新检查
//    - 配置文件/目录在资源管理器中打开
//
//  注意：configPanelApi.close() 使用 send（单向通知），
//        其余方法都使用 invoke（需要等待主进程处理完成）。
// ============================================================================
contextBridge.exposeInMainWorld('configPanelApi', {

  /* 获取完整配置对象（等价于 config.yml 解析后的 JS 对象） */
  getConfig: () => ipcRenderer.invoke('config-panel:get-config'),

  /* 获取悬浮按钮窗口当前屏幕位置（实时；保存配置时由主进程一并带上） */
  getFloatingPosition: () => ipcRenderer.invoke('config-panel:get-floating-position'),

  /* ---- 班级管理（多班级） ---- */
  /* 列出全部班级与当前班级 id（含名单/权重） */
  listClasses: () => ipcRenderer.invoke('config-panel:list-classes'),
  /* 创建班级（立即落盘），返回新班级 */
  createClass: (name) => ipcRenderer.invoke('config-panel:create-class', { name }),
  /* 重命名班级（立即落盘） */
  renameClass: (classId, name) => ipcRenderer.invoke('config-panel:rename-class', { classId, name }),
  /* 删除班级文件（立即落盘） */
  deleteClass: (classId) => ipcRenderer.invoke('config-panel:delete-class', { classId }),

  /*
   * 保存配置到 config.yml
   *
   * config — 完整配置对象（与 DEFAULT_CONFIG 结构一致）
   *          主进程会先 normalizeConfig 再写入磁盘
   */
  saveConfig: (config) => ipcRenderer.invoke('config-panel:save-config', config),

  /*
   * 关闭配置面板窗口
   *
   * saved — true 表示已保存，false 表示取消
   *         主进程根据此值决定是否刷新悬浮按钮等已打开的窗口
   */
  close: (saved) => ipcRenderer.send('config-panel:close', { saved }),

  /* 获取系统/应用信息（管理员状态、版本号、路径等） */
  getAppInfo: () => ipcRenderer.invoke('config-panel:get-app-info'),

  /* 请求以管理员权限重新启动应用（触发 UAC 弹窗） */
  adminElevate: () => ipcRenderer.invoke('config-panel:admin-elevate'),

  /* 普通重启应用（不请求提权） */
  restart: () => ipcRenderer.invoke('config-panel:restart'),

  /*
   * 创建/更新 Windows 计划任务
   *
   * payload — { exePath, taskName, admin }
   *   主进程调用 schtasks 命令行工具创建任务
   */
  createStartupTask: (payload) =>
    ipcRenderer.invoke('config-panel:create-startup-task', payload),

  /* 用系统默认编辑器打开 config.yml */
  openConfigFile: () => ipcRenderer.invoke('config-panel:open-config-file'),

  /* 在资源管理器中打开 config.yml 所在目录 */
  openConfigDir: () => ipcRenderer.invoke('config-panel:open-config-dir'),

  /* 清理 Chromium 会话/缓存数据（sessionData 目录） */
  clearCache: () => ipcRenderer.invoke('config-panel:clear-cache'),

  /* ===== 安全管理（密码保护） ===== */
  /* 查询是否已开启密码保护 */
  getSecurityStatus: () => ipcRenderer.invoke('config-panel:get-security-status'),

  /* 验证密码 */
  verifyPassword: (password) => ipcRenderer.invoke('config-panel:verify-password', { password }),

  /* 设置/更改密码 */
  setPassword: (password) => ipcRenderer.invoke('config-panel:set-password', { password }),

  /* 关闭密码保护 */
  disablePassword: (password) => ipcRenderer.invoke('config-panel:disable-password', { password }),

  /* 忘记密码：清除所有配置 */
  resetAllConfig: () => ipcRenderer.invoke('config-panel:reset-all-config'),

  /* 保存自定义资源到 customs/ 目录（fileName/mimeType/purpose/base64） */
  saveCustom: (payload) => ipcRenderer.invoke('config-panel:save-custom', payload),

  /* 将自定义资源暂存到主进程内存（不落盘，点击“应用”时才写入 customs/） */
  saveCustomStaged: (payload) => ipcRenderer.invoke('config-panel:save-custom-staged', payload),

  /* 按 id 读取自定义资源 */
  getCustom: (payload) => ipcRenderer.invoke('config-panel:get-custom', payload),

  /* 按 id 删除自定义资源 */
  deleteCustom: (payload) => ipcRenderer.invoke('config-panel:delete-custom', payload),

  /* 检查 GitHub Releases 是否有新版本 */
  checkUpdate: () => ipcRenderer.invoke('config-panel:check-update'),

  /*
   * 打开系统原生文件对话框，筛选 .exe 文件
   *
   * 返回值：选中文件的完整路径（用户取消则为 null）
   */
  pickExeFile: () => ipcRenderer.invoke('config-panel:pick-exe-file'),

  /* 重置所有配置为 DEFAULT_CONFIG 并写入磁盘 */
  resetConfig: () => ipcRenderer.invoke('config-panel:reset-config'),

  /*
   * 从磁盘日志文件拉取最近 N 条日志
   *
   * maxLines — 可选，最多返回的行数（默认 500）
   * 返回值：日志条目数组（时间倒序，最新在前）
   */
  getLogs: (maxLines) => ipcRenderer.invoke('config-panel:get-logs', maxLines),

  /* 打开指定窗口的 DevTools（开发/生产均可用）：'floating' | 'config' | 'result' */
  openDevTools: (target) => ipcRenderer.send('config-panel:open-devtools', target)
})
