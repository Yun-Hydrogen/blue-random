<!--
================================================================================
  组件：Floating.vue
  所属：悬浮按钮窗口（主进程 BrowserWindow 直接加载，不经 vue-router）
  父组件：无（作为独立 Electron 窗口运行，路由为 /）

================================================================================
  一、功能概述
================================================================================
  本组件是悬浮按钮窗口的完整界面，包含三个视觉层次：

    层次          | 说明
    ──────────────┼──────────────────────────────────────────
    1. 悬浮按钮   | 可拖拽圆形按钮，居中显示，点击触发 picker
    2. 人数选择器 | 胶囊形控件条，悬浮按钮正上方，MIN/−/N/+/MAX
    3. 确认/取消  | 左右两侧圆形按钮（X 关闭 / ✓ 确认抽取）
  4. 鼠标穿透   | setShape 区域裁剪（矩形尺寸已优化为刚好贴合控件边缘）
  5. 淡入淡出   | 主进程 win.setOpacity() 窗口级动画

  2026-08-29 鼠标穿透回退 setShape 并优化矩形尺寸：
    - 方案 A（setIgnoreMouseEvents + forward 转发）实测：D3D9 下转发 mousemove
      不可用，窗口一直穿透、完全无法点击 → 回退 setShape（SetWindowRgn）
    - 优化矩形“刚好卡在控件边缘”：
        收缩态 = 正方形外接按钮圆（边长 sizePx+4）
        展开态 = 矩形紧贴 按钮 + 胶囊 + X/✓ 外缘
    - 保留 P1（挂载即应用初始 shape）、P9（hover class 控制 + blur 兜底）

  2026-07-20 架构变更：
    - D3D9 作为全模式默认渲染后端。
    - setShape（SetWindowRgn）回归，D3D9 下透明窗口不支持像素级穿透。
    - 淡入淡出回归 win.setOpacity()（D3D9 下 JS/CSS 动画不被合成器逐帧执行）。
    - 展开态允许拖拽悬浮按钮。
    - 窗口最小尺寸从 340px 降至 200px。
    - FloatingButton 子组件已内联到本文件。

================================================================================
  二、数据流架构
================================================================================

  +-------------------------------------------------------------+
  |  主进程 (main.js / ipc.js)                                    |
  |  - floatingButtonApi.getConfig() → 返回 floatingButton 配置   |
  |  - floatingPickerApi.getConfig() → 返回 pickCountDialog 配置  |
  |  - floatingPickerApi.confirm(count) → 触发随机抽取            |
  |  - floatingButtonApi.setExpanded / startDrag / moveDrag ...  |
  +--------------------------+----------------------------------+
                             | IPC (contextBridge via preload.js)
                             v
  +-------------------------------------------------------------+
  |  Floating.vue（本组件）                                       |
  |                                                             |
  |  状态（refs）：                                              |
  |    sizePx, transparencyPercent, iconDataUrl, iconSize        |
  |    borderColor, count, isPickerOpen, countPulse              |
  |    pointerDown, isDragging, startGlobalX, rAF ...            |
  |                                                             |
  |  核心计算（computed）：                                       |
  |    pickerMetrics — 环形布局的几何参数（半径、尺寸等）          |
  |    pickerStyle   — CSS 自定义属性注入                         |
  |    canDecrease / canIncrease — 人数边界判断                   |
  +-------------------------------------------------------------+

================================================================================
  三、picker 展开/收缩机制
================================================================================

  打开 picker（用户点击悬浮按钮）：
    1. isPickerOpen = true
    2. watch 触发 → setExpanded(true, windowSize) 窗口扩大到容纳环绕控件

  关闭 picker（用户点击 X / ✓ 或确认抽取）：
    1. isPickerOpen = false
    2. watch 触发 → setExpanded(false) 窗口收缩为按钮大小

================================================================================
  四、pickerMetrics 几何计算
================================================================================
  环绕人数选择器是一个环形排列的 UI，需要计算：
    - 按钮核心尺寸（sizePx）→ 确认/取消按钮大小（actionBtnSize）
    - 环形排列半径（placementRadius）→ 环半径（ringRadius）
    - 窗口总尺寸（windowSize = ringRadius × 2 + 40）

  所有尺寸通过 CSS 自定义属性（--size-px, --ring-radius 等）注入到模板，
  实现 JS 计算 + CSS 渲染的分离。

================================================================================
  五、主题与样式
================================================================================
  胶囊条：白底 + #66ccff 边框 + 人数格 #66ccff 蓝底白字
  关闭按钮：粉色系（#ff9494 背景 + 红色阴影）
  确认按钮：蓝色系（#66ccff 背景 + 蓝绿阴影）
  入场动画：0.22s 飞入（scale 0.85→1 + translateY 12px→0）
  退场动画：0.18s 淡出
  人数脉冲：0.25s scale 弹跳
  淡入淡出：主进程 win.setOpacity()，与渲染后端无关

================================================================================
  六、维护注意事项
================================================================================
  - pickerMetrics 和 windows.js 的 getFloatingButtonWindowSize 使用了相同的
    尺寸计算公式，修改任一处需同步另一处
  - CSS 自定义属性（--size-px 等）由 pickerStyle computed 注入，模板中通过 var() 引用
  - .picker-layer 不得设置 pointer-events: none，否则所有子元素无法点击
  - .floating-root 严禁 width/height: 100%
  - watch(isPickerOpen) 是处理窗口展开/收缩和 setShape 切换的关键
  - setShape（SetWindowRgn）会同时裁剪显示内容：展开态矩形必须覆盖全部控件；
    收起时需等 CSS leave 动画播完（~220ms）再切小矩形，否则控件被裁断
  - 展开态拖拽已放开（handlePointerDown/Move 无 isPickerOpen 门控）

  最后更新：2026-08-29
    - 修复打包后点击音效失效：fetch('/sound/button_click.wav') 绝对路径在
      file:// 下解析失败，改用 resolveAssetUrl 按协议拼接
================================================================================
-->
<template>
  <div
    class="floating-stage"
    :class="{ 'is-picker-open': isPickerOpen }"
    :style="pickerStyle"
  >

    <!-- ====== 第 1 层：悬浮按钮 ====== -->
    <div class="floating-root main-floating-btn">
      <button
        class="floating-button"
        :class="{ 'is-dragging': isDragging, 'is-hovered': hovered }"
        :style="buttonStyle"
        @contextmenu.prevent
        @pointerdown="handlePointerDown"
        @touchstart.prevent="handleTouchStart"
        @dragstart.prevent
        @pointerenter="hovered = true"
        @pointerleave="hovered = false"
        title=""
      >
        <img :src="iconSrc" alt="随机抽取" draggable="false" :style="iconStyle" />
      </button>
    </div>

    <!-- ====== 第 2 层：人数选择器 + 确认/取消（Vue Transition 控制进出动画） ====== -->
    <transition name="picker-pop">
      <div v-if="isPickerOpen" class="picker-layer" aria-live="polite">

        <!--
          胶囊形控件条：悬浮按钮正上方
          从左到右：MIN | − | 人数 | + | MAX
        -->
        <div class="picker-capsule">
          <button class="capsule-btn" :disabled="!canDecrease" @click="setMinCount" aria-label="最小">MIN</button>
          <span class="capsule-divider"></span>
          <button class="capsule-btn" :disabled="!canDecrease" @click="decreaseCount" aria-label="减少">-</button>
          <span class="capsule-divider"></span>
          <div class="capsule-count" :class="{ 'is-pulse': countPulse }">
            <span class="capsule-count-value">{{ count }}</span>
          </div>
          <span class="capsule-divider"></span>
          <button class="capsule-btn" :disabled="!canIncrease" @click="increaseCount" aria-label="增加">+</button>
          <span class="capsule-divider"></span>
          <button class="capsule-btn" :disabled="!canIncrease" @click="setMaxCount" aria-label="最大">MAX</button>
        </div>

        <!--
          X 关闭（左侧）与 ✓ 确认（右侧）圆形按钮
          通过 CSS transform rotate 定位到悬浮按钮左右两侧
        -->
        <div class="picker-actions">
          <button class="pos-node node-close" @click="handleClosePicker" aria-label="关闭">
            <svg viewBox="0 0 24 24" width="20" height="20" stroke="currentColor" stroke-width="2.5" fill="none"><path d="M18 6L6 18M6 6l12 12"/></svg>
          </button>
          <button class="pos-node node-confirm" @click="handleConfirm" aria-label="确认">
            <svg viewBox="0 0 24 24" width="22" height="22" stroke="currentColor" stroke-width="2.5" fill="none"><path d="M20 6L9 17l-5-5"/></svg>
          </button>
        </div>

      </div>
    </transition>

  </div>
</template>

<script setup>
// ============================================================
//  导入依赖
// ============================================================
import { ref, computed, watch, onMounted, onBeforeUnmount } from 'vue'
import { resolveAssetUrl } from '@/utils/asset'

// ============================================================
//  1. 点击音效（Web Audio API）
// ============================================================
const CLICK_SOUND_GAIN = 1
const audioContext = new (window.AudioContext || window.webkitAudioContext)()
const clickBufferPromise = fetch(resolveAssetUrl('/sound/button_click.wav'))
  .then(r => r.arrayBuffer())
  .then(buf => audioContext.decodeAudioData(buf.slice(0)))

function playClickSound() {
  clickBufferPromise.then(async (buffer) => {
    if (audioContext.state === 'suspended') await audioContext.resume()
    const src = audioContext.createBufferSource()
    src.buffer = buffer
    const gain = audioContext.createGain()
    gain.gain.value = CLICK_SOUND_GAIN
    src.connect(gain).connect(audioContext.destination)
    src.start(0)
  }).catch(() => {})
}

// ============================================================
//  2. 按钮外观状态（从 floatingButtonApi.getConfig() 加载）
// ============================================================
const sizePx              = ref(50)
const transparencyPercent = ref(20)
const iconDataUrl         = ref('')
const iconSize            = ref(48)
const borderColor         = ref('#66ccff')

const styleOpacity = computed(() =>
  Math.max(0, Math.min(1, 1 - transparencyPercent.value / 100)))

const buttonStyle = computed(() => ({
  width: `${sizePx.value}px`,
  height: `${sizePx.value}px`,
  opacity: String(styleOpacity.value),
  borderColor: borderColor.value || '#66ccff'
}))

const iconSrc = computed(() =>
  (iconDataUrl.value || '').trim() || './image/app.ico')

const iconStyle = computed(() => {
  const maxPx = Math.max(16, Math.min(iconSize.value, Math.round(sizePx.value * 0.8)))
  return { width: `${maxPx}px`, height: `${maxPx}px` }
})

// ============================================================
//  3. 拖拽状态
//
//  设计思路：
//    鼠标/触控笔走 Pointer Events → window 级监听（窗口移动时不丢事件）
//    触屏走 Touch Events → document 级监听（避免 pointer capture 坐标异常）
//    触屏使用指数平滑（TOUCH_MOVE_SMOOTHING）消除坐标抖动
//    鼠/触共用同一套 beginDrag / updateDrag / finishDrag 管线
// ============================================================
const DRAG_THRESHOLD_PX = 4            // 拖拽激活阈值（px）
const TOUCH_MOVE_SMOOTHING = 0.9    // 触屏坐标指数平滑系数（0-1，越大越跟手，越小越平滑）

/** 指针/触摸是否处于按下状态 */
const pointerDown       = ref(false)
/** Pointer Events 的 pointerId（仅 activeDragKind === 'pointer' 时有效） */
const activePointerId   = ref(null)
/** Touch Events 的 touch.identifier（仅 activeDragKind === 'touch' 时有效） */
const activeTouchId     = ref(null)
/** 当前拖拽通道类型：'none' | 'pointer' | 'touch' */
const activeDragKind    = ref('none')
/** 是否已经越过 DRAG_THRESHOLD_PX 进入拖拽模式 */
const isDragging        = ref(false)
/** hover 高亮（class 控制，便于在穿透/失焦时强制清除，避免残留） */
const hovered           = ref(false)
/** 拖拽起始坐标（屏幕坐标） */
const startGlobalX      = ref(0)
const startGlobalY      = ref(0)
/** 当前帧待 flush 的位移量 */
const pendingDx         = ref(0)
const pendingDy         = ref(0)
/** requestAnimationFrame 句柄 */
const rafId             = ref(0)

/** 触屏平滑位移缓存（不含响应式 —— 仅 updateDrag 读写） */
let touchSmoothedDx = 0
let touchSmoothedDy = 0

/**
 * 从 PointerEvent 提取屏幕全局坐标
 * 优先使用 screenX/Y（相对于物理屏幕左上角），
 * 回退到 window.screenX + clientX/Y（窗口模式下同样准确）
 */
function getGlobalPointFromPointer(event) {
  const sx = Number(event.screenX)
  const sy = Number(event.screenY)
  if (Number.isFinite(sx) && Number.isFinite(sy)) return { x: sx, y: sy }
  return { x: window.screenX + Number(event.clientX || 0), y: window.screenY + Number(event.clientY || 0) }
}

/**
 * 从 Touch 对象提取屏幕全局坐标（与 getGlobalPointFromPointer 相同逻辑）
 */
function getGlobalPointFromTouch(touch) {
  const sx = Number(touch.screenX)
  const sy = Number(touch.screenY)
  if (Number.isFinite(sx) && Number.isFinite(sy)) return { x: sx, y: sy }
  return { x: window.screenX + Number(touch.clientX || 0), y: window.screenY + Number(touch.clientY || 0) }
}

/**
 * 按 identifier 在 TouchList 中查找指定的 Touch 对象
 * Touch 的 identifier 在触摸周期内保持不变，用于追踪同一根手指
 */
function getTouchById(touchList, touchId) {
  if (!touchList || touchId === null || touchId === undefined) return null
  for (let index = 0; index < touchList.length; index += 1) {
    if (touchList[index].identifier === touchId) return touchList[index]
  }
  return null
}

function flushMove() {
  if (!isDragging.value || !window.floatingButtonApi) { rafId.value = 0; return }
  window.floatingButtonApi.moveDrag(pendingDx.value, pendingDy.value)
  rafId.value = 0
}

function scheduleMove() {
  if (rafId.value !== 0) return
  rafId.value = window.requestAnimationFrame(flushMove)
}

function cancelScheduledMove() {
  if (rafId.value !== 0) {
    window.cancelAnimationFrame(rafId.value)
    rafId.value = 0
  }
}

// ── 拖拽生命周期（鼠/触共用） ──

/** 重置所有拖拽状态并清理 RAF */
function resetDragState() {
  pointerDown.value = false
  activePointerId.value = null
  activeTouchId.value = null
  activeDragKind.value = 'none'
  isDragging.value = false
  pendingDx.value = 0
  pendingDy.value = 0
  touchSmoothedDx = 0
  touchSmoothedDy = 0
  cancelScheduledMove()
}

/** 移除指针拖拽的 window 级监听器 */
function removePointerDragListeners() {
  window.removeEventListener('pointermove', handleWindowPointerMove)
  window.removeEventListener('pointerup', handleWindowPointerUp)
  window.removeEventListener('pointercancel', handleWindowPointerCancel)
}

/** 移除触屏拖拽的 document 级监听器 */
function removeTouchDragListeners() {
  document.removeEventListener('touchmove', handleWindowTouchMove)
  document.removeEventListener('touchend', handleWindowTouchEnd)
  document.removeEventListener('touchcancel', handleWindowTouchCancel)
}

/**
 * 开始拖拽 —— 记录起始坐标并注册对应的全局监听器
 * @param {{x:number, y:number}} point 起始屏幕坐标
 * @param {'pointer'|'touch'} kind 拖拽通道类型
 * @param {number} identifier pointerId 或 touch.identifier
 */
function beginDrag(point, kind, identifier) {
  pointerDown.value = true
  activeDragKind.value = kind
  activePointerId.value = kind === 'pointer' ? identifier : null
  activeTouchId.value = kind === 'touch' ? identifier : null
  isDragging.value = false
  startGlobalX.value = point.x
  startGlobalY.value = point.y
  pendingDx.value = 0
  pendingDy.value = 0
  touchSmoothedDx = 0
  touchSmoothedDy = 0
  cancelScheduledMove()
}

/**
 * 检查位移是否超过阈值，若超过则进入拖拽模式
 * @param {number} dx 累计位移 X
 * @param {number} dy 累计位移 Y
 */
function ensureDragStarted(dx, dy) {
  if (!isDragging.value && (Math.abs(dx) >= DRAG_THRESHOLD_PX || Math.abs(dy) >= DRAG_THRESHOLD_PX)) {
    isDragging.value = true
    if (window.floatingButtonApi) {
      window.floatingButtonApi.startDrag()
    }
  }
}

/**
 * 更新拖拽位移并排帧发送到主进程
 * 触屏输入使用指数平滑（TOUCH_MOVE_SMOOTHING）消除坐标抖动
 * @param {{x:number, y:number}} point 当前屏幕坐标
 * @param {boolean} isTouchInput 是否为触屏输入
 */
function updateDrag(point, isTouchInput) {
  if (!pointerDown.value || !window.floatingButtonApi) return

  const dx = point.x - startGlobalX.value
  const dy = point.y - startGlobalY.value

  ensureDragStarted(dx, dy)
  if (!isDragging.value) return

  if (isTouchInput) {
    // 指数平滑: newValue += (rawValue - oldSmoothed) × α
    touchSmoothedDx += (dx - touchSmoothedDx) * TOUCH_MOVE_SMOOTHING
    touchSmoothedDy += (dy - touchSmoothedDy) * TOUCH_MOVE_SMOOTHING
    pendingDx.value = touchSmoothedDx
    pendingDy.value = touchSmoothedDy
  } else {
    pendingDx.value = dx
    pendingDy.value = dy
  }

  scheduleMove()
}

/**
 * 结束拖拽或触发点击
 * 若曾拖拽 → 发送最终位移并 endDrag；若未拖拽 → 视为点击
 * @param {boolean} shouldClick 未拖拽时是否触发点击
 */
function finishDrag(shouldClick) {
  if (isDragging.value && window.floatingButtonApi) {
    cancelScheduledMove()
    window.floatingButtonApi.moveDrag(pendingDx.value, pendingDy.value)
    window.floatingButtonApi.endDrag()
  } else if (shouldClick) {
    playClickSound()
    handleFloatingButtonClick()
  }
  resetDragState()
}

// ============================================================
//  4. 拖拽事件处理器
//
//  关键设计决策：
//    ── Pointer Events（鼠标/触控笔）──
//    鼠标移动非常流畅，无需平滑处理。
//    监听器挂在 window 上而非元素上：
//      1) 浮窗移动时鼠标可能离开按钮，window 级监听不会丢事件
//      2) 不需要 setPointerCapture / releasePointerCapture
//
//    ── Touch Events（手指触屏）──
//    触屏坐标存在固有抖动（像素级），pointermove 在 Electron 下
//    传递的坐标也有此问题。改用原生 Touch Events：
//      1) 直接读取 touch.screenX/Y，避免 pointer 坐标合成误差
//      2) 通过 touch.identifier 追踪同一根手指，防止多指干扰
//      3) 监听器挂在 document 上（touchmove 默认只在目标元素触发）
//    Touch Events 使用 { passive: false } + preventDefault() 阻止
//    页面滚动，确保拖拽不被浏览器手势打断。
//
//    ── 指数平滑 ──
//    touchSmoothedDx += (rawDx - touchSmoothedDx) × 0.35
//    α=0.35 经过实测，既消除抖动又保持响应速度。
//    该值对鼠标/触控笔不生效（isTouchInput=false 时直接使用原始值）
// ============================================================

/**
 * 鼠标/触控笔按下
 * 排除触屏（交 handleTouchStart 处理）和右键
 * 注册 window 级 pointermove/pointerup/pointercancel
 */
function handlePointerDown(event) {
  if (event.pointerType === 'touch') return
  if (event.pointerType === 'mouse' && event.button !== 0) return
  if (pointerDown.value) return

  beginDrag(getGlobalPointFromPointer(event), 'pointer', event.pointerId)
  window.addEventListener('pointermove', handleWindowPointerMove)
  window.addEventListener('pointerup', handleWindowPointerUp)
  window.addEventListener('pointercancel', handleWindowPointerCancel)
}

/**
 * 手指按下（touchstart）
 * 用 event.preventDefault() 阻止浏览器默认行为（滚动/缩放）
 * 注册 document 级 touchmove/touchend/touchcancel
 */
function handleTouchStart(event) {
  if (pointerDown.value) return
  const touch = event.touches && event.touches[0] ? event.touches[0] : null
  if (!touch) return

  event.preventDefault()
  beginDrag(getGlobalPointFromTouch(touch), 'touch', touch.identifier)
  document.addEventListener('touchmove', handleWindowTouchMove, { passive: false })
  document.addEventListener('touchend', handleWindowTouchEnd)
  document.addEventListener('touchcancel', handleWindowTouchCancel)
}

/** window 级 pointermove —— 仅响应 activeDragKind === 'pointer' 的事件 */
function handleWindowPointerMove(event) {
  if (activeDragKind.value !== 'pointer' || activePointerId.value !== event.pointerId) return
  updateDrag(getGlobalPointFromPointer(event), false)
}

/** window 级 pointerup —— 结束拖拽或触发点击 */
function handleWindowPointerUp(event) {
  if (activeDragKind.value !== 'pointer' || activePointerId.value !== event.pointerId) return
  finishDrag(!isDragging.value)
  removePointerDragListeners()
}

/** window 级 pointercancel —— 异常中断时清理状态 */
function handleWindowPointerCancel(event) {
  if (activeDragKind.value !== 'pointer' || activePointerId.value !== event.pointerId) return
  resetDragState()
  removePointerDragListeners()
}

/** document 级 touchmove —— 按 touch.identifier 追踪，调用 updateDrag 并开启平滑 */
function handleWindowTouchMove(event) {
  const touch = getTouchById(event.touches, activeTouchId.value)
  if (!touch || activeDragKind.value !== 'touch') return
  event.preventDefault()
  updateDrag(getGlobalPointFromTouch(touch), true)
}

/** document 级 touchend —— 检查 changedTouches 确认是该手指抬起 */
function handleWindowTouchEnd(event) {
  const touch = getTouchById(event.changedTouches, activeTouchId.value)
  if (!touch || activeDragKind.value !== 'touch') return
  event.preventDefault()
  finishDrag(!isDragging.value)
  removeTouchDragListeners()
}

/** document 级 touchcancel —— 异常中断时清理状态 */
function handleWindowTouchCancel() {
  if (activeDragKind.value !== 'touch') return
  resetDragState()
  removeTouchDragListeners()
}

// ============================================================
//  5. 人数选择器状态
// ============================================================
const count = ref(1)
const isPickerOpen = ref(false)
const countPulse = ref(false)
const MIN_COUNT = 1
const MAX_COUNT = 10
let pulseTimer = null

function clampInt(value, min, max, fallback) {
  const n = Number(value)
  if (!Number.isFinite(n)) return fallback
  return Math.max(min, Math.min(max, Math.round(n)))
}

// ============================================================
//  6. 环形布局几何计算（pickerMetrics）
// ============================================================
const pickerMetrics = computed(() => {
  const base = Math.max(40, sizePx.value)
  const actionBtnSize = Math.round(Math.max(36, base * 0.7))
  const btnSize = actionBtnSize
  const ringThickness = actionBtnSize
  const countSize = actionBtnSize
  const placementRadius = Math.round(Math.max(55, base * 1.05))
  const ringRadius = placementRadius + Math.round(ringThickness / 2)
  const windowSize = Math.round(Math.max(200, ringRadius * 2 + 40))
  const arcSpan = 145
  const arcCenter = windowSize / 2
  const circum = 2 * Math.PI * placementRadius
  const arcLength = (arcSpan / 360) * circum

  return {
    actionBtnSize,
    placementRadius,
    ringRadius,
    ringThickness,
    countSize,
    btnLargeSize: btnSize,
    btnSmallSize: btnSize,
    windowSize,
    arcSpan,
    arcCenter,
    circum,
    arcLength
  }
})

// ============================================================
//  7. CSS 自定义属性注入（pickerStyle）
// ============================================================
const pickerStyle = computed(() => ({
  '--size-px': `${sizePx.value}px`,
  '--ring-radius': `${pickerMetrics.value.ringRadius}px`,
  '--placement-radius': `${pickerMetrics.value.placementRadius}px`,
  '--ring-thickness': `${pickerMetrics.value.ringThickness}px`,
  '--count-size': `${pickerMetrics.value.countSize}px`,
  '--btn-l-size': `${pickerMetrics.value.btnLargeSize}px`,
  '--btn-s-size': `${pickerMetrics.value.btnSmallSize}px`,
  '--action-size': `${pickerMetrics.value.actionBtnSize}px`,
  '--arc-span': `${pickerMetrics.value.arcSpan}deg`
}))

const canDecrease = computed(() => count.value > MIN_COUNT)
const canIncrease = computed(() => count.value < MAX_COUNT)

// ============================================================
//  8. 人数脉冲动画
// ============================================================
function triggerPulse() {
  countPulse.value = false
  if (pulseTimer) clearTimeout(pulseTimer)
  window.requestAnimationFrame(() => { countPulse.value = true })
  pulseTimer = window.setTimeout(() => { countPulse.value = false }, 260)
}

// ============================================================
//  9. 初始化配置（从主进程加载）
// ============================================================
async function initConfig() {
  if (window.floatingButtonApi) {
    const cfg = await window.floatingButtonApi.getConfig()
    sizePx.value              = Math.round(50 * (cfg.sizePercent / 100))
    transparencyPercent.value = cfg.transparencyPercent
    iconDataUrl.value         = cfg.iconDataUrl || ''
    iconSize.value            = cfg.iconSize || 48
    borderColor.value         = cfg.borderColor || '#66ccff'
  }

  if (window.floatingPickerApi) {
    const pickCfg = await window.floatingPickerApi.getConfig()
    count.value = clampInt(pickCfg.defaultCount, MIN_COUNT, MAX_COUNT, MIN_COUNT)
  }
}

// ============================================================
//  10. picker 开关逻辑
// ============================================================
function handleFloatingButtonClick() { isPickerOpen.value = !isPickerOpen.value }
function handleClosePicker()         { isPickerOpen.value = false }

function handleConfirm() {
  const selectedCount = clampInt(count.value, MIN_COUNT, MAX_COUNT, MIN_COUNT)
  isPickerOpen.value = false
  /*
   * 延迟触发抽取，让 shape 先收缩为按钮大小（~220ms），
   * 再由主进程 fadeOut。避免大窗口下淡出被截断。
   */
  setTimeout(() => {
    if (window.floatingPickerApi) {
      window.floatingPickerApi.confirm(selectedCount)
    }
  }, 250)
}

// ============================================================
//  11. 人数增减操作
// ============================================================
function increaseCount() { if (canIncrease.value) { count.value += 1; triggerPulse() } }
function decreaseCount() { if (canDecrease.value) { count.value -= 1; triggerPulse() } }
function setMinCount()   { if (count.value !== MIN_COUNT) { count.value = MIN_COUNT; triggerPulse() } }
function setMaxCount()   { if (count.value !== MAX_COUNT) { count.value = MAX_COUNT; triggerPulse() } }

// ============================================================
//  12. 鼠标穿透（setShape 区域裁剪）
//
//  方案 A（setIgnoreMouseEvents + forward 转发）实测：D3D9 下
//  forward 转发的 mousemove 不可用 → 窗口一直穿透、完全无法点击。
//  回退到 setShape（SetWindowRgn）：
//    收缩态：正方形外接按钮圆（边长 = sizePx + 4，刚好卡在按钮边缘）
//    展开态：一个矩形刚好包住 按钮 + 胶囊 + X/✓（紧贴各控件外缘）
//  矩形外区域自动穿透鼠标事件，零性能开销。
//  注意：SetWindowRgn 会裁剪显示内容，收起时需等 CSS leave 动画播完
//        （~220ms）再切小矩形，避免控件被裁断。
// ============================================================

const shapeExpanded = ref(false)
/* 收起时延迟收缩 shape（等 CSS leave 动画播完，避免内容被裁剪截断） */
let shapeCloseTimer = null

function applyShape(expanded) {
  /* 切换 shape 时强制清除 hover 态（鼠标可能已移出命中区） */
  hovered.value = false
  if (!window.floatingButtonApi || typeof window.floatingButtonApi.setShape !== 'function') return
  const w = pickerMetrics.value.windowSize
  const c = w / 2                     // 窗口中心（按钮圆心）
  const half = sizePx.value / 2       // 按钮半径
  const pad = 2                       // 边缘容差（刚好贴合且不掉点）

  if (expanded) {
    /* 展开态：矩形刚好包住 按钮 + 胶囊 + X/✓（紧贴控件外缘） */
    const rad = pickerMetrics.value.placementRadius
    /* X/✓ 实际直径 = actionSize*1.2（.node-close/.node-confirm），半径 = 0.6 */
    const actR = pickerMetrics.value.actionBtnSize * 0.6
    const ringH = pickerMetrics.value.ringThickness
    /* 胶囊顶：top:50% + translateY(-50% - half - ringH/2 - 8px) → c - half - ringH - 8 */
    const top = c - half - ringH - 8 - pad
    const bottom = c + half + pad
    const left = c - rad - actR - pad
    const right = c + rad + actR + pad
    window.floatingButtonApi.setShape([{
      x: Math.round(left),
      y: Math.round(top),
      width: Math.round(right - left),
      height: Math.round(bottom - top)
    }])
  } else {
    /* 收缩态：正方形外接按钮圆（边长 = sizePx + 4，每边 2px 容差） */
    const bs = sizePx.value + 4
    const bx = Math.round((w - bs) / 2)
    const by = Math.round((w - bs) / 2)
    window.floatingButtonApi.setShape([{ x: bx, y: by, width: bs, height: bs }])
  }
  shapeExpanded.value = expanded
}

watch(isPickerOpen, (open) => {
  if (!window.floatingButtonApi) return
  if (shapeCloseTimer) { clearTimeout(shapeCloseTimer); shapeCloseTimer = null }

  if (open) {
    count.value = MIN_COUNT
    applyShape(true)
  } else {
    /* 延迟收缩 shape：setShape 会裁剪显示内容，需等 CSS leave 动画播完再切小矩形 */
    shapeCloseTimer = setTimeout(() => {
      applyShape(false)
      shapeCloseTimer = null
    }, 220)
  }

  if (typeof window.floatingButtonApi.setExpanded === 'function') {
    window.floatingButtonApi.setExpanded(open)
  }
})

/* P1：immediate —— 挂载时立即应用初始 shape（默认配置下 sizePx 不变不会触发普通 watch） */
watch([sizePx, pickerMetrics], () => {
  if (shapeExpanded.value) applyShape(true)
  else applyShape(false)
}, { immediate: true })

// ============================================================
//  13. 生命周期
// ============================================================

/* P9：清除 hover 态（窗口失焦兜底，鼠标可能已离开命中区） */
function clearHover() { hovered.value = false }

onMounted(() => {
  initConfig()
  window.addEventListener('blur', clearHover)
})
onBeforeUnmount(() => {
  window.removeEventListener('blur', clearHover)
  removePointerDragListeners()
  removeTouchDragListeners()
  resetDragState()
})
</script>

<style scoped>
/*
 *  本文件 CSS 为 scoped。
 *  按功能分为 8 个分组，每组以分隔注释标识。
 *  大量使用 CSS 自定义属性（--size-px 等），由 pickerStyle computed 注入。
 */

/* =================================================================
   1. 舞台容器
   ================================================================= */

/*
 *  全屏 grid 容器，子元素居中（place-items: center）。
 *  overflow: hidden 防止环形控件超出窗口边界。
 */
.floating-stage {
  position: relative;
  width: 100%;
  height: 100%;
  display: grid;
  place-items: center;
  font-family: 'Bahnschrift', 'Segoe UI Variable', 'Microsoft YaHei UI', 'PingFang SC', sans-serif;
  overflow: hidden;

  /*
   * 淡入淡出由主进程 win.setOpacity() 驱动（SetLayeredWindowAttributes），
   * 渲染进程不参与动画。
   */
  opacity: 1;
}

/* 悬浮按钮在舞台中的层级（高于 picker-layer 的 z-index: 5） */
.main-floating-btn {
  z-index: 10;
  position: relative;
}

/* =================================================================
   2. 悬浮按钮本体
   ================================================================= */

.floating-root {
  display: flex;
  align-items: center;
  justify-content: center;
}

.floating-button {
  position: relative;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: auto;
  height: auto;
  padding: 10px;
  border: 2px solid #66ccff;
  border-radius: 50%;
  outline: none;
  cursor: pointer;
  background: #fff;
  touch-action: none;
  -webkit-tap-highlight-color: transparent;
  transition: transform 260ms ease, box-shadow 260ms ease, background 260ms ease, border-color 260ms ease;
}

.floating-button.is-hovered:not(.is-dragging) {
  background: #fff;
  border-color: rgba(40, 130, 230, 0.9);
}

.floating-button.is-dragging {
  transition: none;
}

.floating-button img {
  width: 120%;
  height: 120%;
  object-fit: contain;
  pointer-events: none;
}

/* =================================================================
   3. picker 层（含胶囊条 + X/✓ 按钮）
   ================================================================= */

/*
 *  绝对定位覆盖整个窗口。
 *  不得设置 pointer-events: none（否则所有子元素无法点击）。
 */
.picker-layer {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 5;
}

/* =================================================================
   4. 胶囊形控件条（悬浮按钮正上方）
   ================================================================= */

/*
 *  定位：按钮上方 8px 处。
 *  transform: translate(-50%, calc(-50% - buttonHalf - ringHalf - 8px))
 *  白底 + #66ccff 边框 + 999px 圆角 = 完美胶囊形。
 */
.picker-capsule {
  position: absolute;
  top: 50%;
  left: 50%;
  transform: translate(-50%, calc(-50% - var(--size-px) / 2 - var(--ring-thickness) / 2 - 8px));
  display: flex;
  align-items: center;
  justify-content: center;
  height: var(--ring-thickness);
  padding: 0 2px;
  border-radius: 999px;
  background: #fff;
  border: 2px solid #66ccff;
  pointer-events: auto;
  z-index: 7;
  white-space: nowrap;
}

/*
 *  胶囊内的按钮（MIN / - / + / MAX）。
 *  透明背景，hover 时浅蓝底，disabled 时灰化。
 */
.capsule-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  height: calc(var(--ring-thickness) - 6px);
  min-width: calc(var(--ring-thickness) * 0.7);
  padding: 0 6px;
  border: none;
  background: transparent;
  color: #4477aa;
  font-weight: 600;
  font-size: clamp(10px, calc(var(--ring-thickness) * 0.28), 13px);
  font-family: 'Bahnschrift', 'Segoe UI Variable', 'Microsoft YaHei UI', sans-serif;
  cursor: pointer;
  outline: none;
  border-radius: 999px;
  letter-spacing: 0.3px;
  transition: all 0.2s ease;
}

.capsule-btn:not(:disabled):hover {
  color: #1a6090;
  background: rgba(102, 180, 255, 0.12);
}

.capsule-btn:not(:disabled):active {
  color: #0d4a78;
  background: rgba(102, 180, 255, 0.2);
}

.capsule-btn:disabled {
  opacity: 0.3;
  cursor: not-allowed;
  filter: grayscale(1);
}

/* - / + 按钮：字体更大，确保 UI 重心突出 */
.capsule-btn:nth-of-type(2),
.capsule-btn:nth-of-type(3) {
  font-size: clamp(24px, calc(var(--ring-thickness) * 0.42), 24px);
  min-width: calc(var(--ring-thickness) * 0.65);
  line-height: 1;
}

/* 竖线分隔符（MIN | − | 人数 | + | MAX 中的 |） */
.capsule-divider {
  display: inline-block;
  width: 1px;
  height: calc(var(--ring-thickness) * 0.45);
  background: rgba(102, 170, 220, 0.3);
  flex-shrink: 0;
}

/*
 *  当前人数显示格。
 *  #66ccff 蓝底 + 白色粗体数字 + 圆角矩形。
 *  align-self: stretch 撑满胶囊高度。
 */
.capsule-count {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  align-self: stretch;
  min-width: calc(var(--ring-thickness) * 0.9);
  padding: 0 8px;
  background: #66ccff;
  border-radius: 8px;
  color: #ffffff;
  font-size: clamp(16px, calc(var(--ring-thickness) * 0.42), 26px);
  font-weight: 700;
  font-family: 'Bahnschrift', 'Segoe UI Variable', 'Microsoft YaHei UI', sans-serif;
}

.capsule-count-value {
  line-height: 1;
}

/* 人数脉冲动画：缩放弹跳 0.25s */
.capsule-count.is-pulse .capsule-count-value {
  animation: txt-pulse 0.25s ease-out;
}

/* =================================================================
   5. X / ✓ 操作按钮容器
   ================================================================= */

/*
 *  绝对定位在窗口中心，width/height 为 0（不占空间），
 *  子按钮通过 transform 位移到悬浮按钮左右两侧。
 *  pointer-events: none 使容器本身不拦截鼠标事件。
 */
.picker-actions {
  position: absolute;
  top: 50%;
  left: 50%;
  width: 0;
  height: 0;
  pointer-events: none;
}

/*
 *  确认/取消按钮的基础样式。
 *  transform: translate(-50%, -50%) rotate(±90deg) translateY(-radius) rotate(∓90deg)
 *  这个组合实现"以悬浮按钮为中心，左右各偏移 radius 距离"的定位。
 */
.pos-node {
  position: absolute;
  top: 50%;
  left: 50%;
  display: flex;
  align-items: center;
  justify-content: center;
  border-radius: 50%;
  pointer-events: auto;
  font-weight: bold;
  cursor: pointer;
  outline: none;
  transition: all 0.25s cubic-bezier(0.2, 0.8, 0.2, 1);
}

/* X 关闭按钮：粉色系（#ff9494 背景 + 红色阴影） */
.node-close {
  width: calc(var(--action-size) * 1.2);
  height: calc(var(--action-size) * 1.2);
  transform: translate(-50%, -50%) rotate(-90deg) translateY(calc(-1 * var(--placement-radius))) rotate(90deg);
  border-radius: 50%;
  border: 2px solid #ffb0b9;
  background: #ff9494;
  box-shadow: 0 4px 12px rgba(255, 120, 140, 0.3);
  color: #ffffff;
}

.node-close:hover {
  transform: translate(-50%, -50%) rotate(-90deg) translateY(calc(-1 * var(--placement-radius))) rotate(90deg) !important;
  border-color: rgba(255, 90, 110, 1);
  box-shadow: 0 4px 16px rgba(255, 90, 110, 0.5);
  filter: none;
}

/* ✓ 确认按钮：蓝色系（#66ccff 背景 + 蓝绿阴影） */
.node-confirm {
  width: calc(var(--action-size) * 1.2);
  height: calc(var(--action-size) * 1.2);
  transform: translate(-50%, -50%) rotate(90deg) translateY(calc(-1 * var(--placement-radius))) rotate(-90deg);
  border-radius: 50%;
  border: 2px solid rgba(166, 233, 255, 0.6);
  background: #66ccff;
  box-shadow: 0 4px 12px rgba(102, 204, 255, 0.3);
  color: #ffffff;
}

.node-confirm:hover {
  transform: translate(-50%, -50%) rotate(90deg) translateY(calc(-1 * var(--placement-radius))) rotate(-90deg) !important;
  border-color: #17e3f2;
  box-shadow: 0 4px 16px rgba(80, 255, 140, 0.5);
  filter: none;
}

/* =================================================================
   6. picker 进出场动画（Vue Transition）
   ================================================================= */

/* 入场：0.22s 飞入（缩小 + 下移 → 正常） */
.picker-pop-enter-active {
  transition: all 0.22s cubic-bezier(0.2, 0.8, 0.2, 1);
}

/* 退场：0.18s 淡出 */
.picker-pop-leave-active {
  transition: all 0.18s cubic-bezier(0.5, 0, 0.1, 1);
}

.picker-pop-enter-from,
.picker-pop-leave-to {
  opacity: 0;
  transform: scale(0.85) translateY(12px);
}

/* =================================================================
   7. 人数脉冲动画 keyframes
   ================================================================= */
@keyframes txt-pulse {
  0%   { transform: scale(1); }
  50%  { transform: scale(1.25); }
  100% { transform: scale(1); }
}
</style>
