<!--
================================================================================
  文件: PickResult.vue
  模块: 招募结果浮窗 (基于透明 WebView2 硬件加速渲染)
  特性:
    1. 还原经典手游招募动效：信封倾角飞入、姓名条逐一弹跳揭晓；
    2. 视效增强：深度高斯漫反射投影、4px 蔚蓝圆角边框；
    3. 交互限制：禁用全屏空白处点击关闭，在卡片面板底部设置专用“点击此处关闭”长条胶囊提示；
    4. 音频与流媒体：抽卡短音效即时回放、BGM 平滑淡入与退场淡出。

  宿主通信契约（C# / WebView2，2026-10-17）:
    · ResultWindow 通过 ExecuteScriptAsync 调用
        window.__onPickResultOpen({ results, token })  打开并渲染结果
        window.__onPickResultReset()                   重置并隐藏面板
    · 配置/结果/关闭通过 window.chrome.webview.hostObjects.nativeBridge 获取：
        GetPickResultConfigJson() / GetLastResultsJson() / ClosePickResult()
    · window.pickResultApi 仅作为旧 Electron/纯浏览器调试的降级路径保留
================================================================================
-->
<template>
  <div
    class="result-stage"
    :class="{ 'is-closing': isClosing }"
    tabindex="0"
    @keydown="handleKeydown"
  >
    <!-- ====== 中央区域：结果面板 ====== -->
    <div class="result-center">
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

        <!-- 空结果提示 -->
        <p v-if="!results.length" class="result-empty">暂无抽取结果</p>

        <!-- 底部关闭长条提示 (限制区域：仅在此处点击关闭) -->
        <div
          v-if="results.length"
          class="result-close-bar"
          :class="{ 'is-ready': canClose }"
          @click.stop="handleCloseBarClick"
        >
          <span class="close-bar-text">{{ canClose ? '点击此处关闭' : '正在揭晓招募结果...' }}</span>
        </div>
      </div>
      <!-- / result-center -->
    </div>
  </div>
</template>

<script setup>
import { ref, computed, onMounted, onBeforeUnmount, nextTick } from 'vue'

// ============================================================
//  1. 状态定义
// ============================================================

/* 当前抽取结果数组 [{ name: string, letterColor: string }] */
const results = ref([])

/* 信封图片映射：彩（默认）/ 金 / 蓝 */
const LETTER_IMAGES = {
  rainbow: '/image/Letter_Rainbow.png',
  gold: '/image/Letter_Gold.png',
  blue: '/image/Letter_Blue.png'
}

function letterSrc(item) {
  const key = item?.letterColor || 'rainbow'
  return resolveAssetUrl(LETTER_IMAGES[key] || LETTER_IMAGES.rainbow)
}

/* 动画重建 key */
const animationKey = ref(0)

/* 阶段状态：idle | opening | reveal | ready | closing */
const stagePhase = ref('idle')
const revealStarted = ref(false)
const canClose = ref(false)
const activeToken = ref(0)

const isClosing = computed(() => stagePhase.value === 'closing')

// ============================================================
//  2. 资源路径解析
// ============================================================

const resolveAssetUrl = (relativePath) => {
  const clean = relativePath.replace(/^\/+/, '')
  const base = window.location.protocol === 'file:'
    ? new URL('.', window.location.href).toString()
    : `${window.location.origin}/`
  return new URL(clean, base).toString()
}

// ============================================================
//  2.5 原生桥接适配（C# WebView2 NativeBridge）
// ============================================================

/*
 * 获取 C# 宿主注入的 nativeBridge（WebView2 hostObjects 异步代理）。
 * 纯浏览器（Vite dev）环境无宿主对象，返回 null，调用方走降级逻辑。
 */
function getNativeBridge() {
  try {
    return window.chrome?.webview?.hostObjects?.nativeBridge || null
  } catch {
    return null
  }
}

/* WebView2 注入 hostObjects 存在时序差，轮询等待其就绪（默认最多 3 秒） */
async function waitForBridge(timeoutMs = 3000) {
  const start = Date.now()
  while (Date.now() - start < timeoutMs) {
    const bridge = getNativeBridge()
    if (bridge) return bridge
    await new Promise((resolve) => setTimeout(resolve, 50))
  }
  return null
}

/* 安全解析 C# 返回的 JSON 字符串 */
function parseJsonSafe(text, fallback) {
  if (!text) return fallback
  try {
    return JSON.parse(text)
  } catch {
    return fallback
  }
}

const gachaSoundDataUrl = ref('')
const bgmDataUrl = ref('')

// ============================================================
//  3. 音频控制器
// ============================================================

let gachaAudio = null
let musicAudio = null
let revealTimer = null
let readyTimer = null
let closeFadeTimer = null
let sessionSeed = 0
let activeSessionId = 0

// ============================================================
//  4. 配置属性
// ============================================================

const playGachaSound = ref(true)
const gachaSoundVolume = ref(0.6)
const playMusic = ref(false)
const musicVolume = ref(0.6)
const bgmStartTime = ref(0)
const bgmFadeDuration = ref(1.5)

const panelOpacity = ref(0.92)
const panelBgColor = ref('#ffffff')
const panelBorderColor = ref('#66ccff')

const panelStyle = computed(() => {
  let hex = panelBgColor.value || '#ffffff'
  if (hex.startsWith('#') && hex.length === 7) {
    const alphaHex = Math.round(panelOpacity.value * 255).toString(16).padStart(2, '0')
    return {
      background: `${hex}${alphaHex}`,
      borderColor: panelBorderColor.value || '#66ccff'
    }
  }
  return {
    background: hex,
    borderColor: panelBorderColor.value || '#66ccff'
  }
})

// ============================================================
//  5. 行分组
// ============================================================

const topRow = computed(() => results.value.slice(0, 5))
const bottomRow = computed(() => results.value.slice(5))
const isTwoRows = computed(() => results.value.length > 5)

// ============================================================
//  6. 归一化与定时器清理
// ============================================================

function normalizeResults(payload) {
  const list = Array.isArray(payload?.results) ? payload.results : (Array.isArray(payload) ? payload : [])
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

function clearTimers() {
  if (revealTimer) { clearTimeout(revealTimer); revealTimer = null }
  if (readyTimer) { clearTimeout(readyTimer); readyTimer = null }
  if (closeFadeTimer) { clearTimeout(closeFadeTimer); closeFadeTimer = null }
}

function resetResultState({ stopSound = true, clearResults = false } = {}) {
  clearTimers()
  if (stopSound) {
    stopGachaLoadingSound()
    stopBgm()
  }
  if (clearResults) {
    results.value = []
  }
  revealStarted.value = false
  canClose.value = false
  stagePhase.value = 'idle'
}

// ============================================================
//  7. 会话启动
// ============================================================

function startSession(payload) {
  sessionSeed += 1
  activeSessionId = sessionSeed
  resetResultState({ stopSound: true, clearResults: true })

  results.value = normalizeResults(payload)
  animationKey.value += 1
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
  const totalDelayMs = (Math.max(results.value.length - 1, 0) * 120) + 600

  revealTimer = setTimeout(() => {
    if (sessionId !== activeSessionId) return
    revealStarted.value = true
    stagePhase.value = 'reveal'
  }, totalDelayMs)

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
//  8. 交互事件
// ============================================================

function handleKeydown(event) {
  if (['Escape', 'Enter', ' '].includes(event.key) && canClose.value && !isClosing.value) {
    closeResult()
  }
}

function handleCloseBarClick() {
  if (!canClose.value || isClosing.value) return
  closeResult()
}

// ============================================================
//  9. 关闭流程
// ============================================================

async function closeResult() {
  if (isClosing.value) return

  stopBgm()
  stagePhase.value = 'closing'
  clearTimers()

  const sessionId = activeSessionId
  closeFadeTimer = setTimeout(async () => {
    if (sessionId !== activeSessionId || stagePhase.value !== 'closing') return
    resetResultState({ stopSound: false, clearResults: true })
    stopGachaLoadingSound()
    releaseAudio()
    await nextTick()
    await new Promise((resolve) => window.requestAnimationFrame(resolve))

    /*
     * 通知宿主关闭：优先使用 WebView2 原生 postMessage 通道（不依赖 COM 代理时序），
     * 由 ResultWindow.WebMessageReceived 直接处理；其余为兼容降级路径。
     */
    const webview = window.chrome?.webview
    if (webview && typeof webview.postMessage === 'function') {
      try {
        webview.postMessage('ClosePickResult')
      } catch (err) {
        console.warn('[PickResult] postMessage 关闭失败:', err)
      }
      return
    }

    /* 兼容旧 Electron preload / 纯浏览器调试环境 */
    const bridge = getNativeBridge()
    if (bridge && typeof bridge.ClosePickResult === 'function') {
      try {
        await bridge.ClosePickResult()
      } catch (err) {
        console.warn('[PickResult] 调用 ClosePickResult 失败:', err)
      }
    } else if (window.pickResultApi && typeof window.pickResultApi.close === 'function') {
      window.pickResultApi.close()
    }
  }, 220)
}

function releaseAudio() {
  if (gachaAudio) { gachaAudio.pause(); gachaAudio.src = ''; gachaAudio = null }
  if (musicAudio) { musicAudio.pause(); musicAudio.src = ''; musicAudio = null }
  gachaSoundDataUrl.value = ''
  bgmDataUrl.value = ''
  bgmPlaying = false
  if (fadeTimerId) { clearTimeout(fadeTimerId); fadeTimerId = null }
}

// ============================================================
//  10. 抽卡音效与 BGM
// ============================================================

function stopGachaLoadingSound() {
  if (gachaAudio) { gachaAudio.pause(); gachaAudio.currentTime = 0 }
}

async function playGachaLoadingSound() {
  const soundSrc = gachaSoundDataUrl.value || resolveAssetUrl('/sound/button_click.wav')
  if (!soundSrc) return
  try {
    stopGachaLoadingSound()
    if (!gachaAudio) {
      gachaAudio = new Audio(soundSrc)
    } else {
      gachaAudio.src = soundSrc
    }
    gachaAudio.volume = Math.max(0, Math.min(1, Number(gachaSoundVolume.value) || 0))
    gachaAudio.currentTime = 0
    await gachaAudio.play().catch((err) => { console.warn('Gacha sound play prevented:', err) })
  } catch (err) {
    console.warn('Play sound error:', err)
  }
}

let fadeTimerId = null
let bgmPlaying = false

function cancelFade() {
  bgmPlaying = false
  if (fadeTimerId) { clearTimeout(fadeTimerId); fadeTimerId = null }
}

async function playBgm() {
  if (!bgmDataUrl.value) return
  try {
    cancelFade()
    bgmPlaying = true
    if (!musicAudio) {
      musicAudio = new Audio(bgmDataUrl.value)
      musicAudio.loop = true
    } else {
      musicAudio.src = bgmDataUrl.value
    }
    const targetVolume = Math.max(0, Math.min(1, Number(musicVolume.value) || 0))
    const fadeSec = Math.max(0.1, Number(bgmFadeDuration.value) || 1.5)
    musicAudio.volume = 0
    musicAudio.currentTime = Math.max(0, Number(bgmStartTime.value) || 0)
    await musicAudio.play().catch((err) => { console.warn('BGM play prevented:', err) })

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
  } catch (err) {
    console.warn('Play BGM error:', err)
  }
}

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
//  11. 配置加载与生命周期
// ============================================================

async function loadSoundConfig() {
  let cfg = null

  /* 1. C# WebView2 宿主：NativeBridge.GetPickResultConfigJson() 返回 JSON 字符串 */
  const bridge = getNativeBridge()
  if (bridge) {
    try {
      cfg = parseJsonSafe(await bridge.GetPickResultConfigJson(), null)
    } catch (err) {
      console.warn('[PickResult] 读取宿主配置失败:', err)
    }
  }

  /* 2. 兼容旧 Electron preload（pickResultApi），纯浏览器/旧环境降级 */
  if (!cfg && window.pickResultApi && typeof window.pickResultApi.getConfig === 'function') {
    cfg = await window.pickResultApi.getConfig()
  }
  if (!cfg) return

  playGachaSound.value = cfg.defaultPlayGachaSound ?? true
  gachaSoundDataUrl.value = cfg.gachaSoundDataUrl || ''
  gachaSoundVolume.value = Math.max(0, Math.min(1, (cfg.soundVolume ?? 80) / 100))
  playMusic.value = cfg.playMusic ?? false
  bgmDataUrl.value = cfg.bgmDataUrl || ''
  musicVolume.value = Math.max(0, Math.min(1, (cfg.musicVolume ?? 60) / 100))
  bgmStartTime.value = cfg.bgmStartTime ?? 0
  bgmFadeDuration.value = cfg.bgmFadeDuration ?? 1.5
  panelOpacity.value = Number(cfg.panelOpacity) || 0.92
  panelBgColor.value = cfg.panelBgColor || '#ffffff'
  panelBorderColor.value = cfg.panelBorderColor || '#66ccff'
}

let removeOpenListener = null
let removeResetListener = null

onMounted(async () => {
  /* 等待 C# 宿主桥接对象（WebView2 hostObjects）就绪 */
  await waitForBridge()

  /*
   * 注册 C# ResultWindow.DisplayResults() / Dismiss() 直接调用的全局回调：
   *   window.__onPickResultOpen({ results: [{ name, letterColor }], token })
   *   window.__onPickResultReset()
   * 页面未就绪时宿主的脚本会写入 window.__pendingPickResult，挂载后补处理。
   */
  window.__onPickResultOpen = async (payload) => {
    await loadSoundConfig()
    startSession(payload)
  }
  window.__onPickResultReset = () => {
    resetResultState({ stopSound: true, clearResults: true })
  }

  await loadSoundConfig()

  /* 处理宿主在 Vue 挂载前暂存的打开事件 */
  if (window.__pendingPickResult) {
    const pending = window.__pendingPickResult
    window.__pendingPickResult = null
    await loadSoundConfig()
    startSession(pending)
  }

  /* 读取宿主持有的当前/上一轮结果（预热时通常为空数组，不会触发显示） */
  const bridge = getNativeBridge()
  if (bridge) {
    try {
      const initial = parseJsonSafe(await bridge.GetLastResultsJson(), [])
      if (Array.isArray(initial) && initial.length) {
        startSession({ results: initial })
      }
    } catch (err) {
      console.warn('[PickResult] 读取宿主结果失败:', err)
    }
  } else if (window.pickResultApi && typeof window.pickResultApi.getResults === 'function') {
    const initial = await window.pickResultApi.getResults()
    if (initial && initial.length) {
      startSession({ results: initial })
    }
  }

  /* 兼容旧 Electron 事件通道（纯浏览器调试环境不存在） */
  if (window.pickResultApi && typeof window.pickResultApi.onOpen === 'function') {
    removeOpenListener = window.pickResultApi.onOpen(async (payload) => {
      await loadSoundConfig()
      startSession(payload)
    })
  }

  if (window.pickResultApi && typeof window.pickResultApi.onReset === 'function') {
    removeResetListener = window.pickResultApi.onReset(() => {
      resetResultState({ stopSound: true, clearResults: true })
    })
  }

  window.addEventListener('keydown', handleKeydown)
})

onBeforeUnmount(() => {
  window.removeEventListener('keydown', handleKeydown)
  clearTimers()
  stopGachaLoadingSound()
  stopBgm()
  if (typeof removeOpenListener === 'function') removeOpenListener()
  if (typeof removeResetListener === 'function') removeResetListener()
  /* 清理宿主回调，避免页面重载后残留旧闭包 */
  if (window.__onPickResultOpen) window.__onPickResultOpen = null
  if (window.__onPickResultReset) window.__onPickResultReset = null
})
</script>

<style scoped>
/*
 *  舞台容器：全屏透明，禁止背景点击关闭
 */
.result-stage {
  width: 100vw;
  height: 100vh;
  display: flex;
  align-items: center;
  justify-content: center;
  outline: none;
  background: rgba(0, 0, 0, 0.01);
  overflow: hidden;
  user-select: none;
}

.result-stage.is-closing {
  pointer-events: none;
  animation: result-fade-out 220ms ease forwards;
}

/* 中央面板纵向容器 */
.result-center {
  display: flex;
  flex-direction: column;
  align-items: center;
  max-width: 95vw;
}

/* 结果主面板 */
.result-panel {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 16px;
  padding: 24px 40px 20px;
  border-radius: 24px;
  background: rgba(255, 255, 255, 0.94);
  border: 4px solid #66ccff;
  box-shadow: 0 14px 38px rgba(6, 22, 48, 0.24);
  min-width: 360px;
  opacity: 0;
  transform: scale(0.85) translateY(16px);
  z-index: 1;
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

/* 信件行容器 */
.result-rows {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 36px;
}

.result-row {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 28px;
}

/* 信封卡片：4:3 比例，15°倾角飞入 */
.letter-card {
  position: relative;
  width: clamp(130px, 15vw, 196px);
  aspect-ratio: 4 / 3;
  opacity: 0;
  transform: scale(2.5) rotate(15deg);
  animation: letter-fly-in 0.6s ease-out forwards;
  animation-delay: calc(var(--index) * 0.12s);
}

/* 信封加深真实高斯投影 (GPU 合成线程硬件加速) */
.letter-img {
  width: 100%;
  height: 100%;
  object-fit: contain;
  filter: drop-shadow(0 14px 24px rgba(6, 22, 48, 0.40));
}

/* 姓名白卡 (信封中央浮现) */
.name-card {
  position: absolute;
  inset: 18% 10%;
  background: #ffffff;
  border-radius: 12px;
  display: flex;
  align-items: center;
  justify-content: center;
  text-align: center;
  padding: 6px 12px;
  font-size: clamp(16px, 2.1vw, 24px);
  font-weight: 700;
  color: #1c2741;
  box-shadow: 0 10px 24px rgba(6, 22, 48, 0.38);
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

/* 空结果提示 */
.result-empty {
  margin: 10px 0;
  font-size: 18px;
  font-weight: bold;
  color: #b9dcff;
  letter-spacing: 2px;
}

/* 底部关闭长条提示 (限制区域：仅在此处点击关闭) */
.result-close-bar {
  margin-top: 14px;
  padding: 8px 36px;
  border-radius: 999px;
  background: #66ccff;
  color: #ffffff;
  font-size: 14px;
  font-weight: 600;
  letter-spacing: 2px;
  box-shadow: 0 4px 14px rgba(102, 204, 255, 0.45);
  transition: all 0.25s cubic-bezier(0.2, 0.8, 0.2, 1);
  user-select: none;
  cursor: default;
  opacity: 0.65;
  display: inline-flex;
  align-items: center;
  justify-content: center;
}

.result-close-bar.is-ready {
  opacity: 1;
  cursor: pointer;
  background: #66ccff;
}

.result-close-bar.is-ready:hover {
  background: #4db8ff;
  transform: translateY(-2px);
  box-shadow: 0 6px 18px rgba(77, 184, 255, 0.65);
}

.result-close-bar.is-ready:active {
  transform: translateY(1px);
  box-shadow: 0 2px 8px rgba(77, 184, 255, 0.4);
}

/* 关键帧动画 */
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
