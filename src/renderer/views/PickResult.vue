<!--
================================================================================
  组件：PickResult.vue
  所属：抽取结果窗口（全屏透明覆盖层，主进程 BrowserWindow 直接加载）
  路由：/pick-result（由 main.js 通过 BrowserWindow 直接加载，不经 vue-router）

================================================================================
  一、功能概述
================================================================================
  本组件是抽取结果展示窗口的完整界面，负责：

    功能              | 说明
    ──────────────────┼──────────────────────────────────────────────
    1. 结果展示       | 信件飞入动画 + 姓名逐一展开，支持 1-10 人双排布局
    2. 状态机         | idle → opening → reveal → ready → closing 五阶段
    3. 音频播放       | 抽卡音效 + 背景音乐（可配置起始位置 + 淡入淡出）
    4. 外观配置       | 面板颜色/透明度/边框色 从 pickResultDialog 配置加载
    5. 关闭交互       | 点击面板外 / 按 Esc 关闭（动画结束后才允许）

================================================================================
  二、数据流架构
================================================================================

  +-------------------------------------------------------------+
  |  主进程 (ipc.js / windows.js)                                 |
  |  - pickResultApi.getConfig()  → pickResultDialog 配置对象     |
  |  - pickResultApi.getResults() → 当前抽取结果数组              |
  |  - pickResultApi.onOpen(cb)   → 接收"打开结果"事件 + payload  |
  |  - pickResultApi.onReset(cb)  → 接收"重置结果"事件 + token    |
  |  - pickResultApi.close()      → 通知主进程关闭窗口             |
  +--------------------------+----------------------------------+
                             | IPC (contextBridge via preload.js)
                             v
  +-------------------------------------------------------------+
  |  PickResult.vue（本组件）                                     |
  |                                                             |
  |  状态（refs）：                                              |
  |    results, stagePhase, canClose, activeToken                |
  |    playGachaSound, gachaSoundVolume, playMusic, musicVolume   |
  |    bgmStartTime, bgmFadeDuration                             |
  |    panelOpacity, panelBgColor, panelBorderColor              |
  |                                                             |
  |  音频引擎（模块变量）：                                       |
  |    gachaAudio, musicAudio, fadeTimerId, bgmPlaying           |
  +-------------------------------------------------------------+

================================================================================
  三、状态机（stagePhase）
================================================================================

  idle ──(startSession)──→ opening ──(动画计时器)──→ reveal ──(计时器)──→ ready
    ^                         │                        │                  │
    │                         │                        │                  │
    └──(resetResultState)─────┴────────────────────────┴──(closeResult)──┘
                                              │
                                              └──→ closing ──(220ms)──→ idle

  各阶段行为：
    idle     — 空闲状态，面板隐藏
    opening  — 面板飞入动画中（0.7s），信封逐张飞入
    reveal   — 姓名逐一展开，canClose 仍为 false
    ready    — 动画完成，允许关闭（点击/ESC）
    closing  — 淡出动画中（220ms），结束后通知主进程隐藏窗口

================================================================================
  四、动画与时序
================================================================================
  面板飞入：0.7s cubic-bezier(0.2,0.8,0.2,1)，scale 0.85→1 + translateY 16→0
  信封飞入：0.6s ease-out，每张延迟 index×0.12s，scale 2.5→1 + rotate 15°
  姓名展开：0.3s ease-out，每张延迟 index×0.12s+0.1s，translateY 12→0
  面板退出：0.2s ease，scale 1→0.9 + translateY 0→8
  舞台淡出：220ms ease，opacity 1→0

================================================================================
  五、BGM 音频系统
================================================================================
  setTimeout 链（16ms ≈ 60fps）替代 requestAnimationFrame：
    原因：win.hide() 后隐藏窗口的 rAF 回调被浏览器暂停，淡出动画冻结。
    setTimeout 不受窗口可见性影响。

  bgmPlaying 标志位：
    解决 playBgm() 中 await musicAudio.play() 与 stopBgm() 的竞态条件。

  BGM 来源（版权资源，不随仓库分发）：
    背景音乐由用户在配置面板上传到 customs/ 目录（purpose=bgm）。
    结果窗口通过 pick-result:get-config 获取主进程解析的 bgmDataUrl
    （data URL）作为 Audio 源；未上传时 bgmDataUrl 为空，不播放 BGM。

================================================================================
  六、维护注意事项
================================================================================
  - stagePhase 状态机是核心：所有动画/交互都依赖阶段判断，修改需全局审视
  - token 由主进程下发，用于判断 onOpen 事件是否过期（快速连续抽取时）
  - sessionSeed 自增生成 activeSessionId，确保旧计时器不会误伤新会话
  - .result-panel 使用 @click.stop 阻止冒泡（点击面板内部不触发关闭）
  - BGM 淡入淡出逻辑见 playBgm/stopBgm/cancelFade 三函数的详细注释

================================================================================
  七、更新记录
================================================================================
  2026-08-29
    - BGM 由仓库静态资源 sound/bgm.mp3 改为 customs/ 自定义资源：
      播放源改用 pick-result:get-config 返回的 bgmDataUrl，未上传则不播放
    - 抽取音效由仓库静态资源 sound/gacha_loading.wav 改为 customs/ 自定义资源：
      播放源改用 get-config 返回的 gachaSoundDataUrl，未上传则不播放
    - 信封改为按学生 letterColor 显示（Letter_Rainbow/Gold/Blue.png，缺省彩虹）
    - 修复打包后信封图片消失：/image 绝对路径在 file:// 下解析失败，
      letterSrc 与装饰图改用 resolveAssetUrl 按协议拼接完整 URL

  2026-09-05
    - 内存优化：.result-panel 移除 backdrop-filter（全屏透明窗口毛玻璃是
      GPU/共享内存暴涨主因），背景改为 rgba(255,255,255,0.92)
    - 新增 releaseAudio()：关闭时暂停/释放 gachaAudio/musicAudio 及
      data URL 引用（Promise 清理），防止 Audio 数据残留占用内存
    - 交互变更：曾移除点击舞台空白处关闭，改为面板下方“点击此区域关闭”
      长条 + Esc；空结果显示“暂无抽取结果”
    - 信封阴影恢复为每张独立 drop-shadow（视觉效果优先）

  2026-09-06
    - 结果窗口尺寸：主进程恢复全屏透明展示（撤销固定 860×680），
      本文件无需改动布局，仅同步注释
    - 交互回退：移除“点击此区域关闭”长条，恢复点击舞台空白处关闭
      （handleStageClick）+ 提示文字（instructionText）与 Esc；
      保留空结果“暂无抽取结果”占位

  最后更新：2026-09-06

  最后更新：2026-09-06
================================================================================
-->
<template>
  <div
    class="result-stage"
    :class="{ 'is-closing': isClosing }"
    tabindex="0"
    @click="handleStageClick"
    @keydown="handleKeydown"
  >
    <!-- ====== 中央区域：装饰图 + 结果面板 ====== -->
    <div class="result-center">
      <!-- 顶部装饰：阿罗娜 & 普拉娜，图片底端紧贴面板上边 -->
      <img
        v-if="showDeco"
        class="result-deco-top"
        :src="resolveAssetUrl('/image/Arona_Plana.png')"
        alt=""
        draggable="false"
      />

      <!-- ====== 结果面板（中央圆角卡片） ====== -->
      <div
        class="result-panel"
        :class="{
          'is-fly-in': stagePhase === 'opening' || stagePhase === 'reveal' || stagePhase === 'ready',
          'is-closing': isClosing
        }"
        :style="panelStyle"
        @click.stop
      >
        <!-- ====== 信件行容器 ====== -->
      <div class="result-rows" :class="{ 'is-two-rows': isTwoRows }" :key="animationKey">
        <!-- 第 1 行：最多 5 张 -->
        <div class="result-row">
          <div
            v-for="(item, index) in topRow"
            :key="`top-${index}-${item.name}`"
            class="letter-card"
            :style="{ '--index': index }"
          >
            <img class="letter-img" :src="letterSrc(item)" alt="letter" />
            <div class="name-card" :class="{ 'is-reveal': revealStarted }" :style="{ '--reveal-index': index }">
              <span>{{ item.name }}</span>
            </div>
          </div>
        </div>
        <!-- 第 2 行：第 6-10 张（仅 >5 人时显示） -->
        <div v-if="isTwoRows" class="result-row">
          <div
            v-for="(item, index) in bottomRow"
            :key="`bottom-${index}-${item.name}`"
            class="letter-card"
            :style="{ '--index': index + 5 }"
          >
            <img class="letter-img" :src="letterSrc(item)" alt="letter" />
            <div class="name-card" :class="{ 'is-reveal': revealStarted }" :style="{ '--reveal-index': index + 5 }">
              <span>{{ item.name }}</span>
            </div>
          </div>
        </div>
      </div>

      <!-- 提示文字 / 空结果 -->
      <p v-if="results.length" class="result-hint">{{ instructionText }}</p>
      <p v-else class="result-empty">暂无抽取结果</p>
    </div>
    <!-- / result-center -->
  </div>
</div>
</template>

<script setup>
// ============================================================
//  导入依赖
// ============================================================
import { ref, computed, onMounted, onBeforeUnmount, nextTick } from 'vue'

// ============================================================
//  1. 结果展示核心状态
// ============================================================

/* 当前抽取结果数组 [{ name: string }] */
const results = ref([])

/* 信封图片映射：彩（默认）/ 黄 / 蓝 */
const LETTER_IMAGES = {
  rainbow: '/image/Letter_Rainbow.png',
  gold: '/image/Letter_Gold.png',
  blue: '/image/Letter_Blue.png'
}
function letterSrc(item) {
  const key = item?.letterColor || 'rainbow'
  return resolveAssetUrl(LETTER_IMAGES[key] || LETTER_IMAGES.rainbow)
}

/* 动画 key：自增以强制 Vue 重建 DOM，重置动画 */
const animationKey = ref(0)

/* 阶段状态机：idle | opening | reveal | ready | closing */
const stagePhase = ref('idle')

/* 姓名是否开始逐一展开 */
const revealStarted = ref(false)

/* 是否允许关闭（动画完成后才置 true） */
const canClose = ref(false)

/* 当前活跃的抽取批次 token（主进程下发） */
const activeToken = ref(0)

/* 是否正在关闭中 */
const isClosing = computed(() => stagePhase.value === 'closing')

/* 提示文字 */
const instructionText = ref('点击区域外任意位置关闭')

// ============================================================
//  2. 资源路径解析
// ============================================================

/*
 * 将相对路径转为完整的资源 URL。
 * file:// 协议和 http:// 协议使用不同的 base URL 拼接方式。
 */
const resolveAssetUrl = (relativePath) => {
  const base = window.location.protocol === 'file:'
    ? new URL('.', window.location.href).toString()
    : `${window.location.origin}/`
  return new URL(relativePath.replace(/^\/+/, ''), base).toString()
}

const gachaSoundDataUrl = ref('')   // 抽取音效 data URL（由主进程从 customs/ 解析，空=未上传）

// ============================================================
//  3. 音频引擎（模块级变量，不参与 Vue 响应式）
// ============================================================

let gachaAudio = null       // 抽卡音效 Audio 实例
let musicAudio = null       // 背景音乐 Audio 实例
let revealTimer = null      // 姓名展开触发定时器
let readyTimer = null       // 允许关闭触发定时器
let closeFadeTimer = null   // 关闭淡出完成定时器
let sessionSeed = 0         // 会话种子（自增生成 ID）
let activeSessionId = 0     // 当前活跃的会话 ID

// ============================================================
//  4. 音频配置 refs（从 pickResultApi.getConfig() 加载）
// ============================================================

const playGachaSound = ref(true)       // 是否播放抽卡音效
const gachaSoundVolume = ref(0.6)      // 音效音量（0-1）
const playMusic = ref(false)           // 是否播放 BGM
const bgmDataUrl = ref('')             // BGM 音频 data URL（由主进程从 customs/ 解析，空=无背景音乐）
const musicVolume = ref(0.6)           // BGM 音量（0-1）
const bgmStartTime = ref(0)            // BGM 起始秒数
const bgmFadeDuration = ref(1.5)       // 淡入淡出秒数

// ============================================================
//  5. 面板外观 refs
// ============================================================

const panelOpacity = ref(0.9)
const panelBgColor = ref('#ffffff')
const panelBorderColor = ref('#66ccff')
const showDeco = ref(true)

/* 面板动态样式：hex 颜色 + rgba alpha 通道拼接 */
const panelStyle = computed(() => ({
  background: `${panelBgColor.value}${Math.round(panelOpacity.value * 255).toString(16).padStart(2, '0')}`,
  borderColor: panelBorderColor.value
}))

// ============================================================
//  6. 分排计算
// ============================================================

/* 第 1 行：前 5 个结果 */
const topRow = computed(() => results.value.slice(0, 5))

/* 第 2 行：第 6 个及之后结果 */
const bottomRow = computed(() => results.value.slice(5))

/* 是否双排（>5 人时启用） */
const isTwoRows = computed(() => results.value.length > 5)

// ============================================================
//  7. 结果归一化
// ============================================================

/*
 * 兼容多种结果格式，统一为 [{ name: string, letterColor: string }]。
 * 支持：字符串数组 / 对象数组 / 嵌套 { results: [...] }
 * letterColor（信封颜色）保留：rainbow/gold/blue，缺省为 rainbow（彩）。
 */
function normalizeResults(payload) {
  const list = Array.isArray(payload?.results) ? payload.results : payload
  if (!Array.isArray(list)) return []
  return list
    .map((item) => {
      if (!item) return null
      if (typeof item === 'string') return { name: item.trim(), letterColor: 'rainbow' }
      if (typeof item === 'object') return {
        name: String(item.name || '').trim(),
        letterColor: ['rainbow', 'gold', 'blue'].includes(item.letterColor) ? item.letterColor : 'rainbow'
      }
      return null
    })
    .filter((item) => item && item.name)
}

// ============================================================
//  8. 定时器管理
// ============================================================

/* 清除所有动画相关的定时器 */
function clearTimers() {
  if (revealTimer) { clearTimeout(revealTimer); revealTimer = null }
  if (readyTimer) { clearTimeout(readyTimer); readyTimer = null }
  if (closeFadeTimer) { clearTimeout(closeFadeTimer); closeFadeTimer = null }
}

// ============================================================
//  9. 状态重置
// ============================================================

/*
 * 重置结果状态到 idle。
 * stopSound=true 时同时停止音效和 BGM。
 */
function resetResultState({ stopSound = true, clearResults = true } = {}) {
  clearTimers()
  if (clearResults) results.value = []
  animationKey.value += 1
  revealStarted.value = false
  canClose.value = false
  stagePhase.value = 'idle'
  if (stopSound) {
    stopGachaLoadingSound()
    stopBgm()
  }
}

// ============================================================
//  10. 会话启动
// ============================================================

/*
 * 开始一个新的抽取结果展示会话。
 *
 * 流程：
 *   1. 生成新 sessionId（使旧计时器失效）
 *   2. 重置所有状态
 *   3. 归一化结果数据
 *   4. 进入 opening 阶段（触发面板飞入动画）
 *   5. 记录 token（用于防御过期的 onOpen 事件）
 *   6. 计算延迟：最后一张信封飞入后 600ms → 展开姓名 → 再 450ms → 允许关闭
 *   7. 触发音效/BGM（按配置）
 */
function startSession(payload) {
  sessionSeed += 1
  activeSessionId = sessionSeed
  resetResultState({ stopSound: true, clearResults: true })

  results.value = normalizeResults(payload)
  stagePhase.value = 'opening'

  const token = Number(payload?.token)
  if (Number.isFinite(token)) {
    activeToken.value = token
  } else {
    activeToken.value += 1
  }

  if (results.value.length === 0) {
    canClose.value = true
    stagePhase.value = 'ready'
    return
  }

  const sessionId = activeSessionId
  /* 延迟 = 最后一张信封飞入时间 + 600ms */
  const totalDelayMs = (Math.max(results.value.length - 1, 0) * 120) + 600
  revealTimer = setTimeout(() => {
    if (sessionId !== activeSessionId) return
    revealStarted.value = true
    stagePhase.value = 'reveal'
  }, totalDelayMs)

  /* 再延迟 450ms 后允许关闭 */
  const totalDurationMs = totalDelayMs + 450
  readyTimer = setTimeout(() => {
    if (sessionId !== activeSessionId) return
    canClose.value = true
    stagePhase.value = 'ready'
  }, totalDurationMs)

  if (playGachaSound.value) playGachaLoadingSound()
  if (playMusic.value) playBgm()
}

// ============================================================
//  11. 事件处理
// ============================================================

/*
 * 处理主进程的 reset 事件。
 * 通过 token 判断事件是否过期：过期 token（< activeToken）直接忽略。
 */
function handleReset(payload) {
  const token = Number(payload?.token)
  if (Number.isFinite(token) && token < activeToken.value) return
  if (Number.isFinite(token)) activeToken.value = token
  resetResultState({ stopSound: true, clearResults: true })
}

/* 按 Esc → 关闭 */
function handleKeydown(event) {
  if (event.key === 'Escape' && canClose.value && !isClosing.value) {
    closeResult()
  }
}

/* 点击舞台（面板外区域）→ 关闭 */
function handleStageClick() {
  if (!canClose.value || isClosing.value) return
  closeResult()
}

// ============================================================
//  12. 关闭流程
// ============================================================

/*
 * 关闭结果面板。
 *
 * 流程：
 *   1. 立即停止 BGM 淡出（与面板 CSS 淡出同步开始）
 *   2. 进入 closing 阶段（触发面板 CSS 淡出动画）
 *   3. 取消所有进行中的动画计时器
 *   4. 220ms 后（面板淡出动画完成）：
 *      a. 停止抽卡音效 + 重置状态
 *      b. 等待 Vue DOM 更新 + 一个动画帧
 *      c. 通知主进程关闭窗口
 *
 * 时序说明：
 *   BGM 的 setTimeout 淡出不受窗口可见性影响（之前已验证）。
 *   面板 CSS 淡出 220ms，BGM 淡出时长由 bgmFadeDuration 配置（默认 1.5s）。
 *   BGM 淡出在窗口关闭后仍会继续完成（Audio 对象 + setTimeout 独立于 DOM）。
 *
 * 使用 sessionId 防御：如果 220ms 内新会话启动，取消关闭。
 */
async function closeResult() {
  if (isClosing.value) return

  /*
   * 立即开始 BGM 淡出，与面板 CSS 淡出同步。
   *
   * 必须在 stagePhase = 'closing' 之前调用，
   * 因为 stopBgm() 内部使用 setTimeout 链（不受 CSS 动画影响），
   * 尽早调用可以让音频淡出和视觉淡出在时间轴上对齐。
   */
  stopBgm()

  stagePhase.value = 'closing'
  canClose.value = false
  const sessionId = activeSessionId

  clearTimers()

  closeFadeTimer = setTimeout(async () => {
    if (sessionId !== activeSessionId || stagePhase.value !== 'closing') return
    /*
     * stopSound: false —— BGM 已在上面 stopBgm() 中开始淡出，不需要再停一次。
     * 只需停止抽卡音效（stopGachaLoadingSound）。
     */
    resetResultState({ stopSound: false, clearResults: true })
    stopGachaLoadingSound()
    releaseAudio()
    await nextTick()
    await new Promise((resolve) => window.requestAnimationFrame(resolve))
    if (window.pickResultApi) window.pickResultApi.close()
  }, 220)
}

// ============================================================
//  12.5 音频资源释放（关闭时调用，避免常驻内存）
// ============================================================

/*
 * 释放音效与 BGM：pause + 清空 src（释放解码器）+ 置空引用 + 清空 data URL 大字符串。
 * 在结果面板关闭时调用（窗口将关闭/隐藏，无需保留 Audio）。
 */
function releaseAudio() {
  if (gachaAudio) { gachaAudio.pause(); gachaAudio.src = ''; gachaAudio = null }
  if (musicAudio) { musicAudio.pause(); musicAudio.src = ''; musicAudio = null }
  gachaSoundDataUrl.value = ''
  bgmDataUrl.value = ''
  bgmPlaying = false
  if (fadeTimerId) { clearTimeout(fadeTimerId); fadeTimerId = null }
}

// ============================================================
//  13. 抽卡音效
// ============================================================

function stopGachaLoadingSound() {
  if (gachaAudio) { gachaAudio.pause(); gachaAudio.currentTime = 0 }
}

/* 播放抽卡短音效（单次、不循环） */
async function playGachaLoadingSound() {
  /* 未上传抽取音效时不播放 */
  if (!gachaSoundDataUrl.value) return
  try {
    stopGachaLoadingSound()
    if (!gachaAudio) {
      gachaAudio = new Audio(gachaSoundDataUrl.value)
      gachaAudio.addEventListener('error', () => {
        console.error('Gacha sound load failed', { src: gachaAudio.src, error: gachaAudio.error })
      })
    }
    gachaAudio.volume = Math.max(0, Math.min(1, Number(gachaSoundVolume.value) || 0))
    gachaAudio.currentTime = 0
    await gachaAudio.play().catch((error) => { console.warn('Gacha sound play blocked', error) })
  } catch (error) { console.warn('Failed to play gacha loading sound:', error) }
}

// ============================================================
//  14. BGM 播放（淡入淡出 + 竞态保护）
// ============================================================

let fadeTimerId = null
let bgmPlaying = false

function cancelFade() {
  bgmPlaying = false
  if (fadeTimerId) { clearTimeout(fadeTimerId); fadeTimerId = null }
}

/*
 * 播放 BGM（循环），从 bgmStartTime 秒处开始，setTimeout 链淡入。
 * bgmPlaying 标志位解决 async/await 竞态：await play() 期间 stopBgm() 可能已调用。
 */
async function playBgm() {
  /* 未上传背景音乐时不播放 */
  if (!bgmDataUrl.value) return
  try {
    cancelFade()
    bgmPlaying = true
    if (!musicAudio) {
      musicAudio = new Audio(bgmDataUrl.value)
      musicAudio.loop = true
      musicAudio.addEventListener('error', () => {
        console.error('BGM load failed', { src: musicAudio.src, error: musicAudio.error })
      })
    }
    const targetVolume = Math.max(0, Math.min(1, Number(musicVolume.value) || 0))
    const fadeSec = Math.max(0.1, Number(bgmFadeDuration.value) || 1.5)
    musicAudio.volume = 0
    musicAudio.currentTime = Math.max(0, Number(bgmStartTime.value) || 0)
    await musicAudio.play().catch((error) => { console.warn('BGM play blocked', error) })

    if (!bgmPlaying) return

    const startTime = performance.now()
    const fadeIn = () => {
      if (!bgmPlaying) { fadeTimerId = null; return }
      const elapsed = (performance.now() - startTime) / 1000
      const progress = Math.min(1, elapsed / fadeSec)
      if (musicAudio) musicAudio.volume = targetVolume * progress
      if (progress < 1 && musicAudio && !musicAudio.paused) {
        fadeTimerId = setTimeout(fadeIn, 16)
      } else {
        fadeTimerId = null
      }
    }
    fadeTimerId = setTimeout(fadeIn, 16)
  } catch (error) { console.warn('Failed to play BGM:', error) }
}

/*
 * 停止 BGM：setTimeout 链淡出，完成后 pause() + 重置进度。
 */
function stopBgm() {
  if (!musicAudio || musicAudio.paused) return
  bgmPlaying = false
  cancelFade()
  const startVolume = musicAudio.volume
  const fadeSec = Math.max(0.1, Number(bgmFadeDuration.value) || 1.5)
  const startTime = performance.now()
  const fadeOut = () => {
    const elapsed = (performance.now() - startTime) / 1000
    const progress = Math.min(1, elapsed / fadeSec)
    if (musicAudio) musicAudio.volume = startVolume * (1 - progress)
    if (progress < 1 && musicAudio && !musicAudio.paused) {
      fadeTimerId = setTimeout(fadeOut, 16)
    } else {
      if (musicAudio) { musicAudio.pause(); musicAudio.currentTime = 0 }
      fadeTimerId = null
    }
  }
  fadeTimerId = setTimeout(fadeOut, 16)
}

// ============================================================
//  15. 配置加载
// ============================================================

/* 从主进程获取 pickResultDialog 配置，字段映射见函数内注释 */
async function loadSoundConfig() {
  if (!window.pickResultApi || typeof window.pickResultApi.getConfig !== 'function') return
  const cfg = await window.pickResultApi.getConfig()
  playGachaSound.value = cfg?.defaultPlayGachaSound ?? true
  gachaSoundDataUrl.value = cfg?.gachaSoundDataUrl || ''
  gachaSoundVolume.value = Math.max(0, Math.min(1, (cfg?.soundVolume ?? 80) / 100))
  playMusic.value = cfg?.playMusic ?? false
  bgmDataUrl.value = cfg?.bgmDataUrl || ''
  musicVolume.value = Math.max(0, Math.min(1, (cfg?.musicVolume ?? 60) / 100))
  bgmStartTime.value = cfg?.bgmStartTime ?? 0
  bgmFadeDuration.value = cfg?.bgmFadeDuration ?? 1.5
  panelOpacity.value = Number(cfg?.panelOpacity) || 0.9
  panelBgColor.value = cfg?.panelBgColor || '#ffffff'
  panelBorderColor.value = cfg?.panelBorderColor || '#66ccff'
  showDeco.value = cfg?.showDeco !== false
}

// ============================================================
//  16. 生命周期
// ============================================================

let removeOpenListener = null
let removeResetListener = null

onMounted(async () => {
  await loadSoundConfig()

  /* 获取初始结果（窗口可能已有缓存的抽取结果） */
  if (window.pickResultApi && typeof window.pickResultApi.getResults === 'function') {
    const initial = await window.pickResultApi.getResults()
    startSession({ results: initial })
  }

  /* 监听"打开结果"事件（后续抽取时主进程通过 IPC 发送） */
  if (window.pickResultApi && typeof window.pickResultApi.onOpen === 'function') {
    removeOpenListener = window.pickResultApi.onOpen(async (payload) => {
      await loadSoundConfig()
      startSession(payload)
    })
  }

  /* 监听"重置结果"事件 */
  if (window.pickResultApi && typeof window.pickResultApi.onReset === 'function') {
    removeResetListener = window.pickResultApi.onReset((payload) => { handleReset(payload) })
  }
})

onBeforeUnmount(() => {
  clearTimers()
  stopGachaLoadingSound()
  stopBgm()
  if (typeof removeOpenListener === 'function') removeOpenListener()
  if (typeof removeResetListener === 'function') removeResetListener()
})
</script>

<style scoped>
/*
 *  本文件 CSS 为 scoped。
 *  按功能分为 8 个分组。
 */

/* =================================================================
   1. 舞台容器
   ================================================================= */

/*
 *  全屏 flex 容器，居中面板。
 *  tabindex="0" 使其可聚焦（接收键盘事件）。
 *  关闭时 pointer-events: none + 淡出动画。
 */
.result-stage {
  width: 100%;
  height: 100%;
  display: flex;
  align-items: center;
  justify-content: center;
  outline: none;
  /*
   * UIAccess 模式下 DWM 合成可能使透明区域无法接收点击。
   * rgba(0,0,0,0.01) 使整个全屏区域创建合成层以确保命中测试。
   */
  background: rgba(0, 0, 0, 0.01);
}

.result-stage.is-closing {
  pointer-events: none;
  animation: result-fade-out 220ms ease forwards;
}

/* =================================================================
   1.5. 中央区域（装饰图 + 结果面板 纵向容器）
   ================================================================= */

/*
 *  纵向排列装饰图 + 面板，整体在 .result-stage 中居中。
 *  max-width 限制面板 + 图的整体宽度。
 */
.result-center {
  display: flex;
  flex-direction: column;
  align-items: center;
}

/*
 * 顶部装饰图：阿罗娜 & 普拉娜趴在线上（原图 1205×506）。
 * 固定宽度 320px，等比缩放。
 * margin-bottom: -4px 使图片底端紧贴面板外上边框，零缝隙。
 */
.result-deco-top {
  display: block;
  width: 300px;
  height: auto;
  margin-bottom: -4px;
  pointer-events: none;
  user-select: none;
}

/* =================================================================
   2. 结果面板（中央圆角卡片）
   ================================================================= */

/*
 *  白底半透明 + 毛玻璃（backdrop-filter） + 实线边框。
 *  初始状态 opacity: 0 + scale(0.85) translateY(16px)（等待飞入动画触发）。
 *  .is-fly-in → panel-fly-in 动画（0.7s cubic-bezier）。
 *  .is-closing → panel-fly-out 动画（0.2s ease）。
 */
.result-panel {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 18px;
  padding: 20px 36px 16px;
  border-radius: 22px;
  background: rgba(255, 255, 255, 0.92);
  /* backdrop-filter 已移除：全屏透明窗口毛玻璃导致 GPU/共享内存暴涨（500MB 主因） */
  border: 4px solid #66ccff;
  box-shadow: 0 8px 32px rgba(6, 22, 48, 0.15);
  min-width: 320px;
  max-width: 95vw;
  opacity: 0;
  transform: scale(0.85) translateY(16px);
}

.result-panel.is-fly-in {
  animation: panel-fly-in 0.7s cubic-bezier(0.2, 0.8, 0.2, 1) forwards;
}

.result-panel.is-closing {
  pointer-events: none;
  animation: panel-fly-out 0.2s ease forwards;
}

@keyframes panel-fly-in {
  0% {
    opacity: 0;
    transform: scale(0.85) translateY(16px);
  }
  100% {
    opacity: 1;
    transform: scale(1) translateY(0);
  }
}

@keyframes panel-fly-out {
  0% {
    opacity: 1;
    transform: scale(1) translateY(0);
  }
  100% {
    opacity: 0;
    transform: scale(0.9) translateY(8px);
  }
}

/* =================================================================
   3. 信件行容器
   ================================================================= */

/* 纵向排列（双排时上下各一行） */
.result-rows {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 36px;
}

/* 每行横向排列信件 */
.result-row {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 28px;
}

/* =================================================================
   4. 信封卡片
   ================================================================= */

/*
 *  信封容器：响应式宽度，固定 4:3 比例。
 *  入场动画：letter-fly-in（0.6s），延迟 index×0.12s。
 *  初始状态：超大 + 旋转 + 透明（等待动画覆盖）。
 */
/* 信封投影恢复为每张独立（开启时视觉效果优先） */
.letter-card {
  position: relative;
  width: clamp(120px, 16vw, 200px);
  aspect-ratio: 4 / 3;
  opacity: 0;
  transform: scale(2.5) rotate(15deg);
  animation: letter-fly-in 0.6s ease-out forwards;
  animation-delay: calc(var(--index) * 0.12s);
}

/* 信封图片：投影增加纵深（恢复独立阴影） */
.letter-img {
  width: 100%;
  height: 100%;
  object-fit: contain;
  filter: drop-shadow(0 12px 24px rgba(0, 0, 0, 0.25));
}

/* =================================================================
   5. 姓名卡（覆盖在信封上）
   ================================================================= */

/*
 *  绝对定位在信封中央偏上（inset: 18% 10%）。
 *  初始透明 + 下移 + 微缩（等待 .is-reveal 触发）。
 *  .is-reveal → name-reveal 动画（0.3s），延迟 index×0.12s+0.1s。
 */
.name-card {
  position: absolute;
  inset: 18% 10%;
  background: #ffffff;
  border-radius: 10px;
  display: flex;
  align-items: center;
  justify-content: center;
  text-align: center;
  padding: 6px 10px;
  font-size: clamp(16px, 2.1vw, 26px);
  font-weight: 700;
  color: #1c2741;
  box-shadow: 0 10px 26px rgba(0, 0, 0, 0.25);
  opacity: 0;
  transform: translateY(12px) scale(0.96);
  animation: none;
  word-break: break-all;
  word-wrap: break-word;
}

.name-card.is-reveal {
  animation: name-reveal 0.3s ease-out forwards;
  animation-delay: calc(var(--reveal-index) * 0.12s + 0.1s);
}

/* =================================================================
   6. 提示文字 / 空结果
   ================================================================= */

/* 关闭提示：蓝底白字胶囊形 */
.result-hint {
  margin: 0;
  padding: 4px 12px;
  border-radius: 999px;
  background: #66ccff;
  color: #ffffff;
  font-size: 13px;
  font-weight: 500;
  letter-spacing: 2px;
}

/* 空结果文字 */
.result-empty {
  margin: 0;
  font-size: 18px;
  color: #b9dcff;
  letter-spacing: 2px;
}

/* =================================================================
   7. 信封飞入动画
   ================================================================= */
@keyframes letter-fly-in {
  0% {
    opacity: 0;
    transform: scale(2.5) rotate(15deg) translateY(-24px);
  }
  100% {
    opacity: 1;
    transform: scale(1) rotate(15deg) translateY(0);
  }
}

/* =================================================================
   8. 姓名展开 + 舞台淡出动画
   ================================================================= */
@keyframes name-reveal {
  0% {
    opacity: 0;
    transform: translateY(12px) scale(0.96);
  }
  100% {
    opacity: 1;
    transform: translateY(0) scale(1);
  }
}

@keyframes result-fade-out {
  0%   { opacity: 1; }
  100% { opacity: 0; }
}
</style>
