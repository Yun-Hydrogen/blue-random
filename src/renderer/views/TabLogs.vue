<!--
================================================================================
  组件：TabLogs.vue
  所属：配置面板 — 日志输出 Tab
  父组件：ConfigPanel.vue（通过 props 传入应用信息）

================================================================================
  一、功能概述
================================================================================

  1. 省电懒加载 —— 默认显示毛玻璃遮罩层并暂停轮询，点击按钮才激活轮询，
     大幅降低 Web 端启动与待机时的性能与 IO 开销。

  2. 日志排序与展示 —— 严格按时间正序展示（最新日志在最下方），新日志追加至底部
     并自动跟随平滑滚动至最底端。

  3. 徽标区分与版本跟随 —— 版本号跟随 package.json，管理员徽标与 UIAccess 徽标
     采用鲜明独立的主题渐变色予以视觉区分。

  4. 重载与清空 —— 提供外部 ref 调用的 reload() 与 clear() 接口，支持底部工具栏直连。

================================================================================
-->

<template>
  <div class="tab-page">
    <div class="card" style="position: relative;">
      <!-- 日志头部栏 -->
      <div class="log-head">
        <span class="log-head-title">运行日志</span>
        <span class="log-badge log-badge-admin" v-if="appInfo.isAdmin">管理员</span>
        <span class="log-badge log-badge-uia" v-if="appInfo.isUiAccess">UIAccess</span>
        <span class="log-badge log-badge-ver">v{{ appInfo.version || pkg.version }}</span>
        <div class="log-head-spacer"></div>
        <button class="log-clear-btn" @click="clearLogs" :disabled="!isLoaded">清空</button>
      </div>

      <!-- 日志列表区 -->
      <div class="log-list" ref="logListRef">
        <div v-if="logs.length === 0" class="log-empty">
          {{ isLoaded ? '暂无日志' : '日志尚未加载' }}
        </div>
        <div
          v-for="item in logs"
          :key="item.id"
          class="log-row"
          :class="'log-' + item.level"
        >
          <span class="log-time">{{ item.time }}</span>
          <span class="log-msg">{{ item.text }}</span>
        </div>
      </div>

      <!-- 懒加载遮罩层 (省电就绪模式) -->
      <Transition name="fade">
        <div v-if="!isLoaded" class="log-mask-overlay">
          <div class="log-mask-content">
            <i class="fa-solid fa-terminal log-mask-icon"></i>
            <div class="log-mask-title">未加载日志</div>
            <div class="log-mask-desc">点击下方按钮加载日志</div>
            <rizui_button text="加载日志" primary @click="loadLogs" r-icon="fa-solid fa-play"/>
          </div>
        </div>
      </Transition>
    </div>
  </div>
</template>

<script setup>
import { ref, nextTick, onMounted, onBeforeUnmount } from 'vue'
import pkg from '../../../package.json'
import { rizui_button } from 'riz-ui'

defineProps({
  appInfo: {
    type: Object,
    default: () => ({})
  }
})

// ================================================================
//  配置常量与状态
// ================================================================

const POLL_INTERVAL = 800
const MAX_DISPLAY = 300

const isLoaded = ref(false)
const logs = ref([])
const logListRef = ref(null)

let lastLogId = ''
let pollTimer = null
let isPolling = false

// ================================================================
//  滚动与日志处理
// ================================================================

function scrollToBottom(smooth = false) {
  nextTick(() => {
    if (logListRef.value) {
      logListRef.value.scrollTo({
        top: logListRef.value.scrollHeight,
        behavior: smooth ? 'smooth' : 'auto'
      })
    }
  })
}

function formatEntry(entry) {
  let displayTime = '--:--:--'
  if (entry.time) {
    if (/^\d{2}:\d{2}:\d{2}/.test(entry.time)) {
      displayTime = entry.time.slice(0, 8)
    } else {
      const d = new Date(entry.time)
      displayTime = isNaN(d.getTime())
        ? (entry.time.length <= 12 ? entry.time : '--:--:--')
        : d.toLocaleTimeString('zh-CN', { hour12: false })
    }
  }
  return {
    id: entry.id,
    level: entry.level || 'info',
    text: entry.text || '',
    time: displayTime
  }
}

async function pollLogs() {
  if (isPolling) return
  if (!window.configPanelApi?.getLogs) return

  isPolling = true
  try {
    const entries = await window.configPanelApi.getLogs(500)
    if (!Array.isArray(entries) || entries.length === 0) return

    // 后端已保证 entries 为时间正序（老日志在前，最新在后）
    if (logs.value.length === 0 || !lastLogId) {
      const formatted = entries.map(formatEntry)
      logs.value = formatted.slice(-MAX_DISPLAY)
      lastLogId = logs.value[logs.value.length - 1]?.id || ''
      scrollToBottom(false)
    } else {
      const lastIdx = entries.findIndex(e => e.id === lastLogId)
      if (lastIdx !== -1) {
        const newEntries = entries.slice(lastIdx + 1)
        if (newEntries.length > 0) {
          const formatted = newEntries.map(formatEntry)
          logs.value.push(...formatted)
          if (logs.value.length > MAX_DISPLAY) {
            logs.value.splice(0, logs.value.length - MAX_DISPLAY)
          }
          lastLogId = logs.value[logs.value.length - 1]?.id || ''
          scrollToBottom(true)
        }
      } else {
        // 如果断层（如日志被清空或重写），全量重载
        const formatted = entries.map(formatEntry)
        logs.value = formatted.slice(-MAX_DISPLAY)
        lastLogId = logs.value[logs.value.length - 1]?.id || ''
        scrollToBottom(false)
      }
    }
  } catch (_) {
    // 轮询异常静默恢复
  } finally {
    isPolling = false
  }
}

function loadLogs() {
  isLoaded.value = true
  pollLogs()
  if (!pollTimer) {
    pollTimer = setInterval(pollLogs, POLL_INTERVAL)
  }
}

function reloadLogs() {
  isLoaded.value = true
  lastLogId = ''
  logs.value = []
  pollLogs()
  if (!pollTimer) {
    pollTimer = setInterval(pollLogs, POLL_INTERVAL)
  }
}

function clearLogs() {
  logs.value = []
  lastLogId = ''
  const now = new Date().toLocaleTimeString('zh-CN', { hour12: false })
  logs.value.push({
    id: `clear-${Date.now()}`,
    level: 'info',
    text: '日志已清空',
    time: now
  })
  scrollToBottom(false)
}

// 暴露给父组件调用
defineExpose({
  load: loadLogs,
  reload: reloadLogs,
  clear: clearLogs
})

onMounted(() => {
  // 保持懒加载，不默认开启轮询，等待用户激活
})

onBeforeUnmount(() => {
  if (pollTimer) {
    clearInterval(pollTimer)
    pollTimer = null
  }
})
</script>

<style scoped>
.tab-page {
  height: 100%;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

::-webkit-scrollbar {
  width: 6px;
  height: 6px;
}
::-webkit-scrollbar-track {
  background: transparent;
}
::-webkit-scrollbar-thumb {
  background: rgba(153, 170, 187, 0.35);
  border-radius: 3px;
}
::-webkit-scrollbar-thumb:hover {
  background: rgba(153, 170, 187, 0.55);
}
::-webkit-scrollbar-button {
  display: none;
}
::-webkit-scrollbar-corner {
  background: transparent;
}

/* ===== 卡片 ===== */
.card {
  flex: 1;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  padding: 14px 16px;
  background: rgba(251, 251, 251, 0.6);
  border: 1px solid #e8ecf2;
  border-radius: 12px;
}

/* ===== 日志头部（标题 + 状态徽章 + 清空按钮） ===== */
.log-head {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 8px;
}
.log-head-title {
  font-size: 14px;
  font-weight: 700;
  color: #2b3a4a;
}
.log-head-spacer {
  flex: 1;
}

.log-badge {
  display: inline-flex;
  align-items: center;
  padding: 2px 8px;
  border-radius: 999px;
  font-size: 10px;
  font-weight: 600;
  color: #fff;
  line-height: 1.4;
  letter-spacing: 0.5px;
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.08);
}
.log-badge-admin {
  background: linear-gradient(135deg, #f59e0b, #ea580c);
}
.log-badge-uia {
  background: linear-gradient(135deg, #10b981, #059669);
}
.log-badge-ver {
  background: #8899a6;
}

.log-clear-btn {
  padding: 3px 12px;
  border: 1px solid #d0d6dc;
  border-radius: 999px;
  background: #fff;
  font-size: 11px;
  color: #667788;
  cursor: pointer;
  font-family: inherit;
  transition: all 0.2s;
}
.log-clear-btn:hover:not(:disabled) {
  border-color: #66ccff;
  color: #3399ff;
}
.log-clear-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

/* ===== 日志列表 ===== */
.log-list {
  flex: 1;
  overflow-y: auto;
  font-size: 12px;
  font-family: 'UI', 'Bahnschrift', 'Microsoft YaHei UI', sans-serif;
  user-select: text;
  padding: 4px 6px;
  scroll-behavior: smooth;
}
.log-empty {
  color: #a0aec0;
  font-size: 13px;
  text-align: center;
  padding: 40px 0;
}
.log-row {
  display: flex;
  align-items: baseline;
  gap: 8px;
  padding: 6px 12px;
  margin: 6px 0;
  background: #fff;
  border-radius: 8px;
  border: 1.5px solid #e0e4e8;
  box-shadow: 0 1px 2px rgba(0, 0, 0, 0.03);
}

/* 各级别边框颜色 */
.log-info    { border-color: #99ccdd; }
.log-warn    { border-color: #e8a840; }
.log-error   { border-color: #e05555; }
.log-success { border-color: #55b888; }

.log-time {
  white-space: nowrap;
  flex-shrink: 0;
  font-size: 11px;
  font-weight: 500;
  color: #94a3b8;
}
.log-info .log-time    { color: #0284c7; }
.log-warn .log-time    { color: #d97706; }
.log-error .log-time   { color: #dc2626; }
.log-success .log-time { color: #16a34a; }

.log-msg {
  word-break: break-all;
  color: #334155;
  line-height: 1.5;
}
.log-warn .log-msg    { color: #92400e; }
.log-error .log-msg   { color: #b91c1c; }
.log-success .log-msg { color: #166534; }

/* ===== 遮罩层 (省电就绪模式) ===== */
.log-mask-overlay {
  position: absolute;
  inset: 0;
  background: rgba(255, 255, 255, 0.78);
  backdrop-filter: blur(5px);
  -webkit-backdrop-filter: blur(5px);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 10;
  border-radius: 12px;
}
.log-mask-content {
  display: flex;
  flex-direction: column;
  align-items: center;
  text-align: center;
  padding: 24px;
  max-width: 320px;
}
.log-mask-icon {
  font-size: 32px;
  color: #66ccff;
  margin-bottom: 12px;
  opacity: 0.9;
}
.log-mask-title {
  font-size: 14px;
  font-weight: 700;
  color: #1e293b;
  margin-bottom: 6px;
}
.log-mask-desc {
  font-size: 12px;
  color: #64748b;
  line-height: 1.5;
  margin-bottom: 18px;
}

.log-mask-btn:hover {
  transform: translateY(-1px);
  box-shadow: 0 6px 16px rgba(51, 153, 255, 0.45);
}
.log-mask-btn:active {
  transform: translateY(1px);
}

.fade-enter-active,
.fade-leave-active {
  transition: opacity 0.25s ease;
}
.fade-enter-from,
.fade-leave-to {
  opacity: 0;
}
</style>
