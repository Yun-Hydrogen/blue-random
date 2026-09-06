<!--
================================================================================
  组件：TabAdvanced.vue
  所属：配置面板 — 高级设置 Tab
  父组件：ConfigPanel.vue （位于 src/renderer/views/ConfigPanel.vue）

================================================================================
  一、功能概述
================================================================================
  本组件是配置面板的"高级设置"选项卡，包含五大功能板块：

    板块           | 包含的配置项
    ───────────────┼────────────────────────────────────────────
    1. 开机自启    | 程序路径（含选取按钮）、计划任务名、管理员运行开关
    2. 置顶增强    | 启动时管理员权限开关 → 控制 UIAccess 可见性
                   | UIAccess 置顶（仅当上方的开关开启时显示）
    3. 渲染后端    | 图形后端选择（d3d9/vulkan）、禁用直接合成、放弃 GPU 加速
    4. 配置管理    | 配置文件路径展示、打开目录、清理缓存、重置配置（二次确认）、重启
    5. 检查更新    | 从 GitHub Releases 拉取最新版本信息，并提供“跳转至GitHub”按钮

================================================================================
  二、数据流架构
================================================================================

  ┌─────────────────────────────────────────────────────────────┐
  │  ConfigPanel.vue（父组件）                                    │
  │  - 持有全部状态（admin, appInfo, update*）                │
  │  - 通过 props 向下传递给 TabAdvanced                         │
  │  - 通过 emit 事件接收子组件的操作请求                          │
  └────────────┬────────────────────────────────────┬───────────┘
               │ Props（只读数据）                    │ Emit（事件上报）
               ▼                                     ▼
  ┌─────────────────────────────────────────────────────────────┐
  │  TabAdvanced.vue（本组件）                                  │
  │                                                             │
  │  Props 接收：                                               │
  │    admin          — 高级设置对象（管理员权限、计划任务等）  │
  │    appInfo        — 主进程采集的系统信息（isAdmin、路径等   │
  │    updateLoading  — 是否正在检查更新                        │
  │    updateStatus   — 更新状态字符串（update / error / ''）   │
  │    updateTitle    — 更新结果标题                            │
  │    updateDetail   — 更新结果详情                            │
  │    updateReleaseUrl — GitHub Releases 页链接（控制跳转按钮显隐）│
  │                                                             │
  │  Emits 发送：                                               │
  │    update:admin       — 修改高级设置（Vue v-model 协议）    │
  │    create-startup-task — 创建/更新 Windows 计划任务         │
  │    admin-elevate      — 请求管理员权限重启                  │
  │    restart            — 普通重启应用                        │
  │    check-update       — 触发更新检查                        │
  │    reset-config       — 确认重置所有配置（二次确认后触发）  │
  │    show-in-explorer   — 在资源管理器中打开配置文件所在目录  │
  │    clear-cache        — 清理 Chromium 会话/缓存数据         │
  └─────────────────────────────────────────────────────────────┘

================================================================================
  三、主题与样式
================================================================================
  主题色：紫 #aa88dd，通过 tabTheme 变量传给 rizui_switch。
  滚动条按组件独立着色（rgba(170,136,221,*) 系列）。

================================================================================
  四、关键实现细节
================================================================================
  - "重置所有配置"按钮为危险样式（rizui_button danger），点击后弹出确认 Dialog
  - Dialog 使用 Vue <Transition> 实现飞入飞出动画
  - 文件选取通过 window.configPanelApi.pickExeFile() 调用主进程文件对话框
  - 图形后端选择使用 rizui_dropdown 组件
  - v-click-outside 指令用于自定义下拉的点击外部关闭
  - “跳转至GitHub”按钮使用 riz-ui 导出的 openURL() 打开系统浏览器
    （openURL 内部走 window.open → 主进程 setWindowOpenHandler 拦截转发）

================================================================================
  五、维护注意事项
================================================================================
  - 新增配置项时，需同时在父组件 ConfigPanel.vue 的 draft 中初始化对应字段
  - emit 事件名需与父组件的 @event-name 监听器保持一致
  - 不要在此组件中直接修改 props，始终通过 emit('update:admin', ...) 上报

================================================================================
  六、更新记录
================================================================================
  2026-08-28
    - “配置管理”卡片新增“清理缓存”行（rizui_cfgrow + danger 按钮），
      emit('clear-cache') 由父组件调用 clearCache() 删除 userData/session 缓存，
      结果以 toast 反馈    - 移除 OpenGL 渲染后端选项（性能/兼容性差），仅保留 d3d9 / vulkan；
      Vulkan 选项提示可能影响透明窗口显示
  2026-08-29
    - 新增“安全管理”卡片（rizui_cfgrow + rizui_switch + 更改密码按钮）：
      开启/更改/关闭密码保护，直接调用 window.configPanelApi.setPassword / verifyPassword /
      disablePassword，成功后 emit('security-changed')
    - “重置所有配置”确认文案更新：重置会几乎清理整个配置目录
      （名单、权重、班级、自定义资源、密码保护），不可撤销

  2026-09-06
    - “检查更新”卡片结果区新增“跳转至GitHub”按钮（riz-ui openURL）：
      无论检查结果是有更新/已最新/出错，只要拿到 releaseUrl 即显示
      跳转入口，打开 GitHub Releases 最新页（系统默认浏览器）
  最后更新：2026-09-06
================================================================================
-->

<template>
  <div class="tab-page">
    <!--
      一、开机自启
      通过 Windows 任务计划程序（Task Scheduler）实现登录时自动启动
    -->
    <rizui_card title="开机自启" desc="通过 Windows 计划任务实现自动启动" icon="fa-solid fa-power-off">

      <!-- 程序路径：文本输入框 + 选取按钮 -->
      <rizui_cfgrow label="程序路径" hint="蔚蓝点名 可执行文件(.exe)的完整绝对路径" stack>
        <div class="adv-input-row">
          <rizui_text
            v-model="adminAutoStartPath"
            placeholder="留空则自动检测"
            :color="tabTheme"
            style="flex: 1; min-width: 0; text-align: left;"
          />
          <rizui_button text="选取" l-icon="fa-regular fa-folder" primary :color="tabTheme" @click="pickExePath" />
        </div>
      </rizui_cfgrow>

      <!-- 计划任务名 -->
      <rizui_cfgrow label="计划任务名" hint="在 Windows 任务计划程序中的显示名称" stack>
        <rizui_text
          v-model="adminAutoStartTaskName"
          placeholder="Blue Random (Admin)"
          :color="tabTheme"
          style="text-align: left;"
        />
      </rizui_cfgrow>

      <!-- 管理员运行开关 + 创建任务按钮 -->
      <rizui_cfgrow label="以管理员身份运行" hint="启动时自动获取管理员权限">
        <rizui_switch :model-value="admin.adminAutoStartAdmin !== false" :color="tabTheme" @update:model-value="$emit('update:admin', { ...admin, adminAutoStartAdmin: $event })" />
      </rizui_cfgrow>
      <div style="border-top: 1px solid var(--c-border); padding-top: 16px; display: flex; justify-content: flex-end;">
        <rizui_button text="创建/更新计划任务(需要管理员权限)" l-icon="fa-solid fa-shield-halved" primary :color="tabTheme" @click="handleCreateTask" />
      </div>
    </rizui_card>

    <!-- 二、置顶增强
       设计说明：
         不再依赖 appInfo.isAdmin（当前进程权限）来控制 UIAccess 显隐，
         而是通过"启动时获得管理员权限"开关让用户显式控制。
         开关关闭 → 提示"UIAccess 需要管理员权限"
         开关开启 → 显示 UIAccess 配置行 + DLL 检测状态
    -->
    <rizui_card title="置顶增强" desc="更高级别的置顶功能，需要先启动悬浮按钮置顶功能" icon="fa-solid fa-thumbtack">

      <!-- 启动时管理员权限开关 -->
      <rizui_cfgrow label="启动时获得管理员权限" hint="开启后显示 UIAccess 置顶选项，每次启动时自动请求管理员权限">
        <rizui_switch :model-value="admin.requireAdminOnLaunch === true" :color="tabTheme" @update:model-value="$emit('update:admin', { ...admin, requireAdminOnLaunch: $event })" />
      </rizui_cfgrow>

      <!-- UIAccess 置顶：仅当"启动时管理员权限"开启时显示 -->
      <template v-if="admin.requireAdminOnLaunch">
        <rizui_cfgrow label="UIAccess 置顶" hint="系统级置顶权限，可覆盖绝大部分应用">
          <rizui_switch :model-value="admin.uiAccessEnabled" :color="tabTheme" @update:model-value="$emit('update:admin', { ...admin, uiAccessEnabled: $event })" />
        </rizui_cfgrow>
        <div v-if="!appInfo.uiAccessDllExists && admin.uiAccessEnabled" class="cfg-hint warn">
          <i class="fa-regular fa-circle-xmark"></i> 未检测到 uiaccess.dll，UIAccess 功能将不可用，请检查程序完整性。
        </div>
        <div v-if="appInfo.uiAccessDllExists && admin.uiAccessEnabled" class="cfg-hint success">
          <i class="fa-regular fa-circle-check"></i> UIAccess 功能可用。
        </div>
      </template>
      <div v-else class="cfg-hint warn" style="margin-top: 4px;">
        <i class="fa-solid fa-triangle-exclamation"></i> UIAccess 置顶需要管理员权限，请先开启"启动时获得管理员权限"。
      </div>
    </rizui_card>

    <!-- 三、渲染后端 -->
    <rizui_card title="渲染后端" desc="切换 Chromium 图形后端，需重启生效。D3D9 为推荐后端（性能与兼容性最佳，UIAccess 可用）。Vulkan 可能影响透明窗口显示。" icon="fa-solid fa-microchip">

      <rizui_cfgrow label="图形后端" hint="推荐 D3D9。Vulkan 可用于测试，但透明悬浮按钮可能异常。需要重启。">
        <rizui_dropdown v-model="renderingBackend" :options="backendOptions" :color="tabTheme" />
      </rizui_cfgrow>

      <rizui_cfgrow label="禁用直接合成" hint="禁用直接合成(启用开关)可能有助于解决某些渲染问题。重启生效。">
        <rizui_switch :model-value="admin.disableDirectComposition !== false" :color="tabTheme" @update:model-value="$emit('update:admin', { ...admin, disableDirectComposition: $event })" />
      </rizui_cfgrow>

      <rizui_cfgrow label="禁用 GPU 加速" hint="启用后完全回退到 CPU 软件渲染。仅用于排查兼容性问题，日常使用不建议开启。重启生效。">
        <rizui_switch :model-value="admin.disableHardwareAcceleration === true" :color="tabTheme" @update:model-value="$emit('update:admin', { ...admin, disableHardwareAcceleration: $event })" />
      </rizui_cfgrow>
    </rizui_card>

    <!-- 四、配置管理 -->
    <rizui_card title="配置管理" desc="管理配置文件和应用状态" icon="fa-solid fa-gear">

      <!-- 配置文件路径 + 打开目录按钮 -->
      <rizui_cfgrow label="配置文件" :hint="appInfo.configPath || '未获取'">
        <rizui_button text="打开" r-icon="fa-solid fa-up-right-from-square" primary :color="tabTheme" @click="$emit('show-in-explorer')" />
      </rizui_cfgrow>

      <!-- 清理缓存：删除 Chromium 会话/缓存数据（userData/session），不影响任何配置 -->
      <rizui_cfgrow label="清理缓存" hint="删除临时生成的缓存目录（Cache、Local Storage 等），以释放磁盘空间；不影响任何配置。">
        <rizui_button text="清理" r-icon="fa-solid fa-broom" danger @click="$emit('clear-cache')" />
      </rizui_cfgrow>

      <!-- 操作按钮组 -->
      <div style="border-top: 1px solid var(--c-border); padding-top: 16px; display: flex; justify-content: flex-end;">
        <div class="adv-btn-group">
          <rizui_button text="重置所有配置" r-icon="fa-solid fa-eraser" danger @click="showResetDialog = true" />
          <rizui_button text="重启应用" l-icon="fa-solid fa-rotate" primary :color="tabTheme" @click="$emit('restart')" />
          <rizui_button text="管理员重启（需要管理员权限）" l-icon="fa-solid fa-shield-halved" primary :color="tabTheme" @click="$emit('admin-elevate')" />
        </div>
      </div>
    </rizui_card>

    <!-- 五、安全管理 -->
    <rizui_card title="安全管理" desc="为配置面板添加密码保护" icon="fa-solid fa-shield-halved">
      <rizui_cfgrow label="密码保护" hint="开启后每次打开配置界面需输入密码解锁以防止恶意操作">
        <rizui_switch :model-value="securityEnabled" :color="tabTheme" @update:model-value="onSecurityToggle($event)" />
      </rizui_cfgrow>
      <rizui_infobox type="error" text="请务必牢记密码，否则只能清除所有配置以恢复"></rizui_infobox>
      <div style="border-top: 1px solid var(--c-border); padding-top: 16px; display: flex; justify-content: flex-end;">
        <rizui_button text="更改密码" l-icon="fa-solid fa-key" primary :color="tabTheme" @click="openSecurityDialog('change')" :disabled="!securityEnabled" />
      </div>
    </rizui_card>

    <!-- 六、检查更新 -->
    <rizui_card title="检查更新" desc="从 GitHub Releases 检查是否有新版本" icon="fa-solid fa-cloud-arrow-up">
      <div style="padding: 8px 0;">
        <rizui_button
          :text="updateLoading ? '检查中…' : '立即检查更新'"
          long
          primary
          :color="tabTheme"
          :disabled="updateLoading"
          @click="$emit('check-update')"
        />
      </div>
      <div v-if="updateStatus" class="cfg-hint" :class="updateStatus">{{ updateTitle }}</div>
      <div v-if="updateDetail" class="update-detail-text">{{ updateDetail }}</div>
      <!-- 无论是否有更新，均提供 GitHub Releases 跳转入口 -->
      <rizui_button 
        primary
        v-if="updateReleaseUrl"
        text="跳转至GitHub"
        r-icon="fa-solid fa-cloud-arrow-up"
        long
        @click="openURL('https://github.com/Yun-Hydrogen/blue-random/releases/latest')"
        :color="tabTheme"
        style="margin-top: 10px;"
      />
    </rizui_card>

    <!-- 重置确认 Dialog -->
    <rizui_dialog v-model:show="showResetDialog" title="确认重置" :color="tabTheme" teleport=".config-left">
      <p>此操作将<strong>几乎清理整个配置目录</strong>并恢复为默认值：<br>名单、权重、自定义设置、班级、自定义资源与密码保护均会被清除。此操作不可撤销。</p>
      <template #footer>
        <rizui_button text="取消" @click="showResetDialog = false" />
        <rizui_button text="确认重置" danger @click="confirmReset" />
      </template>
    </rizui_dialog>

    <!-- 计划任务结果 Dialog -->
    <rizui_dialog v-model:show="showTaskDialog" :title="taskDialogTitle" :color="tabTheme" teleport=".config-left">
      <p>{{ taskDialogMsg }}</p>
      <template #footer>
        <rizui_button text="确定" primary :color="tabTheme" @click="showTaskDialog = false" />
      </template>
    </rizui_dialog>

    <!-- 安全管理 Dialog（开启 / 更改 / 关闭密码） -->
    <rizui_dialog v-model:show="securityDialog.show" :title="securityDialog.title" :color="tabTheme" teleport=".config-left" :mask-close="false">
      <div class="security-dialog-body">
        <rizui_text v-if="securityDialog.mode !== 'enable'" v-model="securityDialog.oldPwd" type="password" placeholder="当前密码" :color="tabTheme" style="text-align: left;" />
        <rizui_text v-if="securityDialog.mode !== 'disable'" v-model="securityDialog.newPwd" type="password" placeholder="新密码（至少 4 位）" :color="tabTheme" style="text-align: left;" />
        <rizui_text v-if="securityDialog.mode === 'enable'" v-model="securityDialog.confirmPwd" type="password" placeholder="确认新密码" :color="tabTheme" style="text-align: left;" @enter="confirmSecurity" />
      </div>
      <p v-if="securityDialog.error" class="security-dialog-error">{{ securityDialog.error }}</p>
      <template #footer>
        <rizui_button text="取消" @click="securityDialog.show = false" :disabled="securityDialog.busy" />
        <rizui_button text="确定" primary :color="tabTheme" @click="confirmSecurity" :disabled="securityDialog.busy" />
      </template>
    </rizui_dialog>
  </div>
</template>

<script setup>
// ============================================================
//  导入依赖
//  ref      — 创建响应式变量（值变化时自动更新界面）
//  onMounted — 组件挂载完成后的生命周期钩子
// ============================================================
import { ref, reactive, computed, onMounted } from 'vue'
import { rizui_card, rizui_cfgrow, rizui_switch, rizui_dialog, rizui_dropdown, rizui_text, rizui_button, rizui_infobox, openURL } from 'riz-ui'

/* Tab 主题色 */
const tabTheme = '#aa88dd'

// ============================================================
//  Props 定义（从父组件 ConfigPanel.vue 接收的数据）
//  注意：props 是只读的，不能直接修改！
//  要修改配置，必须通过 emit('update:admin', 新对象) 上报
// ============================================================
const props = defineProps({
  /*
   * admin — 高级设置对象
   * 包含管理员权限、计划任务、UIAccess 等高级配置
   * 常用字段：adminAutoStartPath, adminAutoStartTaskName,
   *           adminAutoStartAdmin, uiAccessEnabled,
   *           renderingBackend, disableDirectComposition,
   *           disableHardwareAcceleration
   */
  admin: Object,

  /*
   * appInfo — 主进程采集的系统信息
   * 字段：isAdmin（是否管理员）, isUiAccess（是否 UIAccess 模式）,
   *       isWindows（是否 Windows）, uiAccessDllExists（DLL 是否存在）,
   *       configPath（配置文件路径）, configDir（配置目录）,
   *       exePath（程序自身路径）, version（应用版本号）
   */
  appInfo: Object,

  /* updateLoading — 是否正在向 GitHub 请求版本信息 */
  updateLoading: Boolean,

  /* updateStatus — 更新结果类型：'update'（有新版本）/ 'error'（出错）/ ''（无结果） */
  updateStatus: String,

  /* updateTitle — 更新结果的标题文字 */
  updateTitle: String,

  /* updateDetail — 更新结果的详细说明（支持换行） */
  updateDetail: String,

  /* updateReleaseUrl — GitHub Releases 页面链接（无论是否有更新均可跳转） */
  updateReleaseUrl: String,

  /* securityEnabled — 是否已开启密码保护（安全管理） */
  securityEnabled: Boolean
})

// ============================================================
//  Emits 定义（向父组件发送事件）
//  Vue 3 中 emit 必须提前声明，否则会有运行时警告
// ============================================================
const emit = defineEmits([
  /*
   * update:admin — 修改高级设置（Vue v-model 协议）
   * payload: 新的完整 admin 对象
   * 用法：emit('update:admin', { ...props.admin, 字段: 新值 })
   * 注意：必须展开原对象再覆盖，否则会丢失其他字段
   */
  'update:admin',

  /* open-config-file — 用系统默认编辑器打开配置文件 */
  'open-config-file',

  /* open-config-dir — 打开配置目录（备用） */
  'open-config-dir',

  /* clear-cache — 清理 Chromium 会话/缓存数据（userData/session） */
  'clear-cache',

  /* admin-elevate — 请求以管理员权限重新启动应用 */
  'admin-elevate',

  /* restart — 普通重启应用 */
  'restart',

  /* create-startup-task — 创建或更新 Windows 计划任务 */
  'create-startup-task',

  /* check-update — 触发 GitHub Releases 版本检查 */
  'check-update',

  /* reset-config — 确认重置所有配置（对话框确认后触发） */
  'reset-config',

  /* show-in-explorer — 在文件资源管理器中打开配置文件所在目录 */
  'show-in-explorer',

  /* security-changed — 密码保护状态发生变化（父组件刷新安全状态） */
  'security-changed'
])

// ============================================================
//  状态与生命周期
// ============================================================

// ============================================================
//  自定义下拉框（替代原生 <select>）
//
//  使用 div + button 实现，配合 v-click-outside 指令。
//  下拉面板使用 Vue <Transition name="drop-pop"> 动画。
// ============================================================

/* 渲染后端下拉选项 */
const backendOptions = [
  { value: 'd3d9', label: 'D3D9 (默认)' },
  { value: 'vulkan', label: 'Vulkan' }
]

/* v-model 桥接：props.admin.renderingBackend ↔ rizui_dropdown */
const renderingBackend = computed({
  get: () => props.admin.renderingBackend || 'd3d9',
  set: (val) => emit('update:admin', { ...props.admin, renderingBackend: val })
})

/* v-model 桥接：rizui_text 输入框 ↔ props.admin */
const adminAutoStartPath = computed({
  get: () => props.admin.adminAutoStartPath || '',
  set: (val) => emit('update:admin', { ...props.admin, adminAutoStartPath: val })
})

const adminAutoStartTaskName = computed({
  get: () => props.admin.adminAutoStartTaskName || '',
  set: (val) => emit('update:admin', { ...props.admin, adminAutoStartTaskName: val })
})

/*
 * v-click-outside 自定义指令
 * 点击/触摸元素外部时触发回调，用于关闭下拉面板。
 * 同时监听 mousedown 和 touchstart，捕获阶段确保不被 stopPropagation 阻止。

// ============================================================
//  函数：confirmReset — 确认重置配置
// ============================================================
/*
 * 功能：关闭确认对话框，并向父组件发送重置指令。
 *
 * 调用时机：用户在"确认重置"Dialog 中点击"确认重置"按钮时触发。
 *
 * 执行流程：
 *   1. 关闭 Dialog（showResetDialog.value = false）
 *   2. emit('reset-config') → 父组件收到后执行实际重置逻辑（TODO）
 *
 * 注意：
 *   - 当前父组件的重置逻辑尚未实现（标记 TODO），emit 后仅关闭对话框。
 *   - 不要在此函数中直接修改 props.admin，
 *     重置逻辑应由父组件统一处理。
 */

/* showResetDialog — 控制重置确认对话框的显示/隐藏 */
const showResetDialog = ref(false)

function confirmReset() {
  showResetDialog.value = false
  emit('reset-config')
}

/* ---- 计划任务创建结果 ---- */
const showTaskDialog = ref(false)
const taskDialogTitle = ref('')
const taskDialogMsg = ref('')

async function handleCreateTask() {
  try {
    const result = await window.configPanelApi?.createStartupTask({
      exePath: props.admin.adminAutoStartPath || props.appInfo.exePath,
      taskName: props.admin.adminAutoStartTaskName,
      admin: props.admin.adminAutoStartAdmin
    })
    if (!result) return
    taskDialogTitle.value = result.ok ? '创建成功' : '创建失败'
    taskDialogMsg.value = result.message || (result.ok ? '计划任务已创建/更新。' : '未知错误')
  } catch (e) {
    taskDialogTitle.value = '创建失败'
    taskDialogMsg.value = e?.message || String(e)
  }
  showTaskDialog.value = true
}

// ============================================================
//  函数：pickExePath — 选取可执行文件路径
// ============================================================
/*
 * 功能：打开系统原生文件选择对话框，让用户选取一个 .exe 文件，
 *       选中后将路径自动填入"程序路径"文本框。
 *
 * 调用时机：用户在"程序路径"行点击"选取"按钮时触发。
 *
 * 执行流程：
 *   1. 调用 window.configPanelApi.pickExeFile()
 *      → 通过 IPC 通知主进程打开文件对话框
 *      → 主进程调用 Electron dialog.showOpenDialog()，筛选 .exe 文件
 *      → 返回选中文件的完整路径（用户取消则返回 null）
 *   2. 如果选中了文件（filePath 不为 null/空）：
 *      emit('update:admin', { ...props.admin, adminAutoStartPath: filePath })
 *      → 将路径更新到 admin.adminAutoStartPath
 *
 * 注意：
 *   - 这是一个 async 函数，因为 IPC 调用是异步的（需要等待用户操作对话框）
 *   - 使用扩展运算符 ...props.admin 创建新对象，
 *     确保 Vue 能检测到数据变化（响应式要求）
 *   - 如果用户在对话框中点击"取消"，不做任何操作
 *   - window.configPanelApi 由 preload.js 通过 contextBridge 注入，
 *     如果 API 不可用（如非 Electron 环境），调用会报错
 */
async function pickExePath() {
  // 调用主进程文件对话框，等待用户选择
  const filePath = await window.configPanelApi.pickExeFile()
  // 用户取消选择时 filePath 为 null，跳过更新
  if (filePath) {
    emit('update:admin', { ...props.admin, adminAutoStartPath: filePath })
  }
}

// ============================================================
//  安全管理（密码保护）
//  开启：写 SHA256 到 <配置根目录>/.SHA256（主进程 security.js）
//  更改：验证当前密码 → 写入新密码
//  关闭：验证当前密码 → 删除哈希文件
// ============================================================
const securityDialog = reactive({
  show: false,
  mode: 'enable',        // enable | change | disable
  title: '',
  oldPwd: '',
  newPwd: '',
  confirmPwd: '',
  error: '',
  busy: false
})

/* 开关切换：关闭 → 开启（设置密码）；开启 → 关闭（验证当前密码） */
function onSecurityToggle(on) {
  openSecurityDialog(on ? 'enable' : 'disable')
}

function openSecurityDialog(mode) {
  securityDialog.mode = mode
  securityDialog.title = mode === 'enable' ? '开启密码保护'
    : (mode === 'change' ? '更改密码' : '关闭密码保护')
  securityDialog.oldPwd = ''
  securityDialog.newPwd = ''
  securityDialog.confirmPwd = ''
  securityDialog.error = ''
  securityDialog.show = true
}

async function confirmSecurity() {
  if (securityDialog.busy) return
  const { mode, oldPwd, newPwd, confirmPwd } = securityDialog
  securityDialog.error = ''
  try {
    if (mode === 'enable') {
      if (!newPwd || newPwd.length < 4) { securityDialog.error = '密码至少 4 位'; return }
      if (newPwd !== confirmPwd) { securityDialog.error = '两次输入的密码不一致'; return }
      securityDialog.busy = true
      const r = await window.configPanelApi?.setPassword?.(newPwd)
      if (r?.ok) {
        securityDialog.show = false
        emit('security-changed')
      } else {
        securityDialog.error = (r && r.message) || '设置失败'
      }
    } else if (mode === 'change') {
      if (!oldPwd) { securityDialog.error = '请输入当前密码'; return }
      if (!newPwd || newPwd.length < 4) { securityDialog.error = '新密码至少 4 位'; return }
      securityDialog.busy = true
      const v = await window.configPanelApi?.verifyPassword?.(oldPwd)
      if (!v?.ok) { securityDialog.error = (v && v.message) || '当前密码错误'; return }
      const r = await window.configPanelApi?.setPassword?.(newPwd)
      if (r?.ok) {
        securityDialog.show = false
        emit('security-changed')
      } else {
        securityDialog.error = (r && r.message) || '更改失败'
      }
    } else { /* disable */
      if (!oldPwd) { securityDialog.error = '请输入当前密码'; return }
      securityDialog.busy = true
      const r = await window.configPanelApi?.disablePassword?.(oldPwd)
      if (r?.ok) {
        securityDialog.show = false
        emit('security-changed')
      } else {
        securityDialog.error = (r && r.message) || '关闭失败'
      }
    }
  } catch (error) {
    securityDialog.error = String((error && error.message) || '操作失败')
  } finally {
    securityDialog.busy = false
  }
}
</script>

<style scoped>
/* =================================================================
   主题色：紫 #aa88dd
   本文件所有颜色均硬编码，不依赖外部 CSS 变量。
   修改主题色时需同步替换所有 #aa88dd 及 rgba(170,136,221,*)。
   ================================================================= */

/* =================================================================
   1. 页面进入动画
   ================================================================= */
.tab-page {
  animation: slide-in 0.3s cubic-bezier(0.25, 0, 0.25, 1);
}

@keyframes slide-in {
  from {
    opacity: 0;
    transform: translateX(24px);
  }
  to {
    opacity: 1;
    transform: translateX(0);
  }
}

/* =================================================================
   2. 自定义滚动条（整个 Tab 页面）
   使用主题色半透明，与卡片风格协调。
   ================================================================= */
::-webkit-scrollbar {
  width: 6px;
  height: 6px;
}

::-webkit-scrollbar-track {
  background: transparent;
}

::-webkit-scrollbar-thumb {
  background: rgba(170, 136, 221, 0.35);
  border-radius: 3px;
}

::-webkit-scrollbar-thumb:hover {
  background: rgba(170, 136, 221, 0.55);
}

::-webkit-scrollbar-button {
  display: none;
}

::-webkit-scrollbar-corner {
  background: transparent;
}

/* 通用提示信息 */
.cfg-hint {
  font-size: 12px;
  color: #888;
  margin-top: 4px;
}

/* 警告提示（橙色） */
.cfg-hint.warn {
  color: #e80;
}

/* 更新成功提示（绿色） */
.cfg-hint.update {
  color: #4a8;
}

/* 更新失败提示（红色） */
.cfg-hint.error {
  color: #d44;
}

/* 更新详情的多行文本 */
.update-detail-text {
  font-size: 12px;
  color: #888;
  white-space: pre-wrap;
  margin-top: 4px;
}

/* =================================================================
   5. 输入框 + 选取按钮布局
   ================================================================= */
.adv-input-row {
  display: flex;
  gap: 8px;
  align-items: center;
  width: 100%;
}

.adv-input-row > * {
  flex-shrink: 0;
}

/* =================================================================
   6. 按钮组（多个按钮水平排列）
   ================================================================= */
.adv-btn-group {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
  justify-content: flex-end;
}

/* =================================================================
   8. 关于板块
   ================================================================= */
.about-section {
  font-size: 12px;
  color: #667;
  line-height: 2;
}

.about-item {
  margin: 2px 0;
}

/* 标签列（如"界面字体"、"源码许可"等） */
.about-label {
  display: inline-block;
  min-width: 60px;
  color: #99a;
}

/* 关于板块中的链接 */
.about-section a {
  color: #aa88dd;
  text-decoration: none;
  transition: color 0.15s;
}

.about-section a:hover {
  color: #8866bb;
}

/* 版权声明文字（更小、更淡） */
.about-copyright {
  margin-top: 8px;
  color: #aaa;
  font-size: 11px;
}

/* ===== 安全管理 Dialog ===== */
.security-dialog-body {
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.security-dialog-error {
  color: #d44;
  font-size: 12px;
  margin: 8px 0 0;
}

</style>
