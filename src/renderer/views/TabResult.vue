<!--
================================================================================
  组件：TabResult.vue
  所属：配置面板 - 抽取结果设置 Tab
  路由：由 ConfigPanel.vue 直接引用，不是独立路由页面

================================================================================
  一、功能概述
================================================================================
  配置抽取结果浮窗的外观、音效及联动功能。包含三张卡片：

    卡片         | 功能
    ─────────────┼──────────────────────────────────────────
    浮窗外观     | 背景/边框颜色、透明度、装饰角色显示开关
    音效控制     | 抽取音效开关与音量；背景音乐上传（customs）与开关/音量/起始位置/淡入淡出
    联动         | ClassIsland 播报（暂未实现，notReady 遮罩）

  数据流：父组件 ConfigPanel 通过 :pickResult 传入配置对象，
          本组件通过 $emit('update:pickResult', ...) 上报修改。

================================================================================
  二、Props / Emits
================================================================================
  pickResult  — 抽取结果配置对象（来自父组件 draft.pickResult）
  emit('update:pickResult') — 通知父组件更新配置

  注意：必须传递完整新对象（展开原对象 + 覆盖修改字段），
        不可直接修改 props 的属性。

================================================================================
  三、依赖
================================================================================
  riz-ui 组件：rizui_card, rizui_cfgrow, rizui_switch, rizui_slider, rizui_timeline, rizui_colorpicker, rizui_text, rizui_button, NotReadyOverlay
  Vue API：computed, ref, watch, onMounted, onBeforeUnmount

================================================================================
  四、背景音乐（BGM）存储方案
================================================================================
  背景音乐为版权资源，不随仓库分发，默认无背景音乐，由用户自行上传：
    - 上传校验：扩展名白名单 + HTMLAudioElement 可解码性 + 时长解析（probeAudio）
    - 存储：customs/custom_<时间戳>.yml（purpose=bgm，含 duration 时长字段）
    - 配置：pickResultDialog.bgmCustomId 只存引用，不内嵌音频数据
    - 时间轴：max 跟随音乐实际时长（bgmDuration），起始位置超限自动回退 0
    - 未上传 BGM 时：播放开关/音量/起始位置/淡入淡出全部禁用，并提示先上传
    - 播放：PickResult.vue 通过 pick-result:get-config 由主进程解析为 bgmDataUrl

================================================================================
  五、更新记录
================================================================================
  2026-08-29
    - 背景音乐改为 customs/ 可加载资源（默认无），支持上传/移除、格式与
      可解码性校验、时间轴同步音乐时长、未上传时调整项禁用
    - 移除仓库内 public/sound/bgm.mp3（版权音乐）
    - 上传改为内存暂存：BGM 先暂存主进程内存（不落盘），“应用”时才写入 customs/；
      移除/换歌的旧资源在应用时由主进程统一删除
    - 抽取音效改为 customs/ 可加载资源（默认无）；音效卡片新增 上传/试听/移除，
      与 BGM 同一 staged 暂存机制；未上传时开关与音量禁用
    - 打包后 Arona 装饰图失效：src="/image/..." 绝对路径在 file:// 下解析失败，
      改用 resolveAssetUrl 按协议拼接

  最后更新：2026-08-29
================================================================================
-->
<template>
  <div class="tab-page">
    <!--
      一、浮窗外观
      控制抽取结果浮窗的视觉呈现
    -->
    <rizui_card title="浮窗外观" desc="调整抽取结果浮窗的视觉样式" icon="fa-solid fa-eye">
      <!-- 背景色 -->
      <rizui_cfgrow label="浮窗背景颜色" hint="结果浮窗的整体底色">
        <rizui_colorpicker v-model="pickResult.panelBgColor" :color="tabTheme" teleport=".config-left" />
      </rizui_cfgrow>
      <!-- 边框色 -->
      <rizui_cfgrow label="浮窗边框颜色" hint="结果浮窗的外框描边颜色">
        <rizui_colorpicker v-model="pickResult.panelBorderColor" :color="tabTheme" teleport=".config-left" />
      </rizui_cfgrow>
      <!-- 透明度滑块：内部存储 0-1，UI 显示 10-100% -->
      <rizui_cfgrow label="浮窗不透明度" hint="数值越高越不透明，100% 为完全不透明">
        <rizui_slider :model-value="opacityPercent" :min="10" :max="100" :color="tabTheme" display="%" @update:model-value="$emit('update:pickResult',{...pickResult,panelOpacity:$event/100})" />
      </rizui_cfgrow>
      <!-- 装饰角色开关 -->
      <rizui_cfgrow label="结果浮窗顶部装饰" hint="结果浮窗顶部是否显示小阿罗娜&小普拉娜两小只">
        <rizui_switch :model-value="pickResult.showDeco !== false" :color="tabTheme" @update:model-value="$emit('update:pickResult',{...pickResult,showDeco:$event})" />
      </rizui_cfgrow>
      <rizui_cfgrow label="" hint="" v-if="pickResult.showDeco == true" stack>
      <img :src="resolveAssetUrl('/image/Arona_Plana.png')" style="margin:auto; height: 66px; display: block;"/>  
      </rizui_cfgrow>  
    </rizui_card>

    <!--
      二、音效控制
      抽取音效 / BGM 的开关、音量、时间参数
    -->
    <rizui_card title="音效控制" desc="抽取时的音频反馈设置" icon="fa-solid fa-music">
      <!-- 抽取音效资源：上传 / 移除 / 试听（存于 customs/ 目录，默认无音效） -->
      <rizui_cfgrow label="抽取音效" :hint="gachaHint" stack>
        <div class="bgm-picker-row">
          <rizui_text :model-value="gachaDisplay" readonly :placeholder="gachaPlaceholder" :color="tabTheme" style="flex: 1; min-width: 0; text-align: left;" />
          <rizui_button :text="gachaUploading ? '上传中…' : '上传'" l-icon="fa-solid fa-upload" primary :color="tabTheme" @click="gachaFileInput?.click()" :disabled="gachaUploading" />
          <rizui_button :text="gachaPreviewPlaying ? '停止' : '试听'" l-icon="fa-solid fa-play" :color="tabTheme" @click="previewGachaSound" :disabled="!hasGachaSound" />
          <rizui_button text="移除" l-icon="fa-solid fa-trash" danger @click="removeGachaSound" :disabled="!hasGachaSound" />
          <input ref="gachaFileInput" type="file" accept="audio/*" @change="handleGachaPick" style="display:none" />
        </div>
        <rizui_infobox text="支持mp3/ogg/wav/m4a/flac等常见音频,音频将以 Base64 存储到配置文件中" style="margin-top: 20px;"/>
      </rizui_cfgrow>

      <!-- 抽取音效开关（未上传时禁用） -->
      <rizui_cfgrow label="播放抽取音效" :hint="playGachaHint" :disabled="!hasGachaSound">
        <rizui_switch :model-value="hasGachaSound && pickResult.defaultPlayGachaSound" :color="tabTheme" :disabled="!hasGachaSound" @update:model-value="$emit('update:pickResult',{...pickResult,defaultPlayGachaSound:$event})" />
      </rizui_cfgrow>
      <!-- 音效音量 -->
      <rizui_cfgrow label="抽取音效音量" hint="提示音的响度百分比" :disabled="!hasGachaSound">
        <rizui_slider :model-value="pickResult.soundVolume || 80" :min="0" :max="100" :color="tabTheme" display="%" @update:model-value="$emit('update:pickResult',{...pickResult,soundVolume:$event})" />
      </rizui_cfgrow>
      <!-- BGM 资源：上传 / 移除（存于 customs/ 目录，默认无背景音乐） -->
      <rizui_cfgrow label="背景音乐" :hint="bgmHint" stack>
        <div class="bgm-picker-row">
          <rizui_text :model-value="bgmDisplay" readonly :placeholder="bgmPlaceholder" :color="tabTheme" style="flex: 1; min-width: 0; text-align: left;" />
          <rizui_button :text="bgmUploading ? '上传中…' : '上传'" l-icon="fa-solid fa-upload" primary :color="tabTheme" @click="bgmFileInput?.click()" :disabled="bgmUploading" />
          <rizui_button text="移除" l-icon="fa-solid fa-trash" danger @click="removeBgm" :disabled="!hasBgm" />
          <input ref="bgmFileInput" type="file" accept="audio/*" @change="handleBgmPick" style="display:none" />
        </div>
          <rizui_infobox text="支持mp3/ogg/wav/m4a/flac等常见音频,音频将以 Base64 存储到配置文件中" style="margin-top: 20px;"/>
      </rizui_cfgrow>

      <!-- BGM 开关（未上传时禁用） -->
      <rizui_cfgrow label="播放抽取音乐" :hint="playMusicHint" :disabled="!hasBgm">
        <rizui_switch :model-value="hasBgm && pickResult.playMusic" :color="tabTheme" :disabled="!hasBgm" @update:model-value="$emit('update:pickResult',{...pickResult,playMusic:$event})" />
      </rizui_cfgrow>
      <!-- BGM 音量 -->
      <rizui_cfgrow label="抽取音乐音量" hint="背景音乐的响度百分比" :disabled="!hasBgm">
        <rizui_slider :model-value="pickResult.musicVolume || 60" :min="0" :max="100" :color="tabTheme" display="%" @update:model-value="$emit('update:pickResult',{...pickResult,musicVolume:$event})" />
      </rizui_cfgrow>
      <!-- BGM 起始秒数，时间轴 max 跟随音乐时长 -->
      <rizui_cfgrow label="播放起始位置" :hint="'BGM 从指定秒数开始播放（' + formatTime(bgmStartClamped) + ' / ' + formatTime(bgmDuration) + '）'" stack :disabled="!hasBgm">
        <rizui_timeline :model-value="bgmStartClamped" :min="0" :max="bgmMaxTime" :color="tabTheme" @update:model-value="$emit('update:pickResult',{...pickResult,bgmStartTime:$event})" />
        <div style="margin-top: 6px;">
          <button class="preview-btn" @click="previewBgm" :style="{ color: tabTheme, borderColor: tabTheme }" :disabled="!hasBgm">
            <i class="fa-solid" :class="previewPlaying ? 'fa-stop' : 'fa-play'"></i>
            {{ previewPlaying ? '停止' : '试听' }}
          </button>
        </div>
      </rizui_cfgrow>
      <!-- BGM 淡入淡出渐变秒数 -->
      <rizui_cfgrow label="淡入淡出时长" hint="BGM 开始和结束时的渐变过渡秒数" :disabled="!hasBgm">
        <rizui_slider :model-value="pickResult.bgmFadeDuration || 1.5" :min="0.5" :max="5" :step="0.1" :color="tabTheme" :display-value="(pickResult.bgmFadeDuration || 1.5).toFixed(1) + 's'" @update:model-value="$emit('update:pickResult',{...pickResult,bgmFadeDuration:$event})" />
      </rizui_cfgrow>
    </rizui_card>

    <!--
      三、联动
      与其他桌面软件的集成功能
    -->
    <rizui_card title="联动" desc="与其他软件的联动功能" icon="fa-solid fa-link">
      <!-- notReady：功能未实现，遮罩自动覆盖（rizui_cfgrow 无 notReady 属性，手动叠加遮罩） -->
      <rizui_cfgrow label="ClassIsland 联动播报" hint="将抽取结果播报到 ClassIsland" stack style="position: relative; overflow: hidden;">
        <rizui_switch :model-value="false" :color="tabTheme" disabled />
        <NotReadyOverlay text="等待实现" />
      </rizui_cfgrow>
    </rizui_card>
  </div>
</template>

<script setup>
/*
 *  组件逻辑概览
 *  1. props / emit      — 与父组件 ConfigPanel 通信
 *  2. tabTheme          — 当前 Tab 主题色（#55cc99 初音绿）
 *  3. opacityPercent    — 透明度 computed：内部 0-1 ↔ UI 10-100%
 *  4. formatTime        — 秒数 → MM:SS 格式化
 */
import { computed, ref, watch, onMounted, onBeforeUnmount } from 'vue'
import { rizui_card, rizui_cfgrow, rizui_switch, rizui_slider, rizui_timeline, rizui_colorpicker, rizui_text, rizui_button, NotReadyOverlay, rizui_infobox } from 'riz-ui'
import { resolveAssetUrl } from '@/utils/asset'

const props = defineProps({ pickResult: Object })
const emit = defineEmits(['update:pickResult'])

/* Tab 主题色 */
const tabTheme = '#55cc99'

/*
 * 透明度转换：config 中存储为 0-1 小数（panelOpacity），UI 滑条使用 10-100 整数。
 * rizui_slider 的 @update:model-value 中直接 $event/100 转回小数并 emit。
 */
const opacityPercent = computed(() => Math.round((props.pickResult.panelOpacity || 0.9) * 100))

/*
 *  秒数 → MM:SS 格式化
 *  用于 BGM 起始位置 hint 的动态显示
 */
function formatTime(sec) {
  const s = Math.max(0, Math.round(sec || 0))
  const m = Math.floor(s / 60)
  const rs = s % 60
  return `${m}:${String(rs).padStart(2, '0')}`
}

/* ---- BGM 管理：上传 / 时长同步 / 试听 ---- */

/* 当前 BGM 资源信息（从 customs/ 拉取：id、文件名、时长） */
const bgmInfo = ref(null)
/* BGM 试听用 data URL */
const bgmPreviewUrl = ref('')
/* 是否正在上传 */
const bgmUploading = ref(false)
/* 隐藏的音频文件选择框（供 rizui_button 触发） */
const bgmFileInput = ref(null)
/* 试听 Audio 实例与播放状态 */
const previewAudio = ref(null)
const previewPlaying = ref(false)

/* 是否已上传背景音乐（决定 BGM 调整项是否可用） */
const hasBgm = computed(() => !!props.pickResult.bgmCustomId)
/* BGM 时长（秒），来自 customs 资源的 duration 字段 */
const bgmDuration = computed(() => Number(bgmInfo.value?.duration) || 0)
/* 时间轴最大秒数：跟随音乐时长（至少 1s，无 BGM 时回退 120s） */
const bgmMaxTime = computed(() => Math.max(1, Math.ceil(bgmDuration.value || 120)))
/* 起始位置钳制：不超过音乐时长（防止切歌后越界） */
const bgmStartClamped = computed(() => {
  const t = Number(props.pickResult.bgmStartTime) || 0
  return Math.min(t, bgmMaxTime.value)
})

/* 文本框显示：已上传 → 文件名；未上传 → 占位提示 */
const bgmDisplay = computed(() => bgmInfo.value?.fileName || props.pickResult.bgmCustomId || '')
const bgmPlaceholder = computed(() => hasBgm.value ? '已上传背景音乐' : '请上传背景音乐')

/* 背景音乐行 hint：状态 + 时长 */
const bgmHint = computed(() => hasBgm.value
  ? `已上传：${bgmInfo.value?.fileName || ''}（时长 ${formatTime(bgmDuration.value)}）`
  : '请先上传背景音乐，上传后可调整音量、起始位置与淡入淡出')

/* 播放开关 hint：未上传时提示先上传 */
const playMusicHint = computed(() => hasBgm.value
  ? '抽取时播放完整的背景音乐'
  : '请先上传背景音乐后才能启用')

/*
 * 按 bgmCustomId 从 customs/ 拉取 BGM 信息（文件名、时长），并缓存试听 data URL。
 * 在组件挂载时与 bgmCustomId 变化时调用；若起始位置超过新时长则回退到 0。
 */
async function loadBgmInfo() {
  const id = props.pickResult.bgmCustomId
  if (!id) { bgmInfo.value = null; bgmPreviewUrl.value = ''; return }
  try {
    const c = await window.configPanelApi?.getCustom?.({ id })
    if (c) {
      let duration = Number(c.duration) || 0
      const dataUrl = (c.base64 && c.mimeType) ? `data:${c.mimeType};base64,${c.base64}` : ''
      bgmPreviewUrl.value = dataUrl
      /* 兼容旧资源：duration 字段缺失（0）时用音频元数据补测时长 */
      if (!duration && dataUrl) {
        try {
          const probe = await probeAudio(dataUrl)
          duration = probe.duration || 0
        } catch (_) { /* 补测失败则维持 0，时间轴回退默认范围 */ }
      }
      bgmInfo.value = { id: c.id, fileName: c.fileName || '', duration }
      /* 新时长小于当前起始位置时回退到 0 */
      const max = Math.max(1, Math.ceil(duration || 0))
      if (Number(props.pickResult.bgmStartTime) > max) {
        emit('update:pickResult', { ...props.pickResult, bgmStartTime: 0 })
      }
    } else {
      bgmInfo.value = null
      bgmPreviewUrl.value = ''
    }
  } catch (_) {
    bgmInfo.value = null
    bgmPreviewUrl.value = ''
  }
}

/* 组件挂载时加载 BGM 与抽取音效信息，引用变化时重新加载 */
onMounted(() => {
  loadBgmInfo()
  loadGachaInfo()
})
watch(() => props.pickResult.bgmCustomId, () => { loadBgmInfo() })
watch(() => props.pickResult.gachaSoundCustomId, () => { loadGachaInfo() })

/*
 * 用 HTMLAudioElement 探测音频：可解码则返回时长（秒），失败 reject。
 * 这是上传时的“文件合法性校验”。
 */
function probeAudio(dataUrl) {
  return new Promise((resolve, reject) => {
    const audio = new Audio()
    audio.preload = 'metadata'
    audio.onloadedmetadata = () => {
      const duration = Number.isFinite(audio.duration) ? audio.duration : 0
      audio.src = ''
      resolve({ duration })
    }
    audio.onerror = () => { audio.src = ''; reject(new Error('音频解码失败，请更换文件')) }
    audio.src = dataUrl
  })
}

/*
 * 处理背景音乐文件选择：
 *   1. 扩展名/类型预检（音频白名单）
 *   2. HTMLAudioElement 校验可解码性并获取时长（probeAudio）
 *   3. 暂存到主进程内存（saveCustomStaged，不落盘；点击“应用”时才写入 customs/）
 *   4. emit bgmCustomId（staged 引用）；时间轴随之同步到音乐时长
 */
async function handleBgmPick(e) {
  const file = e.target.files[0]
  if (!file) return
  const ext = (file.name.split('.').pop() || '').toLowerCase()
  const audioExts = ['mp3', 'ogg', 'wav', 'm4a', 'flac', 'aac', 'opus', 'webm']
  if (!audioExts.includes(ext) && !String(file.type || '').startsWith('audio/')) {
    console.error('不支持的音频格式:', file.name, file.type)
    e.target.value = ''
    return
  }
  const reader = new FileReader()
  reader.onload = async (ev) => {
    const dataUrl = ev.target.result
    bgmUploading.value = true
    try {
      /* 校验可解码性 + 获取时长 */
      const { duration } = await probeAudio(dataUrl)
      if (!duration || !Number.isFinite(duration)) throw new Error('无法解析音频时长')
      const semicolon = dataUrl.indexOf(';')
      const comma = dataUrl.indexOf(',')
      const mime = semicolon > 5 ? dataUrl.slice(5, semicolon) : (file.type || 'audio/mpeg')
      const base64 = comma >= 0 ? dataUrl.slice(comma + 1) : ''
      const res = await window.configPanelApi?.saveCustomStaged?.({
        fileName: file.name || 'bgm',
        mimeType: mime,
        purpose: 'bgm',
        base64,
        duration: Math.round(duration * 10) / 10
      })
      if (res?.ok && res.id) {
        /* 同步写入 BGM 信息：文本框立即显示文件名，无需等待异步重新加载 */
        bgmInfo.value = {
          id: res.id,
          fileName: file.name || 'bgm',
          duration: Math.round(duration * 10) / 10
        }
        emit('update:pickResult', { ...props.pickResult, bgmCustomId: res.id })
      } else {
        console.error('暂存背景音乐失败', res)
      }
    } catch (error) {
      console.error('背景音乐上传失败', error)
    } finally {
      bgmUploading.value = false
      e.target.value = ''
    }
  }
  reader.onerror = () => { console.error('读取音频文件失败'); e.target.value = '' }
  reader.readAsDataURL(file)
}

/* 移除背景音乐：清空引用并关闭播放开关；旧资源在“应用”时由主进程删除 */
function removeBgm() {
  stopPreview()
  bgmInfo.value = null
  bgmPreviewUrl.value = ''
  emit('update:pickResult', { ...props.pickResult, bgmCustomId: '', playMusic: false })
}

/* 试听：从 customs 加载的 data URL 播放，从当前起始位置开始，音量跟随配置 */
async function previewBgm() {
  if (previewPlaying.value) { stopPreview(); return }
  if (!props.pickResult.bgmCustomId) return
  if (!bgmPreviewUrl.value) await loadBgmInfo()
  if (!bgmPreviewUrl.value) return
  try {
    const audio = previewAudio.value || (previewAudio.value = new Audio())
    audio.src = bgmPreviewUrl.value
    audio.loop = false
    audio.currentTime = Number(bgmStartClamped.value) || 0
    audio.volume = Math.max(0.01, Math.min(1, (props.pickResult.musicVolume || 60) / 100))
    await audio.play().catch(() => {})
    previewPlaying.value = true
    audio.onended = () => { previewPlaying.value = false }
  } catch { /* 忽略音频加载错误 */ }
}

/* ---- 抽取音效管理：上传 / 试听（与 BGM 同一 customs 资源机制） ---- */

/* 当前抽取音效资源信息（从 customs/ 拉取：id、文件名） */
const gachaInfo = ref(null)
/* 抽取音效试听用 data URL */
const gachaPreviewUrl = ref('')
/* 是否正在上传 */
const gachaUploading = ref(false)
/* 隐藏的音频文件选择框 */
const gachaFileInput = ref(null)
/* 试听 Audio 实例与播放状态 */
const gachaPreviewAudio = ref(null)
const gachaPreviewPlaying = ref(false)

/* 是否已上传抽取音效（决定开关/音量是否可用） */
const hasGachaSound = computed(() => !!props.pickResult.gachaSoundCustomId)

/* 文本框 / hint 显示 */
const gachaDisplay = computed(() => gachaInfo.value?.fileName || props.pickResult.gachaSoundCustomId || '')
const gachaPlaceholder = computed(() => hasGachaSound.value ? '已上传抽取音效' : '请上传抽取音效（默认无）')
const gachaHint = computed(() => hasGachaSound.value
  ? `已上传：${gachaInfo.value?.fileName || ''}`
  : '请先上传抽取音效，上传后可播放、调整音量')
const playGachaHint = computed(() => hasGachaSound.value
  ? '抽取时播放短促的提示音'
  : '请先上传抽取音效后才能启用')

/* 按 gachaSoundCustomId 从 customs/ 拉取抽取音效信息并缓存试听 data URL */
async function loadGachaInfo() {
  const id = props.pickResult.gachaSoundCustomId
  if (!id) { gachaInfo.value = null; gachaPreviewUrl.value = ''; return }
  try {
    const c = await window.configPanelApi?.getCustom?.({ id })
    if (c) {
      gachaInfo.value = { id: c.id, fileName: c.fileName || '' }
      gachaPreviewUrl.value = (c.base64 && c.mimeType) ? `data:${c.mimeType};base64,${c.base64}` : ''
    } else {
      gachaInfo.value = null
      gachaPreviewUrl.value = ''
    }
  } catch (_) {
    gachaInfo.value = null
    gachaPreviewUrl.value = ''
  }
}

/*
 * 处理抽取音效文件选择：
 *   1. 扩展名/类型预检（音频白名单）
 *   2. probeAudio 校验可解码性
 *   3. 暂存到主进程内存（saveCustomStaged，不落盘；“应用”时才写入 customs/）
 *   4. emit gachaSoundCustomId（staged 引用）
 */
async function handleGachaPick(e) {
  const file = e.target.files[0]
  if (!file) return
  const ext = (file.name.split('.').pop() || '').toLowerCase()
  const audioExts = ['mp3', 'ogg', 'wav', 'm4a', 'flac', 'aac', 'opus', 'webm']
  if (!audioExts.includes(ext) && !String(file.type || '').startsWith('audio/')) {
    console.error('不支持的音频格式:', file.name, file.type)
    e.target.value = ''
    return
  }
  const reader = new FileReader()
  reader.onload = async (ev) => {
    const dataUrl = ev.target.result
    gachaUploading.value = true
    try {
      /* 校验可解码性 */
      await probeAudio(dataUrl)
      const semicolon = dataUrl.indexOf(';')
      const comma = dataUrl.indexOf(',')
      const mime = semicolon > 5 ? dataUrl.slice(5, semicolon) : (file.type || 'audio/wav')
      const base64 = comma >= 0 ? dataUrl.slice(comma + 1) : ''
      const res = await window.configPanelApi?.saveCustomStaged?.({
        fileName: file.name || 'gacha-sound',
        mimeType: mime,
        purpose: 'gacha-sound',
        base64
      })
      if (res?.ok && res.id) {
        /* 同步写入信息：文本框立即显示文件名 */
        gachaInfo.value = { id: res.id, fileName: file.name || 'gacha-sound' }
        emit('update:pickResult', { ...props.pickResult, gachaSoundCustomId: res.id })
      } else {
        console.error('暂存抽取音效失败', res)
      }
    } catch (error) {
      console.error('抽取音效上传失败', error)
    } finally {
      gachaUploading.value = false
      e.target.value = ''
    }
  }
  reader.onerror = () => { console.error('读取音频文件失败'); e.target.value = '' }
  reader.readAsDataURL(file)
}

/* 移除抽取音效：清空引用并关闭播放开关；旧资源在“应用”时由主进程删除 */
function removeGachaSound() {
  stopGachaPreview()
  gachaInfo.value = null
  gachaPreviewUrl.value = ''
  emit('update:pickResult', { ...props.pickResult, gachaSoundCustomId: '', defaultPlayGachaSound: false })
}

/* 试听：从 customs 加载的 data URL 播放，音量跟随配置 */
async function previewGachaSound() {
  if (gachaPreviewPlaying.value) { stopGachaPreview(); return }
  if (!props.pickResult.gachaSoundCustomId) return
  if (!gachaPreviewUrl.value) await loadGachaInfo()
  if (!gachaPreviewUrl.value) return
  try {
    const audio = gachaPreviewAudio.value || (gachaPreviewAudio.value = new Audio())
    audio.src = gachaPreviewUrl.value
    audio.loop = false
    audio.volume = Math.max(0.01, Math.min(1, (props.pickResult.soundVolume || 80) / 100))
    await audio.play().catch(() => {})
    gachaPreviewPlaying.value = true
    audio.onended = () => { gachaPreviewPlaying.value = false }
  } catch { /* 忽略音频加载错误 */ }
}

function stopGachaPreview() {
  const audio = gachaPreviewAudio.value
  if (audio) {
    audio.pause()
    audio.currentTime = 0
    audio.onended = null
  }
  gachaPreviewPlaying.value = false
}

function stopPreview() {
  const audio = previewAudio.value
  if (audio) {
    audio.pause()
    audio.currentTime = 0
    audio.onended = null
  }
  previewPlaying.value = false
}

/* 组件卸载时停止试听 */
onBeforeUnmount(() => { stopPreview(); stopGachaPreview() })
</script>

<style scoped>
/* ===== 主题色 #55cc99 ===== */
.tab-page {
  animation: slide-in 0.3s cubic-bezier(0.25, 0, 0.25, 1);
}
@keyframes slide-in {
  from { opacity: 0; transform: translateX(24px); }
  to   { opacity: 1; transform: translateX(0); }
}

/* ===== 滚动条 ===== */
::-webkit-scrollbar {
  width: 6px;
  height: 6px;
}
::-webkit-scrollbar-track {
  background: transparent;
}
::-webkit-scrollbar-thumb {
  background: rgba(85, 204, 153, 0.35);
  border-radius: 3px;
}
::-webkit-scrollbar-thumb:hover {
  background: rgba(85, 204, 153, 0.55);
}
::-webkit-scrollbar-button {
  display: none;
}
::-webkit-scrollbar-corner {
  background: transparent;
}

/* ===== 试听按钮 ===== */
.preview-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 4px 12px;
  border: 1.5px solid;
  border-radius: 999px;
  background: transparent;
  font-size: 11px;
  font-weight: 600;
  cursor: pointer;
  font-family: inherit;
  transition: background 0.15s, opacity 0.15s;
}
.preview-btn:hover {
  opacity: 0.75;
}
.preview-btn:active {
  opacity: 0.55;
}
.preview-btn i {
  font-size: 10px;
}
.preview-btn:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}

/* ===== 背景音乐上传行 ===== */
.bgm-picker-row {
  display: flex;
  gap: 8px;
  align-items: center;
  width: 100%;
}
.bgm-picker-row > * {
  flex-shrink: 0;
}

</style>
