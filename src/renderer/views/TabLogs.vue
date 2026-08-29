/*
================================================================================
  组件：TabLogs.vue
  所属：配置面板 — 日志输出 Tab
  父组件：ConfigPanel.vue（通过 props 传入应用信息）

================================================================================
  一、功能概述
================================================================================

  1. 日志拉取 —— 通过 IPC 轮询主进程的磁盘日志文件（log.txt）
     替代了旧的 SSE 方案（SSE 服务端 config-server.js 已于 2026-06-28 删除）

  2. 日志展示 —— 按时间倒序显示，最新日志在最上方

  3. 日志清空 —— 一键清除当前显示的所有日志（仅清空前端数组，不影响磁盘文件）

================================================================================
  二、为什么用 IPC 轮询替代 SSE
================================================================================

  旧版问题：
    - SSE 端点 /api/logs 由 config-server.js（HTTP 服务器）提供
    - config-server.js 已于 2026-06-28 删除（被原生 IPC 配置面板取代）
    - 管理员模式下，新进程有独立内存空间，in-memory buffer 不共享

  新方案：
    ┌──────────────┐   每 800ms 轮询     ┌──────────────┐
    │  TabLogs.vue  │ ──configPanelApi──→ │  主进程       │
    │  (渲染进程)    │ ←───getLogs()────── │  logging.js   │
    │               │                    │  ↓            │
    │  logs.value   │   返回日志数组      │  readFile()   │
    │  = 最新 200 条 │                    │  log.txt      │
    └──────────────┘                    └──────────────┘

  轮询参数：
    - 间隔：800ms（足够实时，不会造成 CPU 负担）
    - 每次拉取最近 500 条（后端限制）
    - 前端最多显示 200 条（UI 性能考虑）
    - 通过 lastLogId 去重，避免重复渲染

  竞态保护：
    - 主进程端：读取前等待所有待处理写入完成（Promise 链互斥锁）
    - 渲染端：通过 lastLogId 比对，只更新增量日志
    - 轮询回调只保留最新一次的结果（忽略过期响应）

================================================================================
  三、日志级别与颜色对照
================================================================================

  level   | 时间颜色   | 消息颜色
  ────────┼────────────┼──────────
  info    | #99ccdd 青 | #556 深灰
  warn    | #e8a840 琥珀| #a07030 棕
  error   | #e05555 红 | #c04040 红
  success | #55b888 绿 | #3a8050 绿

================================================================================
  四、维护注意事项
================================================================================

  - 日志轮询间隔（POLL_INTERVAL）可调整，但不应低于 500ms
  - 前端最多显示 200 条（MAX_DISPLAY），超出自动截断
  - 清空按钮只清空前端显示，不影响磁盘日志文件
  - 组件卸载时必须 clearInterval 停止轮询
  - 如果 preload API 不可用（非 Electron 环境），静默降级显示"暂无日志"

  最后更新：2026-06-28
================================================================================
*/

<template>
  <div class="tab-page">
    <div class="card">
      <div class="log-head">
        <span class="log-head-title">运行日志</span>
        <span class="log-badge" v-if="appInfo.isAdmin">管理员</span>
        <span class="log-badge" v-if="appInfo.isUiAccess">UIAccess</span>
        <span class="log-badge log-badge-ver">v{{ appInfo.version }}</span>
        <div class="log-head-spacer"></div>
        <button class="log-clear-btn" @click="clearLogs">清空</button>
      </div>
      <div class="log-list">
        <div v-if="logs.length === 0" class="log-empty">暂无日志</div>
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
    </div>
  </div>
</template>

<script setup>
/*
 *  组件逻辑概览（按代码顺序）：
 *  1. props       —— 接收父组件传入的应用信息
 *  2. 配置常量    —— 轮询间隔、最大显示条数
 *  3. 日志存储    —— 日志数组的管理（增/截断/清空）
 *  4. IPC 轮询    —— 定时拉取磁盘日志文件
 *  5. 清空日志    —— 清除前端显示（不影响磁盘文件）
 *  6. 生命周期    —— 挂载时开始轮询，卸载时停止
 */
import { ref, onMounted, onBeforeUnmount } from 'vue'

/*
 *  props：父组件传入的数据（只读，本组件不修改）
 *  appInfo.isAdmin    —— 当前是否以管理员权限运行
 *  appInfo.isUiAccess —— 是否启用了 UIAccess 置顶增强
 *  appInfo.version    —— 应用版本号
 */
defineProps({ appInfo: Object })

// ================================================================
//  1. 配置常量
// ================================================================

/*
 *  POLL_INTERVAL —— 轮询间隔（毫秒）
 *
 *  800ms 是经验和折中：
 *    - 太短（< 500ms）→ CPU 空转，日志窗口本身不需要毫秒级实时
 *    - 太长（> 2s）  → 用户感知到延迟，体验下降
 *    - 800ms 足够实时，且 CPU 占用几乎为零
 */
const POLL_INTERVAL = 800

/*
 *  MAX_DISPLAY —— 前端最多显示的日志条数
 *
 *  200 条足够覆盖最近几分钟的日志（正常应用每秒 ~1-3 条日志）。
 *  超出时自动丢弃最旧的（数组尾部）。
 */
const MAX_DISPLAY = 200

// ================================================================
//  2. 日志存储与管理
// ================================================================

/*
 *  logs —— 日志数组（响应式），每条日志包含：
 *    id    : 唯一标识（来自主进程生成的 ISO 时间戳 + 随机 hex）
 *    level : 日志级别（'info' | 'warn' | 'error'）
 *    text  : 日志正文
 *    time  : 格式化时间字符串（HH:mm:ss）
 *
 *  使用 ref([]) 而非 reactive，因为整个数组会被替换。
 */
const logs = ref([])

/*
 *  lastLogId —— 上次轮询时最新日志的 ID
 *
 *  用于增量更新：下次轮询时，只取 ID 不等于 lastLogId 的新日志。
 *  避免重复添加已有日志，减少不必要的 Vue 响应式触发。
 *
 *  初始为空字符串，首次轮询时加载所有日志。
 */
let lastLogId = ''

/*
 *  pollTimer —— setInterval 返回的定时器 ID
 *
 *  存储在模块作用域以便 onBeforeUnmount 中 clearInterval。
 */
let pollTimer = null

// ================================================================
//  3. IPC 轮询 —— 定时从主进程拉取日志文件
// ================================================================

/*
 *  pollLogs() —— 执行一次日志拉取
 *
 *  流程：
 *    1. 检查 API 可用性（非 Electron 环境静默跳过）
 *    2. 调用 configPanelApi.getLogs(500) 拉取最近 500 条
 *    3. 按 id 去重：只保留 id !== lastLogId 的新日志
 *    4. 更新 lastLogId 为最新日志的 id
 *    5. 将新日志追加到 logs 头部（unshift）
 *    6. 截断超出 MAX_DISPLAY 的旧日志
 *
 *  竞态保护：
 *    - 主进程端（logging.js）在读取 log.txt 前等待所有待处理写入完成
 *    - 本函数是同步风格（async/await），每次只运行一个实例
 *    - 如果上次 pollLogs 尚未完成，新的 setInterval 回调会在事件队列中等待，
 *      不会并发执行（JS 单线程）。但如果上次请求卡住 >800ms，
 *      可能导致多个请求排队。解决方案：用 isPolling 标志跳过排队请求。
 *
 *  错误处理：
 *    - API 不可用 → 静默跳过
 *    - 网络/ IPC 错误 → try/catch 静默恢复
 */
let isPolling = false

async function pollLogs() {
  /* 跳过并发请求（上一次还未完成） */
  if (isPolling) return
  if (!window.configPanelApi?.getLogs) return

  isPolling = true
  try {
    const entries = await window.configPanelApi.getLogs(500)
    if (!Array.isArray(entries) || entries.length === 0) return

    /*
     * 增量更新：只保留上次未见过的新日志。
     *
     * 比较逻辑：
     *   entries 已按时间倒序（最新在前），从头部开始遍历。
     *   遇到 id === lastLogId 时停止（说明后面的都见过了）。
     *   收集所有新条目后，更新 lastLogId 为 entries[0].id。
     *
     * 首次轮询：lastLogId === ''，所以 entries[0].id !== '' 为 true，
     *   会加载所有日志。加载完后 lastLogId 被设为 entries[0].id。
     */
    const newEntries = []
    for (const entry of entries) {
      if (entry.id === lastLogId) break
      newEntries.push(entry)
    }

    if (newEntries.length === 0) return

    /* 更新 lastLogId 为最新日志的 ID */
    lastLogId = entries[0].id

    /*
     * 格式化时间 + 追加到前端数组
     *
     * 主进程存储的 time 是 ISO 8601 格式（如 "2026-06-28T12:00:00.000Z"），
     * 这里转为本地时间 HH:mm:ss 显示。
     */
    for (const entry of newEntries) {
      const displayTime = entry.time
        ? new Date(entry.time).toLocaleTimeString('zh-CN', { hour12: false })
        : '--:--:--'
      logs.value.unshift({
        id: entry.id,
        level: entry.level || 'info',
        text: entry.text || '',
        time: displayTime
      })
    }

    /* 截断超出上限的旧日志（从尾部移除） */
    if (logs.value.length > MAX_DISPLAY) {
      logs.value.length = MAX_DISPLAY
    }
  } catch (_) {
    /* IPC 调用失败（如主进程未就绪），静默恢复，下次轮询再试 */
  } finally {
    isPolling = false
  }
}

// ================================================================
//  4. 清空日志
// ================================================================

/*
 *  clearLogs() —— 清除前端显示的所有日志
 *
 *  注意：只清空 logs 数组，不影响磁盘 log.txt。
 *        清空后重置 lastLogId，确保后续轮询能加载新日志。
 *        添加一条"日志已清空"的提示记录。
 */
function clearLogs() {
  logs.value = []
  lastLogId = ''
  /* 添加一条清空提示（level: 'info'，时间是当前时间） */
  const now = new Date().toLocaleTimeString('zh-CN', { hour12: false })
  logs.value.unshift({
    id: `clear-${Date.now()}`,
    level: 'info',
    text: '日志已清空',
    time: now
  })
}

// ================================================================
//  5. 生命周期钩子
// ================================================================

/*
 *  onMounted —— 组件挂载后立即开始轮询
 *
 *  先执行一次 pollLogs() 立即加载已有日志（不等第一个 800ms）。
 *  然后每 800ms 轮询一次。
 */
onMounted(() => {
  pollLogs()
  pollTimer = setInterval(pollLogs, POLL_INTERVAL)
})

/*
 *  onBeforeUnmount —— 组件卸载前停止轮询
 *
 *  必须 clearInterval，否则定时器会继续运行并尝试更新已销毁的组件，
 *  导致内存泄漏和控制台错误。
 */
onBeforeUnmount(() => {
  if (pollTimer) {
    clearInterval(pollTimer)
    pollTimer = null
  }
})
</script>

<style scoped>
/*
 *  本组件完整样式表
 *  主题色：灰蓝 #99aabb
 *  所有颜色直接硬编码，不使用 CSS 变量以提升性能
 */

/* ===== 全局动画 ===== */
.tab-page {
  height: 100%;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

/* ===== 滚动条（主题色 #99aabb） ===== */
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
  background: rgb(251, 251, 251,0.5);
  border: 1px solid #e8ecf2;
}

/* ===== 日志头部（标题 + 状态徽章 + 清空按钮） ===== */
.log-head {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-bottom: 8px;
}
.log-head-title {
  font-size: 14px;
  font-weight: 600;
  color: #444;
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
  background: #99aabb;
  line-height: 1.4;
}
.log-badge-ver {
  background: #c0c8d0;
}
.log-clear-btn {
  padding: 3px 12px;
  border: 1px solid #d0d6dc;
  border-radius: 999px;
  background: #fff;
  font-size: 11px;
  color: #888;
  cursor: pointer;
  font-family: inherit;
  transition: all 0.2s;
}
.log-clear-btn:hover {
  border-color: #aaa;
  color: #555;
}

/* ===== 日志列表 ===== */
.log-list {
  flex: 1;
  overflow-y: auto;
  font-size: 12px;
  font-family: 'UI', 'Bahnschrift', 'Microsoft YaHei UI', sans-serif;
  user-select: text;
  padding: 4px 6px;
}
.log-empty {
  color: #ccc;
  font-size: 13px;
  text-align: center;
  padding: 20px 0;
}
.log-row {
  display: flex;
  align-items: baseline;
  gap: 6px;
  padding: 5px 10px;
  margin: 5px 0;
  background: #fff;
  border-radius: 8px;
  border: 1.5px solid #e0e4e8;
}

/* 各级别边框颜色 */
.log-info    { border-color: #99ccdd; }
.log-warn    { border-color: #e8a840; }
.log-error   { border-color: #e05555; }
.log-success { border-color: #55b888; }

/*
 *  时间戳颜色按日志级别区分（取代彩色圆点）
 *  默认灰色 #b0b8c0，各级别覆盖为对应颜色
 */
.log-time {
  white-space: nowrap;
  flex-shrink: 0;
  font-size: 11px;
  color: #b0b8c0;
}
.log-info .log-time    { color: #99ccdd; }
.log-warn .log-time    { color: #e8a840; }
.log-error .log-time   { color: #e05555; }
.log-success .log-time { color: #55b888; }

/*
 *  消息正文颜色
 *  默认深灰 #556，warn/error/success 级别有对应颜色
 */
.log-msg {
  word-break: break-all;
  color: #556;
  line-height: 1.5;
}
.log-warn .log-msg    { color: #a07030; }
.log-error .log-msg   { color: #c04040; }
.log-success .log-msg { color: #3a8050; }
</style>
