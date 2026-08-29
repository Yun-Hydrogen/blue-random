<!--
  组件：TabFloating.vue
  所属：配置面板 - 悬浮按钮 Tab
  父组件：ConfigPanel.vue（通过 props 传入数据，通过 emit 传出修改）

  功能概述：
    1. 按钮样式   —— 大小百分比 / 屏幕位置 X,Y / 持续置顶开关
    2. 中心图标   —— 文件选择器（上传到 customs/ 目录）+ 圆形预览 + 图标尺寸滑条
    3. 边框颜色   —— rizui_colorpicker 组件（HSL 调色盘 Dialog）
    4. 滑条填充   —— rizui_slider 组件自动处理轨道渐变

  数据流：
    父组件 ConfigPanel 持有 draft.floatingButton 对象
    本组件通过 props.fb 接收完整的 floatingButton 配置
    任何修改通过 emit('update:fb', newFb) 把整个新对象传回父组件

  主题色：初音绿 #39c5bb，通过 tabTheme 变量传给 rizui_switch / rizui_colorpicker

  图标存储方案（customs/ 目录）：
    - 用户点击「选择图片」→ FileReader 读取为 base64 → 通过 IPC 上传到
      <配置根目录>/customs/custom_<时间戳>.yml（含 fileName/mimeType/purpose/base64）
    - config.yml 只保存引用 floatingButton.customIconId，不再内嵌大段 base64
    - 预览：TabFloating 通过 configPanelApi.getCustom() 按 id 拉取 base64 显示
    - 悬浮按钮窗口通过 floating-button:get-config 由主进程解析为 data URL 显示
    - 旧版内嵌 iconDataUrl 由 customs.ensureInitialized() 启动时自动迁移

  注意事项：
    - 必须用扩展运算符 {...props.fb, key: newValue} 创建新对象触发响应式
    - 滑条默认值兜底：sizePercent||100, iconSize||48，防止首次渲染 NaN
    - 图标文本框为 readonly 模式，显示 customIconId 引用（无手动编辑意义）
    - 颜色选择器由 rizui_colorpicker 统一管理（HSL 转换 / 拖拽 / Dialog 动画）

  更新记录（2026-08-29）：
    - 自定义图标改为 customs/ 目录存储：选图后上传到 customs/custom_<时间戳>.yml，
      config.yml 只存 floatingButton.customIconId 引用；预览按 id 从 customs 拉取；
      “恢复默认”删除对应资源文件
    - 上传改为内存暂存：选图先暂存主进程内存（不落盘），“应用”时才写入 customs/，
      未应用关闭面板即舍弃；恢复默认不再立即删资源（应用时统一处理）
    - 修复“实时位置”X 不自动读取：原 refreshLivePosition 通过 posX.value/posY.value
      分别赋值会触发两次独立 emit('update:fb')，第二次基于未更新的 props.fb 会把 X
      覆盖回旧值；改为一次合并 emit({ position: { x, y } })
-->

<template>
  <div class="tab-page floating-tab">
    <!-- 一、按钮样式 -->
    <rizui_card title="行为" desc="调整悬浮按钮的行为" icon="fa-solid fa-toggle-on">

      <rizui_cfgrow label="位置 X / Y" hint="实时显示悬浮按钮当前位置，可直接编辑；在配置面板中“应用”时随配置一起保存">
        <div class="cfg-xy-group">
          <rizui_text v-model="posX" type="number" placeholder="X" :color="tabTheme" style="width: 90px; text-align: center;" />
          <rizui_text v-model="posY" type="number" placeholder="Y" :color="tabTheme" style="width: 90px; text-align: center;" />
        </div>
      </rizui_cfgrow>

      <rizui_cfgrow label="持续置顶" hint="启用后开启悬浮按钮的置顶功能">
        <rizui_switch :model-value="fb.alwaysOnTop" :color="tabTheme" @update:model-value="$emit('update:fb',{...fb,alwaysOnTop:$event})" />
      </rizui_cfgrow>

      <rizui_cfgrow label="任务栏可见" hint="悬浮按钮是否显示在任务栏中，可能解决多桌面下桌面意外跳转的问题">
        <rizui_switch :model-value="fb.showInTaskbar" :color="tabTheme" @update:model-value="$emit('update:fb',{...fb,showInTaskbar:$event})" />
      </rizui_cfgrow>
    </rizui_card>

    <!-- 二、自定义 -->
    <rizui_card title="样式" desc="按钮中心图标、边框颜色等个性化设置" icon="fa-solid fa-star">

      <!-- 中心图标 -->
      <rizui_cfgrow label="按钮大小" hint="缩放百分比，100% 为默认尺寸">
        <rizui_slider :model-value="fb.sizePercent || 100" :min="50" :max="200" :color="tabTheme" display="%" @update:model-value="$emit('update:fb',{...fb,sizePercent:$event})" />
      </rizui_cfgrow>

      <rizui_cfgrow label="中心图标" hint="按钮中央显示的图片，点击右侧按钮选择本地图片" stack>
        <div class="icon-picker-row">
          <div class="icon-actions">
            <div class="icon-path-row">
              <rizui_text
                :model-value="fb.customIconId"
                readonly
                :placeholder="iconPlaceholder"
                :color="tabTheme"
                style="flex: 1; min-width: 0; text-align: left;"
              />
              <rizui_button text="选择图片" primary :color="tabTheme" @click="iconFileInput?.click()" />
              <input ref="iconFileInput" type="file" accept="image/*" @change="handleIconPick" style="display:none" />
            </div>
            <div class="icon-path-row icon-path-sub">
              <span class="cfg-hint">图片将以 base64 存储到配置文件中</span>
              <rizui_button text="恢复默认" :color="tabTheme" @click="resetIcon" />
            </div>
          </div>
          <div class="icon-preview-box">
            <img :src="iconPreviewSrc" class="icon-preview-img" :style="{ width: effectivePreviewSize + 'px', height: effectivePreviewSize + 'px' }" />
          </div>
        </div>
      </rizui_cfgrow>

      <!-- 图标大小 -->
      <rizui_cfgrow label="图标大小" hint="中心图标在按钮内的显示尺寸（实际不超过按钮的 80%）">
        <rizui_slider :model-value="fb.iconSize || 48" :min="16" :max="96" :color="tabTheme" display="px" @update:model-value="$emit('update:fb',{...fb,iconSize:$event})" />
      </rizui_cfgrow>

      <!-- 边框颜色 -->
      <rizui_cfgrow label="按钮边框颜色" hint="悬浮圆形按钮的外圈描边颜色">
        <rizui_colorpicker v-model="fb.borderColor" :color="tabTheme" teleport=".config-left" />
      </rizui_cfgrow>
    </rizui_card>
  </div>
</template>

<script setup>
/*
 *  组件逻辑概览（按代码顺序）：
 *  1. props / emit      —— 与父组件通信的接口
 *  2. 图标预览          —— 直接使用存储的 base64 data URL（iconPreviewSrc）
 *  3. 文件选择          —— FileReader.readAsDataURL() 将图片转 base64 存入 iconDataUrl
 *  4. 图标重置          —— 清空 iconDataUrl 恢复默认图标
 */
import { computed, ref, onMounted, onBeforeUnmount, watch } from 'vue'
import { rizui_card, rizui_cfgrow, rizui_switch, rizui_slider, rizui_colorpicker, rizui_text, rizui_button } from 'riz-ui'

/* Tab 主题色 */
const tabTheme = '#39c5bb'

/*
 *  props：父组件传入的数据
 *  fb —— floatingButton 配置对象，包含以下字段：
 *    sizePercent   (number)  按钮缩放百分比，默认 100
 *    alwaysOnTop   (boolean) 是否持续置顶
 *    position.x    (number|null) 屏幕 X 坐标，null=自动
 *    position.y    (number|null) 屏幕 Y 坐标，null=自动
 *    customIconId  (string)  自定义图标引用（customs/ 目录下的资源 id），空=使用默认图标
 *    iconSize      (number)  图标显示尺寸(px)，默认 48
 *    borderColor   (string)  边框颜色，默认 '#ffffff'
 */
const props = defineProps({ fb: Object })

/*
 *  emit：向父组件发送数据变更事件
 *  update:fb —— 悬浮按钮配置发生变化
 *  注意：必须传递完整的新对象（用扩展运算符创建），不能直接修改 props.fb 的属性
 */
const emit = defineEmits(['update:fb'])

/* 悬浮按钮位置：默认实时跟随（主进程轮询）；用户编辑后以手动值为准 */
const userEditedPosition = ref(false)
let programmaticSync = false
let positionTimer = null

/* v-model 桥接：位置输入框 ↔ props.fb.position（空值 → null） */
const posX = computed({
  get: () => props.fb.position?.x ?? null,
  set: (val) => {
    if (!programmaticSync) userEditedPosition.value = true
    const n = Number(val)
    emit('update:fb', { ...props.fb, position: { ...props.fb.position, x: val === null || val === '' || Number.isNaN(n) ? null : Math.round(n) } })
  }
})

const posY = computed({
  get: () => props.fb.position?.y ?? null,
  set: (val) => {
    if (!programmaticSync) userEditedPosition.value = true
    const n = Number(val)
    emit('update:fb', { ...props.fb, position: { ...props.fb.position, y: val === null || val === '' || Number.isNaN(n) ? null : Math.round(n) } })
  }
})

/* 实时读取悬浮按钮当前屏幕位置；未手动编辑时同步到输入框 */
async function refreshLivePosition() {
  try {
    const pos = await window.configPanelApi?.getFloatingPosition?.()
    if (pos && Number.isFinite(pos.x) && Number.isFinite(pos.y) && !userEditedPosition.value) {
      /*
       * 注意：不能通过 posX.value / posY.value 分别赋值（会触发两次独立的
       * emit('update:fb')，第二次基于尚未更新的 props.fb 会把 X 覆盖回旧值）。
       * 必须一次 emit 合并写入完整的 position，避免 X 被覆盖回 null/旧值。
       */
      programmaticSync = true
      emit('update:fb', { ...props.fb, position: { x: Math.round(pos.x), y: Math.round(pos.y) } })
      programmaticSync = false
    }
  } catch (_) {}
}

onMounted(() => {
  refreshLivePosition()
  loadCustomIcon()
  /* customIconId 变化时重新拉取预览 */
  watch(() => props.fb.customIconId, loadCustomIcon)
  /* 悬浮按钮拖动时位置会变化，轮询保持实时（1s，IPC 开销可忽略） */
  positionTimer = setInterval(refreshLivePosition, 1000)
})

onBeforeUnmount(() => {
  if (positionTimer) {
    clearInterval(positionTimer)
    positionTimer = null
  }
})

/* 隐藏的图片文件选择框（供 rizui_button 触发） */
const iconFileInput = ref(null)

// ================================================================
//  1. 图标预览：从 customs/ 按 id 拉取 base64 显示
// ================================================================

/* 默认图标路径（应用安装目录下的 app.ico） */
const defaultIcon = './image/app.ico'

/* 当前自定义图标的 data URL（从 customs/ 加载，用于预览） */
const previewDataUrl = ref('')

/*
 *  iconPlaceholder — 文本框占位提示
 *  未设置图标时显示提示文字，设置后显示 customIconId 引用
 */
const iconPlaceholder = computed(() => {
  return (props.fb.customIconId || '') ? `已设置自定义图标 (${props.fb.customIconId})` : '点击右侧按钮选择本地图片'
})

/*
 *  iconPreviewSrc — 图标预览的 <img src> 值。
 *  - 已从 customs/ 加载到 base64（previewDataUrl 非空）→ 直接显示
 *  - 否则使用内置默认图标 ./image/app.ico
 */
const iconPreviewSrc = computed(() => previewDataUrl.value || defaultIcon)

/*
 *  loadCustomIcon — 按 customIconId 从 customs/ 拉取资源并组 data URL 供预览。
 *  在组件挂载时与 customIconId 变化时调用。
 */
async function loadCustomIcon() {
  const id = props.fb.customIconId
  if (!id) { previewDataUrl.value = ''; return }
  try {
    const c = await window.configPanelApi?.getCustom?.({ id })
    previewDataUrl.value = (c?.base64 && c?.mimeType) ? `data:${c.mimeType};base64,${c.base64}` : ''
  } catch (_) {
    previewDataUrl.value = ''
  }
}

/*
 *  effectivePreviewSize — 匹配 FloatingButton 实际渲染的图标尺寸。
 *
 *  FloatingButton 的 iconStyle 将图标限制为按钮尺寸的 80%（最小 16px）：
 *    effective = max(16, min(iconSize, round(sizePx × 0.8)))
 *  其中 sizePx = 50 × (sizePercent / 100)
 *
 *  此处计算相同的值，确保配置面板预览与实际悬浮按钮显示一致。
 */
const effectivePreviewSize = computed(() => {
  const iconSize = props.fb.iconSize || 48
  const sizePx = Math.round(50 * ((props.fb.sizePercent || 100) / 100))
  const maxIcon = Math.round(sizePx * 0.8)
  return Math.max(16, Math.min(iconSize, maxIcon))
})

// ================================================================
//  2. 文件选择：读取图片 → 上传到 customs/ 目录 → 配置存引用
// ================================================================

/*
 *  处理文件选择器的 change 事件。
 *
 *    1. 从 e.target.files[0] 获取用户选择的文件
 *    2. 用 FileReader.readAsDataURL() 读取为 data URL，解析出 mimeType + base64
 *    3. 通过 configPanelApi.saveCustomStaged() 将数据暂存到主进程内存
 *       （不落盘；点击“应用”保存配置时才写入 customs/，未应用则被舍弃）
 *    4. emit('update:fb', { customIconId: stagedId })
 *    5. 清空 input.value 以允许重复选择同一文件
 *
 *  注意：
 *    - 读取/暂存均为异步，emit 在回调中执行
 *    - 大图片（>1MB）的 base64 会很大，建议用户使用小尺寸图标
 */
function handleIconPick(e) {
  const file = e.target.files[0]
  if (!file) return
  const reader = new FileReader()
  reader.onload = async (ev) => {
    const dataUrl = ev.target.result
    const semicolon = dataUrl.indexOf(';')
    const comma = dataUrl.indexOf(',')
    const mime = semicolon > 5 ? dataUrl.slice(5, semicolon) : 'image/png'
    const base64 = comma >= 0 ? dataUrl.slice(comma + 1) : ''
    previewDataUrl.value = dataUrl // 先本地预览
    try {
      const res = await window.configPanelApi?.saveCustomStaged?.({
        fileName: file.name || 'icon',
        mimeType: mime,
        purpose: 'floating-icon',
        base64
      })
      if (res?.ok && res.id) {
        emit('update:fb', { ...props.fb, customIconId: res.id })
      } else {
        previewDataUrl.value = ''
        console.error('暂存自定义图标失败', res)
      }
    } catch (error) {
      previewDataUrl.value = ''
      console.error('暂存自定义图标失败', error)
    }
    e.target.value = ''
  }
  reader.onerror = () => {
    console.error('读取图片文件失败')
    e.target.value = ''
  }
  reader.readAsDataURL(file)
}

// ================================================================
//  3. 图标重置：恢复默认图标设置
// ================================================================

/*
 *  清空 customIconId（使用默认图标），图标尺寸恢复为 48px。
 *  旧资源文件在“应用”保存配置时由主进程统一删除（未应用则不落盘）。
 */
function resetIcon() {
  previewDataUrl.value = ''
  emit('update:fb', { ...props.fb, customIconId: '', iconSize: 48 })
}

</script>

<style scoped>
/*
 *  本组件完整样式表
 *  主题色：初音绿 #39c5bb（所有控件颜色直接硬编码，不使用 CSS 变量以提升性能）
 */

/* ===== 主题色 & 全局 ===== */
.tab-page {
  animation: slide-in 0.3s cubic-bezier(0.25, 0, 0.25, 1);
}
@keyframes slide-in {
  from { opacity: 0; transform: translateX(24px); }
  to   { opacity: 1; transform: translateX(0); }
}

/* ===== 滚动条（主题色） ===== */
::-webkit-scrollbar {
  width: 6px;
  height: 6px;
}
::-webkit-scrollbar-track {
  background: transparent;
}
::-webkit-scrollbar-thumb {
  background: rgba(57, 197, 187, 0.35);
  border-radius: 3px;
}
::-webkit-scrollbar-thumb:hover {
  background: rgba(57, 197, 187, 0.55);
}
::-webkit-scrollbar-button {
  display: none;
}
::-webkit-scrollbar-corner {
  background: transparent;
}

.cfg-hint {
  font-size: 12px;
  color: #888;
  margin-top: 4px;
}
.cfg-hint.warn {
  color: #e80;
}

/* ===== 位置 X / Y 输入组 ===== */
.cfg-xy-group {
  display: flex;
  gap: 8px;
  margin-left: auto;
}

/* ===== 图标选择区域 ===== */
.icon-picker-row {
  display: flex;
  gap: 14px;
  align-items: center;
  width: 100%;
}
.icon-actions {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.icon-path-row {
  display: flex;
  gap: 8px;
  align-items: center;
}
.icon-path-sub {
  justify-content: space-between;
}
.icon-warn {
  margin: 0;
  white-space: nowrap;
}
.icon-preview-box {
  width: 72px;
  height: 72px;
  flex-shrink: 0;
  border: 2px solid #e0e4ea;
  border-radius: 50%;
  display: flex;
  align-items: center;
  justify-content: center;
  background: #f2f4f7;
  overflow: hidden;
}
.icon-preview-img {
  object-fit: contain;
}
</style>
