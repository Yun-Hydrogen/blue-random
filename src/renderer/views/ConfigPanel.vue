<!--
================================================================================
  组件：ConfigPanel.vue
  所属：配置面板窗口的根组件
  路由：/config-panel（由 main.js 通过 BrowserWindow 直接加载，不经过 vue-router）

================================================================================
  一、功能概述
================================================================================
  本组件是配置面板（从托盘右键 → 配置 打开）的编排组件，负责：

    功能              | 说明
    ──────────────────┼──────────────────────────────────────────────
    1. Tab 导航       | 5 个选项卡的胶囊滑块导航，主题色随 Tab 切换
    2. 全局状态管理   | draft（配置草稿）+ appInfo（系统信息），委托给 useConfigPanel.js
    3. 子组件编排     | TabRoster / TabFloating / TabResult / TabAdvanced / TabAbout / TabLogs
    4. IPC 操作       | 保存/重置配置、管理员重启、更新检查等，通过 configPanelApi 桥接
    5. 芯片 tooltip   | 全局 fixed 定位的 tooltip，由 TabRoster 的 chip-hover 事件触发
    6. 主题色跟随     | 滑块/按钮/开关等控件颜色随当前 Tab 的主题色动态变化
    7. 背景泡泡装饰   | 10 个跟随主题色的浮动圆圈，位于面板之下、底色之上
    8. 右侧日志面板   | TabLogs 组件固定在右侧区域

================================================================================
  二、布局结构与 z-index 层叠
================================================================================

  ┌────────────────────────────────────────────────────┐
  │ .config-layout (position: relative, bg: #e8ecf1)    │
  │                                                    │
  │  ┌─ .bg-decoration (absolute, z:1, bg: #fff) ────┐ │
  │  │  10 个 .bg-circle  泡泡在白色画布上浮动         │ │
  │  └────────────────────────────────────────────────┘ │
  │  ┌───────────────┐  ┌─────────────────────────────┐ │
  │  │ .config-left  │  │ .config-right               │ │
  │  │ z:1, 0.4 透明 │  │ z:1, 0.4 透明               │ │
  │  │ [TabBar]      │  │ [TabLogs]                   │ │
  │  │ [内容区]      │  │ [底部按钮]                   │ │
  │  └───────────────┘  └─────────────────────────────┘ │
  └────────────────────────────────────────────────────┘

  层叠方案（2026-07-20 最终版）：
    - 三层同为 z-index: 1，靠 DOM 顺序自然堆叠
    - .bg-decoration 最先渲染 → 最底层，background: #fff 自成白色画布
    - .config-left / .config-right 后渲染 → 覆盖在泡泡上方
    - 面板统一 rgba 透明度 0.4，泡泡从两侧均匀透出，不遮挡内容

  窗口尺寸：980×680（windows.js createConfigPanelWindow），不可调整大小。
  左侧 flex: 1.5，右侧 flex: 1（3:2 比例）。

================================================================================
  三、数据流架构
================================================================================

  +-------------------------------------------------------------+
  |  main.js / ipc.js（主进程）                                   |
  |  - configPanelApi 提供 15 个 IPC 通道                        |
  |  - 读取/写入 config.yml                                      |
  +--------------------------+----------------------------------+
                             | IPC (contextBridge)
                             v
  +-------------------------------------------------------------+
  |  useConfigPanel.js（逻辑层）                                  |
  |  - draft（响应式配置草稿）                                    |
  |  - 所有 IPC 操作方法（openConfigFile, adminElevate 等）       |
  |  - Tab 导航状态（activeTab, sliderLeft 等）                   |
  |  - tooltip 状态                                             |
  +--------------------------+----------------------------------+
                             | 解构导出
                             v
  +-------------------------------------------------------------+
  |  ConfigPanel.vue（本组件 - 视图层）                            |
  |  - 模板：bg-decoration + config-left + config-right + tooltip|
  |  - 仅负责渲染和事件绑定，不包含业务逻辑                        |
  +--------------------------+----------------------------------+
                             | props / emits
              +--------------+-------+-------+-------+----------+
              v              v       v       v       v
        TabRoster    TabFloating TabResult TabAdvanced TabAbout
              +
           TabLogs

================================================================================
  四、子组件 Props/Emits 约定
================================================================================

  组件         | Props                                    | Emits
  -------------|------------------------------------------|------------------------------
  TabRoster    | studentList, allowRepeatDraw             | update:studentList, update:allowRepeatDraw, chip-hover, chip-leave
  TabFloating  | fb (floatingButton 配置对象)              | update:fb
  TabResult    | pickResult (pickResultDialog 配置对象)    | update:pickResult
  TabAdvanced  | admin, appInfo, updateLoading/Status/Title/Detail | update:admin + 7 个操作事件
  TabAbout     | appInfo                                  | （无，纯展示组件）
  TabLogs      | appInfo                                  | （无，纯展示组件）

================================================================================
  五、Tab 胶囊滑块导航
================================================================================
  导轨：28px 高，白色药丸形，border 颜色随 currentTab.color 动态变化
  滑块：±3px 悬浮于导轨，left 0.45s / width 0.45s cubic-bezier，固定宽度 80px

  动画序列：
    1. 滑块先移动到目标 Tab 位置（0.45s）
    2. 图标立即变白（color 0.2s 0s）
    3. 等滑块到位后，文字缓慢展开变色（max-width/opacity/color 0.5s 0.45s）

================================================================================
  六、背景泡泡装饰
================================================================================
  10 个跟随主题色的浮动圆圈，尺寸 180-200px，动画周期 34-42s。

  设计要点：
    - .bg-decoration 设 background: #fff 白色实底，作为泡泡的独立画布
    - 泡泡 opacity 0.10-0.16，主题色透过白色画布呈现柔和的淡彩效果
    - pointer-events: none 确保不干扰鼠标操作
    - 修改泡泡数量/大小/速度：编辑 .c1 ~ .c10 的 CSS 自定义属性

================================================================================
  七、CSS 组织
================================================================================
  本文件 CSS 为 scoped（Vue 自动添加 data-v-xxx 属性隔离）。
  子组件样式已全部分离至各自 .vue 文件，ConfigPanel 仅保留自身模板元素的样式。
  2026-07-20 清理了 ~300 行死代码（.roster-* / .cfg-* / .switch / .card 等）。

    1. 全局滚动条        — 蔚蓝主题蓝色椭圆滑块
    2. 双栏布局          — .config-layout / .config-left / .config-right + 背景泡泡
    3. Tab 导航栏        — .tab-bar / .tab-slider / .tab-item / .tab-text
    4. 内容区            — .config-body
    5. 加载骨架          — .loading-skeleton
    6. 全局 tooltip      — .chip-tooltip
    7. 底部操作区        — .config-footer / .action-btn / .devtools-bar

================================================================================
  八、维护注意事项
================================================================================
  - 业务逻辑全在 useConfigPanel.js 中，本文件只做渲染和事件转发
  - 新增 Tab 时：在模板中按相同模式添加子组件 + 在 useConfigPanel tabs 数组中注册
  - CSS 虽然是 scoped，但滚动条伪元素（::-webkit-scrollbar）会影响全局
  - 泡泡修改：编辑 .c1~.c10 的 CSS，面板透明度改 .config-left/.config-right 的 rgba alpha
  - 底部按钮（取消）的样式不跟随 Tab 主题色，但应用按钮跟随。

================================================================================
  九、更新记录
================================================================================
  2026-08-28
    - 新增全局 toast 通知：渲染 rizui_toast（右上角，倒计时自动关闭），
      状态由 useConfigPanel 的 toast 提供（保存成功/失败反馈）
    - TabRoster 新增 :saveVersion prop：应用保存成功后自动重载班级列表
    - TabAdvanced 新增 @clear-cache 事件绑定 → clearCache()（清理缓存，toast 反馈）
  2026-08-29
    - 新增密码锁定（安全管理）：配置界面打开时若已开启密码保护，整个界面
      覆盖 .security-lock 遮罩，需输入密码解锁（unlock）；提供“忘记密码”
      清除所有配置（forgetReset）
    - TabAdvanced 新增 :security-enabled prop 绑定与 @security-changed 刷新

  最后更新：2026-08-29
================================================================================
-->
<template>
  <div class="config-layout" :style="rootStyle">
    <!-- 背景装饰浮动圆圈 -->
    <div class="bg-decoration" aria-hidden="true">
      <span class="bg-circle c1"></span>
      <span class="bg-circle c2"></span>
      <span class="bg-circle c3"></span>
      <span class="bg-circle c4"></span>
      <span class="bg-circle c5"></span>
      <span class="bg-circle c6"></span>
      <span class="bg-circle c7"></span>
      <span class="bg-circle c8"></span>
      <span class="bg-circle c9"></span>
      <span class="bg-circle c10"></span>
    </div>

    <!-- ====== 左侧：配置面板 ====== -->
    <div class="config-left">
      <!-- Tab 胶囊滑块导航 -->
      <div class="tab-bar" ref="tabBarRef" :style="trackBorderStyle">
        <div class="tab-slider" :style="sliderStyle"></div>
        <button
          v-for="tab in tabs"
          :key="tab.id"
          class="tab-item"
          :class="{ active: activeTab === tab.id }"
          :style="tabItemStyle(tab)"
          @click="switchTab(tab.id)"
        >
          <span class="tab-label">
            <span class="tab-icon-svg" v-html="tab.icon"></span>
            <span class="tab-text" :class="{ show: activeTab === tab.id }">{{ tab.label }}</span>
          </span>
        </button>
      </div>

      <!-- 内容区 -->
      <div class="config-body" v-if="!loading">
        <TabRoster
          v-show="activeTab === 'roster'"
          :studentList="draft.studentList"
          :allowRepeatDraw="draft.allowRepeatDraw"
          :saveVersion="saveVersion"
          @update:studentList="draft.studentList = $event"
          @update:allowRepeatDraw="draft.allowRepeatDraw = $event"
          @update:activeClassId="draft.activeClassId = $event"
          @chip-hover="showChipTooltip"
          @chip-leave="hideChipTooltip"
        />
        <TabFloating
          v-show="activeTab === 'floating'"
          :fb="draft.floatingButton"
          @update:fb="draft.floatingButton = $event"
        />
        <TabResult
          v-show="activeTab === 'result'"
          :pickResult="draft.pickResultDialog"
          @update:pickResult="draft.pickResultDialog = $event"
        />
        <TabAdvanced
          v-show="activeTab === 'advanced'"
          :admin="draft.admin"
          :appInfo="appInfo"
          :security-enabled="security.enabled"
          :updateLoading="updateLoading"
          :updateStatus="updateStatus"
          :updateTitle="updateTitle"
          :updateDetail="updateDetail"
          :updateReleaseUrl="updateReleaseUrl"
          @update:admin="draft.admin = $event"
          @open-config-file="openConfigFile"
          @open-config-dir="openConfigDir"
          @clear-cache="clearCache"
          @security-changed="refreshSecurity"
          @admin-elevate="adminElevate"
          @restart="appRestart"
          @create-startup-task="createStartupTask"
          @check-update="checkUpdate"
          @reset-config="resetConfig"
          @show-in-explorer="showInExplorer"
        />

        <!-- 关于应用 Tab -->
        <TabAbout
          v-show="activeTab === 'about'"
          :appInfo="appInfo"
        />
      </div>

      <!-- 加载骨架 -->
      <div v-else class="loading-skeleton">
        <div class="loading-spinner"></div>
        <p>正在加载配置...</p>
      </div>
    </div>

    <!-- ====== 右侧：运行日志 + 底部按钮 ====== -->
    <div class="config-right">
      <TabLogs :appInfo="appInfo" />

      <!-- 底部操作区 -->
      <div class="config-footer">
        <div class="footer-left">
          <span class="devtools-label">开发者工具 | DevTools </span>
          <div class="devtools-bar">
            <button class="devtools-btn" @click="openDevTools('floating')">悬浮按钮</button>
            <button class="devtools-btn" @click="openDevTools('config')">配置面板</button>
            <button class="devtools-btn" @click="openDevTools('result')">结果浮窗</button>
          </div>
        </div>
        <div class="footer-actions">
          <button class="action-btn action-cancel" @click="handleCancel" title="取消">
            <svg viewBox="0 0 24 24" width="18" height="18" stroke="currentColor" stroke-width="2.5" fill="none"><path d="M18 6L6 18M6 6l12 12"/></svg>
          </button>
          <button class="action-btn action-apply" :style="applyBtnStyle" @click="handleApply" title="应用">
            <svg viewBox="0 0 24 24" width="16" height="16" stroke="currentColor" stroke-width="2.5" fill="none"><path d="M20 6L9 17l-5-5"/></svg>
            <span class="action-btn-apply-text">应用</span>
          </button>
        </div>
      </div>
    </div>

    <!-- ====== 全局 tooltip ====== -->
    <div
      class="chip-tooltip"
      :class="{ show: tooltip.visible }"
      :style="{ left: tooltip.x + 'px', top: tooltip.y + 'px' }"
    >
      {{ tooltip.text }}
      <span class="chip-tooltip-arrow"></span>
    </div>

    <!-- ====== 全局 toast 通知（rizui_toast，右上角） ====== -->
    <rizui_toast
      v-if="toast.show"
      :type="toast.type"
      :title="toast.title"
      :hint="toast.hint"
      @close="toast.show = false"
    />

    <!-- ====== 密码锁遮罩（启用密码保护时覆盖整个配置界面） ====== -->
    <div v-if="security.enabled && !security.unlocked" class="security-lock">
      <rizui_card title="配置已锁定" desc="请输入密码解锁配置面板" icon="fa-solid fa-lock">
        <rizui_text
          v-model="lockPassword"
          type="password"
          placeholder="输入密码"
          :color="'#66ccff'"
          style="text-align: left;"
          @enter="doUnlock"
        />
        <p v-if="security.error" class="security-error">{{ security.error }}</p>
        <div class="security-actions">
          <rizui_button text="解锁" primary :color="'#66ccff'" @click="doUnlock" :disabled="security.busy || !lockPassword" />
          <rizui_button text="忘记密码" danger @click="forgetDialog = true" :disabled="security.busy" />
        </div>
      </rizui_card>
    </div>

    <!-- 忘记密码确认 -->
    <rizui_dialog v-model:show="forgetDialog" title="忘记密码" :color="'#66ccff'" :mask-close="false">
      <p>此操作将<strong>清除所有配置</strong>（密码、名单、班级、自定义资源），且不可恢复。确定继续？</p>
      <template #footer>
        <rizui_button text="取消" @click="forgetDialog = false" />
        <rizui_button text="清除所有配置" danger @click="confirmForget" />
      </template>
    </rizui_dialog>
  </div>
</template>

<script setup>
/*
 *  ConfigPanel.vue — 纯编排组件（视图层）。
 *
 *  所有 JS 逻辑（状态管理、IPC 操作、Tab 导航）均在 useConfigPanel.js 中实现。
 *  本文件仅负责：
 *    1. 导入子组件和逻辑层
 *    2. 解构 useConfigPanel 返回的状态和方法
 *    3. 在模板中绑定 props / emits / 事件
 *
 *  如需修改业务逻辑，请编辑 src/renderer/composables/useConfigPanel.js。
 */
import TabRoster   from './TabRoster.vue'
import TabFloating from './TabFloating.vue'
import TabResult   from './TabResult.vue'
import TabAdvanced from './TabAdvanced.vue'
import TabAbout    from './TabAbout.vue'
import TabLogs     from './TabLogs.vue'
import { computed, ref } from 'vue'
import { rizui_toast, rizui_card, rizui_text, rizui_button, rizui_dialog } from 'riz-ui'
import { useConfigPanel } from '../composables/useConfigPanel.js'

const {
  /* Tab 导航 */
  tabs, activeTab, tabBarRef, sliderLeft, sliderWidth, currentTab,
  updateSlider, switchTab,

  /* 全局状态 */
  draft, appInfo,

  /* 芯片 tooltip */
  tooltip, showChipTooltip, hideChipTooltip,

  /* IPC 操作 */
  openConfigFile, openConfigDir, clearCache, adminElevate, appRestart,
  createStartupTask, resetConfig, showInExplorer,

  /* 更新检查 */
  updateLoading, updateStatus, updateTitle, updateDetail, updateReleaseUrl, checkUpdate,

  /* 关闭与动画 */
  isClosing, closeWithAnimation, handleCancel, handleApply, saveVersion,
  loading,

  /* 全局 toast 通知 */
  toast,

  /* 密码保护（安全管理） */
  security, unlock, forgetReset, refreshSecurity,

  /* 主题色跟随样式 */
  panelBorderStyle, applyBtnStyle, sliderStyle, trackBorderStyle, tabItemStyle
} = useConfigPanel()

/* DevTools 快捷打开 */
function openDevTools(target) {
  if (window.configPanelApi?.openDevTools) {
    window.configPanelApi.openDevTools(target)
  }
}

/* ---- 密码锁：遮罩输入与忘记密码 ---- */
const lockPassword = ref('')
const forgetDialog = ref(false)

function doUnlock() {
  unlock(lockPassword.value)
}

async function confirmForget() {
  forgetDialog.value = false
  lockPassword.value = ''
  await forgetReset()
}

/* 注入 CSS 变量供全局控件跟随主题色 */
function hexToRgba(hex, alpha) {
  const r = parseInt(hex.slice(1, 3), 16)
  const g = parseInt(hex.slice(3, 5), 16)
  const b = parseInt(hex.slice(5, 7), 16)
  return `rgba(${r}, ${g}, ${b}, ${alpha})`
}
const rootStyle = computed(() => {
  const c = currentTab.value?.color || '#66ccff'
  return {
    '--theme-color': c,
    '--theme-scrollbar': hexToRgba(c, 0.45),
    '--theme-scrollbar-hover': hexToRgba(c, 0.75),
    '--theme-footer-bg': hexToRgba(c, 0.12),
  }
})
</script>

<style scoped>
/*
 *  本文件 CSS 为 scoped（Vue 自动添加 data-v-xxx 属性隔离），
 *  但 ::-webkit-scrollbar 等伪元素不受 scoped 影响（浏览器全局生效）。
 *  按功能分为 7 个分组。
 *
 *  z-index 层叠方案（均在 .config-layout 内）：
 *    .bg-decoration  (z:1, absolute, bg:#fff) ← 泡泡白色画布，DOM 最先
 *    .config-left    (z:1, relative)          ← 覆盖在泡泡上，rgba 0.4
 *    .config-right   (z:1, relative)          ← 覆盖在泡泡上，rgba 0.4
 *    三者同 z-index，靠 DOM 顺序自然堆叠。
 */

/* =================================================================
   1. 全局滚动条 —— 蔚蓝档案主题蓝色椭圆滑块
   注意：::-webkit 伪元素不受 Vue scoped 限制，会影响整个窗口
   ================================================================= */
::-webkit-scrollbar {
  width: 6px;
  height: 6px;
}
::-webkit-scrollbar-track {
  background: transparent;
}
::-webkit-scrollbar-thumb {
  background: var(--theme-scrollbar, rgba(102, 204, 255, 0.45));
  border-radius: 3px;
}
::-webkit-scrollbar-thumb:hover {
  background: var(--theme-scrollbar-hover, rgba(102, 204, 255, 0.75));
}
::-webkit-scrollbar-button {
  display: none;
}
::-webkit-scrollbar-corner {
  background: transparent;
}

/* =================================================================
   2. 双栏布局 + 背景泡泡
   ================================================================= */

/*
 *  全窗口 flex 容器。
 *  background: #e8ecf1 为最底层底色。
 */
.config-layout {
  position: relative;
  display: flex;
  width: 100%;
  height: 100%;
  background: #e8ecf1;
  font-family: 'UI', 'Bahnschrift', 'Microsoft YaHei UI', sans-serif;
  overflow: hidden;
}

/*
 *  左侧：配置面板（Tab 导航 + 内容）。
 *  z-index: 1 与 bg-decoration 同级，DOM 在后故覆盖其上。
 *  rgba 0.4 透明度使下方泡泡隐约透出。
 */
.config-left {
  position: relative;
  z-index: 1;
  flex: 1.5;
  display: flex;
  flex-direction: column;
  background: rgba(255, 255, 255, 0.4);
  border-right: 1px solid rgba(232, 232, 240, 0.8);
  overflow: hidden;
}

/*
 *  右侧：运行日志 + 底部按钮。
 *  rgba 0.4 透明度与左侧一致，保证泡泡可见度均匀。
 */
.config-right {
  position: relative;
  z-index: 1;
  flex: 1;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  background: rgba(250, 251, 252, 0.4);
}

/*
 *  背景泡泡容器。
 *  绝对定位覆盖全窗口，z-index: 1 与面板同级，DOM 最先 → 最底层。
 *  background: #fff 提供白色画布，泡泡浮动其上，透过半透明面板可见。
 */
.bg-decoration {
  position: absolute;
  inset: 0;
  pointer-events: none;
  overflow: hidden;
  z-index: 1;
  background: #fff;
}
/*
 *  泡泡个体：10 个尺寸相近（180-200px）的浮动圆。
 *  动画周期 34-42s，opacity 0.10-0.16。
 *  主题色由 --theme-color CSS 变量注入（跟随当前 Tab）。
 */
.bg-circle {
  position: absolute;
  border-radius: 50%;
  background: var(--theme-color, #66ccff);
  opacity: 0;
  animation: float-circle var(--dur, 12s) ease-in-out infinite;
  animation-delay: var(--delay, 0s);
}
.c1  { width: 190px; height: 190px; top: 5%;   left: 5%;   --dur: 38s; --delay: 0s;    opacity: 0.1; }
.c2  { width: 180px; height: 180px; top: 55%;  left: 75%;  --dur: 35s; --delay: -5s;   opacity: 0.16; }
.c3  { width: 200px; height: 200px; top: 72%;  left: 8%;   --dur: 40s; --delay: -10s;  opacity: 0.15; }
.c4  { width: 185px; height: 185px; top: 20%;  left: 60%;  --dur: 36s; --delay: -15s;  opacity: 0.16; }
.c5  { width: 195px; height: 195px; top: 38%;  left: 35%;  --dur: 42s; --delay: -7s;   opacity: 0.15; }
.c6  { width: 190px; height: 190px; top: 82%;  left: 48%;  --dur: 34s; --delay: -12s;  opacity: 0.16; }
.c7  { width: 180px; height: 180px; top: 15%;  left: 85%;  --dur: 38s; --delay: -3s;   opacity: 0.16; }
.c8  { width: 200px; height: 200px; top: 48%;  left: 15%;  --dur: 41s; --delay: -9s;   opacity: 0.15; }
.c9  { width: 185px; height: 185px; top: 60%;  left: 52%;  --dur: 37s; --delay: -14s;  opacity: 0.16; }
.c10 { width: 195px; height: 195px; top: 30%;  left: 78%;  --dur: 39s; --delay: -6s;   opacity: 0.16; }

@keyframes float-circle {
  0%   { transform: translate(0, 0) scale(1); }
  20%  { transform: translate(100px, -80px) scale(1.1); }
  40%  { transform: translate(-70px, 90px) scale(0.9); }
  60%  { transform: translate(-110px, -40px) scale(1.08); }
  80%  { transform: translate(50px, 60px) scale(0.95); }
  100% { transform: translate(0, 0) scale(1); }
}

/* =================================================================
   3. Tab 胶囊滑块导航
   ================================================================= */

/*
 *  导轨：白色药丸形，28px 高。
 *  border-color 由 trackBorderStyle 跟随当前 Tab 主题色。
 */
.tab-bar {
  position: relative;
  display: flex;
  align-items: center;
  margin: 20px auto 20px;
  padding: 0 6px;
  gap: 0;
  width: min(480px, calc(100% - 40px));
  box-sizing: border-box;
  height: 28px;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.95);
  border: 1.5px solid rgba(0, 0, 0, 0.12);
  transition: border-color 0.3s;
}

/*
 *  滑块：比导轨略大（±3px），微悬浮效果。
 *  left/width 由 JS 动态计算，0.45s cubic-bezier 平滑过渡。
 */
.tab-slider {
  position: absolute;
  top: -3px;
  bottom: -3px;
  border-radius: 999px;
  transition:
    left 0.45s cubic-bezier(0.25, 0, 0.25, 1),
    width 0.45s cubic-bezier(0.25, 0, 0.25, 1),
    background 0.35s;
  z-index: 1;
  box-shadow: 0 2px 10px rgba(0, 0, 0, 0.12);
}

/*
 *  Tab 按钮：覆盖在滑块上方（z-index: 2）。
 *  color 由 JS 动态设置（未选中=主题色, 选中=白色）。
 */
.tab-item {
  flex: 1 1 0;
  min-width: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 0;
  white-space: nowrap;
  border: none;
  background: transparent;
  cursor: pointer;
  position: relative;
  z-index: 2;
  font-family: 'UI', 'Bahnschrift', 'Microsoft YaHei UI', sans-serif;
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.2px;
  color: inherit;
  outline: none;
  -webkit-tap-highlight-color: transparent;
  transition: color 0.2s 0s;
}

/* 图标 + 文字的水平排列 */
.tab-label {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  color: inherit;
}

/*
 *  未选中状态：文字隐藏（max-width: 0 + overflow: hidden）。
 *  选中状态（.show）：文字展开（max-width: 64px），延迟 0.45s 等滑块到位。
 */
.tab-text {
  display: inline-block;
  max-width: 0;
  overflow: hidden;
  white-space: nowrap;
  opacity: 0;
  color: inherit;
  transition:
    max-width 0.15s ease 0s,
    opacity 0.15s ease 0s,
    color 0.15s 0s;
}

.tab-text.show {
  max-width: 64px;
  opacity: 1;
  transition:
    max-width 0.5s ease 0.45s,
    opacity 0.5s ease 0.45s,
    color 0.5s 0.45s;
}

/* SVG 图标尺寸和线条样式 */
.tab-icon-svg :deep(svg) {
  display: block;
  width: 14px;
  height: 14px;
  fill: none;
  stroke: currentColor;
  stroke-width: 2;
  stroke-linecap: round;
  stroke-linejoin: round;
}

/* =================================================================
   4. 内容区
   ================================================================= */
.config-body {
  flex: 1;
  overflow-y: auto;
  padding: 24px 32px 16px 32px;
}

/* =================================================================
   5. 加载骨架
   ================================================================= */
.loading-skeleton {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 12px;
  color: #99a;
  font-size: 13px;
}

/* 旋转动画的圆形 spinner */
.loading-spinner {
  width: 28px;
  height: 28px;
  border: 3px solid #e8ecf2;
  border-top-color: #66ccff;
  border-radius: 50%;
  animation: spin 0.7s linear infinite;
}

@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}



/* =================================================================
   6. 全局 tooltip
   ================================================================= */

/*
 *  fixed 定位，z-index: 99999，不受任何父容器裁切。
 *  默认透明（opacity: 0），.show 时淡入。
 *  箭头（.chip-tooltip-arrow）指向触发元素。
 */
.chip-tooltip {
  position: fixed;
  padding: 5px 10px;
  border-radius: 8px;
  background: rgba(30, 36, 48, 0.9);
  color: #fff;
  font-size: 11px;
  font-weight: 500;
  white-space: nowrap;
  pointer-events: none;
  opacity: 0;
  transform: translateY(3px);
  z-index: 99999;
  transition:
    opacity 0.18s,
    transform 0.18s;
}

.chip-tooltip.show {
  opacity: 1;
  transform: translateY(0);
}

.chip-tooltip-arrow {
  position: absolute;
  top: -8px;
  left: 50%;
  transform: translateX(-50%);
  border: 5px solid transparent;
  border-bottom-color: rgba(30, 36, 48, 0.9);
}

/* =================================================================
   7. 底部操作区
   ================================================================= */
.config-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 6px 12px 8px;
  background: var(--theme-footer-bg, rgba(102, 204, 255, 0.885));
}

/* 左侧：DevTools 标签 + 按钮纵向排列 */
.footer-left {
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.devtools-label {
  font-size: 10px;
  color: #bbc;
}
.devtools-bar {
  display: flex;
  gap: 4px;
}
.devtools-btn {
  padding: 2px 8px;
  border: 1px solid #e0e4e8;
  border-radius: 999px;
  background: #fff;
  font-size: 10px;
  color: #99a;
  cursor: pointer;
  font-family: inherit;
  transition: all 0.2s;
}
.devtools-btn:hover {
  border-color: #66ccff;
  color: #66ccff;
}

/* 右侧：圆形按钮（跨左侧两行高度，由父级 align-items:center 居中） */
.footer-actions {
  display: flex;
  gap: 8px;
  flex-shrink: 0;
}
.action-btn {
  width: 36px;
  height: 36px;
  border-radius: 50%;
  border: none;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 5px;
  cursor: pointer;
  transition: filter 0.2s, background 0.35s;
}
.action-btn:hover {
  filter: brightness(1.15);
}
.action-btn:active {
  filter: brightness(0.9);
}
.action-cancel {
  background: #ff8c8c;
  color: #fff;
}
.action-apply {
  background: #66ccff;
  color: #fff;
  width: 80px;
  border-radius: 999px;
}

.action-btn-apply-text {
  font-size: 12px;
  font-weight: 600;
  line-height: 1;
  color: inherit;
  font-family: inherit;
}

/* ===== 密码锁遮罩 ===== */
.security-lock {
  position: absolute;
  inset: 0;
  z-index: 999;
  display: flex;
  align-items: center;
  justify-content: center;
  background: rgba(232, 236, 241, 0.88);
  backdrop-filter: blur(4px);
}
.security-lock > * {
  width: 340px;
}
.security-error {
  color: #d44;
  font-size: 12px;
  margin: 8px 0 0;
}
.security-actions {
  display: flex;
  gap: 8px;
  margin-top: 14px;
  justify-content: flex-end;
}
</style>
