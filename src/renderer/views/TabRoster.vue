<!--
  组件：TabRoster.vue
  所属：配置面板 - 名单管理 Tab
  父组件：ConfigPanel.vue（通过 props 传入数据，通过 emit 传出修改）

  功能概述：
    1. 名单导入 —— 支持手动输入（每行一个名字）或上传 txt/csv 文件
    2. 芯片预览 —— 右侧实时显示已添加的学生，点击可跳转到权重列表
    3. 权重管理 —— 每位学生独立滑条调节权重 0~5，自动计算概率百分比
    4. 单轮重复抽取开关 —— 控制同一轮次是否允许抽到同一人

  数据流：
    父组件 ConfigPanel 持有全局配置对象 draft
    本组件通过 props.studentList 和 props.allowRepeatDraw 接收数据
    修改时通过 emit('update:studentList', newList) 把新数据传回父组件
    父组件收到后更新 draft.studentList，再通过 props 流回本组件（单向数据流）

  注意事项：
    - 名单文本(textarea)和 studentList 数组之间需要双向同步，但要避免死循环
      用 syncingFromList 标志位区分"用户正在输入"和"外部数据变更"
    - 权重滑条 0~5 步长 0.1，由 rizui_slider 组件提供轨道填充
    - 芯片 tooltip 通过 emit('chip-hover'/'chip-leave') 通知父组件的全局 tooltip

  更新记录（2026-08-28）：
    - 新增 saveVersion prop：父组件"应用"保存成功后自动重载班级列表
      （修复：新建班级保存后切走再切回名单为空，需手动刷新的问题）
    - 班级操作（创建/重命名/删除/刷新失败）接入 rizui_toast 通知
    - applyActiveClass 仅在能定位到班级时才覆盖名单，避免无班级时误清空编辑内容

  2026-08-29：
    - 打包后 Arona 空态图失效：src="/image/Arona_Empty.png" 绝对路径在 file:// 下
      解析失败，改用 resolveAssetUrl 按协议拼接完整 URL
-->

<template>
  <div class="tab-page">

    <!-- 班级管理 -->
    <rizui_card title="班级管理" desc="支持多个班级，每个班级拥有独立的名单与权重（保存于 classes/ 目录）" icon="fa-solid fa-people-group">
      <rizui_cfgrow label="当前班级" hint="切换班级后，名单与权重会随之更新">
        <rizui_dropdown
          :model-value="activeClassId"
          :options="classOptions"
          :color="tabTheme"
          @update:model-value="handleSwitchClass"
          width="100%"
        />
      </rizui_cfgrow>
      <rizui_cfgrow stack>
        <div class="class-actions">
          <rizui_button text="添加" l-icon="fa-solid fa-plus" primary :color="tabTheme" @click="openClassDialog('add')" />
          <rizui_button text="刷新" l-icon="fa-solid fa-rotate" :color="tabTheme" @click="handleRefreshClasses" />
          <rizui_button text="重命名" l-icon="fa-solid fa-pen" :color="tabTheme" @click="openClassDialog('rename')" :disabled="!activeClassId" />
          <rizui_button text="删除" l-icon="fa-solid fa-trash" danger @click="handleDeleteClass" :disabled="!activeClassId" />
        </div>
      </rizui_cfgrow>
      <div v-if="!activeClassId" class="class-empty-hint">
        暂无班级，请点击「添加」创建第一个班级
      </div>
    </rizui_card>

    <!-- 名单导入 -->
    <rizui_card title="名单导入" desc="导入 txt/csv 文件或粘贴名单于输入框中（每行一名同学）" icon="fa-solid fa-list">
      <div class="import-capsule">
        <label class="upload-btn">
          <svg viewBox="0 0 24 24" width="14" height="14" stroke="currentColor" stroke-width="2" fill="none" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="17,8 12,3 7,8"/><line x1="12" y1="3" x2="12" y2="15"/></svg>
          导入文件
          <input type="file" accept=".txt,.csv" @change="handleFileUpload" style="display:none" />
        </label>
        <span class="import-hint">老师可以点击左侧按钮导入txt/csv名单哦~</span>
        <span class="count-badge"> 人数:{{ studentList.length }} </span>
      </div>
      <div class="roster-layout">
        <div class="roster-left">
          <textarea
            v-model="rawListText"
            class="roster-textarea"
            placeholder="每行一个姓名&#10;例如：&#10;早濑优香&#10;小鸟游星野&#10;空崎日奈"
            @input="syncTextToList"
          ></textarea>
        </div>
        <div class="roster-right">
          <div v-if="studentList.length === 0" class="chip-empty-hint">左侧输入姓名后<br>这里会显示学生芯片</div>
          <div v-else class="name-chip-grid">
            <span
              v-for="(s, i) in studentList"
              :key="i"
              class="name-chip"
              @click="scrollToStudent(i)"
              @mouseenter="$emit('chip-hover', $event, s)"
              @mouseleave="$emit('chip-leave')"
            >{{ s.name }}</span>
          </div>
        </div>
      </div>
    </rizui_card>

    <!-- 二、权重管理 -->
    <rizui_card title="权重管理" desc="调整每位同学的抽取权重（越高越容易被抽到）以及重复抽取规则" icon="fa-solid fa-chart-pie">
      <div class="weight-head-row">
        <rizui_checkbox
          :model-value="allowRepeatDraw"
          :color="tabTheme"
          r-label="单轮抽取中允许重复结果"
          @update:model-value="$emit('update:allowRepeatDraw', $event)"
        />
        <button class="roster-reset" @click="resetWeights">重置全部权重</button>
      </div>
      <div v-if="studentList.length === 0" class="empty-tip">
        <img :src="resolveAssetUrl('/image/Arona_Empty.png')" alt="Arona Empty" class="empty-arona-img" />
        <p>暂无名单，请先导入</p>
      </div>
      <div v-else class="student-table-wrap">
        <div class="roster-list">
          <div
            v-for="(s, i) in studentList"
            :key="i"
            class="roster-item"
            :ref="el => { if (el) studentRefs[i] = el }"
          >
            <span class="roster-name">{{ s.name }}</span>
            <div class="roster-letter" title="信封颜色">
              <button
                v-for="opt in letterColorOptions"
                :key="opt.value"
                class="letter-dot"
                :class="[`letter-${opt.value}`, { active: (s.letterColor || 'rainbow') === opt.value }]"
                :title="opt.label"
                type="button"
                @click="setLetterColor(i, opt.value)"
              ></button>
            </div>
            <div class="roster-weight">
              <rizui_slider :model-value="s.weight" :min="0" :max="5" :step="0.1" :color="tabTheme" :display-value="s.weight.toFixed(1)" @update:model-value="setWeight(i, $event)" />
              <span class="weight-prob">{{ studentProb(s) }}</span> 
            </div>
            <button class="roster-del" @click="removeStudent(i)">✕</button>
          </div>
        </div>
      </div>
      <rizui_infobox type="warn" text="请合理、公平地使用权重管理和信封颜色设置" />
      <rizui_infobox type="error" text="Blue Random 并不推荐对现实的学生们设置不同的信封颜色/分等级，每一位学生都具有自身独特的价值，都应该平等且同样尊重地被对待。" />
    </rizui_card>

    <!-- 添加 / 重命名班级 -->
    <rizui_dialog v-model:show="classDialog.show" :title="classDialog.title" :color="tabTheme" teleport=".config-left" :mask-close="false">
      <rizui_text v-model="classDialog.name" placeholder="输入班级名称" :color="tabTheme" style="text-align: left;" @enter="confirmClassDialog" />
      <template #footer>
        <rizui_button text="取消" @click="classDialog.show = false" />
        <rizui_button text="确定" primary :color="tabTheme" @click="confirmClassDialog" />
      </template>
    </rizui_dialog>

    <!-- 删除确认 -->
    <rizui_dialog v-model:show="deleteDialog.show" title="删除班级" :color="tabTheme" teleport=".config-left" :mask-close="false">
      <p>确定删除班级「{{ deleteDialog.name }}」吗？其名单与权重将被移除，且不可恢复。</p>
      <template #footer>
        <rizui_button text="取消" @click="deleteDialog.show = false" />
        <rizui_button text="删除" danger @click="confirmDeleteClass" />
      </template>
    </rizui_dialog>

    <!-- 班级操作 toast 通知 -->
    <rizui_toast
      v-if="toast.show"
      :type="toast.type"
      :title="toast.title"
      :hint="toast.hint"
      @close="toast.show = false"
    />
  </div>
</template>

<script setup>
/*
 *  组件逻辑概览（按代码顺序）：
 *  1. props / emit  —— 与父组件通信的接口
 *  2. 名单文本同步  —— textarea  ←→  studentList 数组 双向转换
 *  3. 文件导入      —— 读取用户选择的 txt/csv 文件
 *  4. 权重操作      —— 单个修改 / 删除学生 / 批量重置 / 概率计算
 *  5. 数据监听      —— 外部数据变化时自动回填 textarea
 *  6. 芯片点击跳转  —— 点击芯片滚动到权重列表对应行并高亮
 */
import { ref, computed, watch, reactive, onMounted } from 'vue'
import { rizui_card, rizui_checkbox, rizui_slider, rizui_dropdown, rizui_text, rizui_button, rizui_dialog, rizui_toast, rizui_cfgrow, rizui_infobox } from 'riz-ui'
import { resolveAssetUrl } from '@/utils/asset'

/* Tab 主题色 */
const tabTheme = '#66ccff'

/*
 *  props：父组件传入的数据
 *  studentList    - 学生数组，每项 { name: string, weight: number }
 *  allowRepeatDraw - 是否允许同一轮抽到同一人
 */
const props = defineProps({
  studentList: { type: Array, required: true },
  allowRepeatDraw: { type: Boolean, default: true },
  /* 保存版本号：父组件每次"应用"保存成功后 +1，本组件据此重新加载班级列表 */
  saveVersion: { type: Number, default: 0 }
})

/*
 *  emit：向父组件发送数据变更事件
 *  update:studentList   - 学生数组发生变化（增/删/改权重）
 *  update:allowRepeatDraw - 重复抽取开关变化
 *  chip-hover / chip-leave - 鼠标悬停/离开芯片（父组件显示全局 tooltip）
 */
const emit = defineEmits(['update:studentList', 'update:allowRepeatDraw', 'update:activeClassId', 'chip-hover', 'chip-leave'])

// ================================================================
//  0. 班级管理（多班级）
// ================================================================

/* 班级列表与当前班级 id */
const classes = ref([])
const activeClassId = ref('')
const classLoading = ref(false)

/* 添加/重命名对话框 */
const classDialog = reactive({ show: false, mode: 'add', title: '添加班级', name: '' })
/* 删除确认对话框 */
const deleteDialog = reactive({ show: false, id: '', name: '' })

/* toast 通知（rizui_toast） */
const toast = reactive({ show: false, type: 'info', title: '', hint: '' })
function showToast(type, title, hint = '') {
  toast.type = type
  toast.title = title
  toast.hint = hint
  toast.show = true
}

const classOptions = computed(() => classes.value.map(c => ({ value: c.classId, label: c.name })))

/* 将指定班级设为当前班级（仅更新草稿与编辑器，落盘由“应用”完成）。
   注意：仅在能定位到班级时才覆盖名单，避免无班级时误清空编辑内容。 */
function applyActiveClass(classId) {
  activeClassId.value = classId
  const cls = classes.value.find(c => c.classId === classId)
  emit('update:activeClassId', classId)
  if (cls) emit('update:studentList', cls.studentList)
}

/*
 * 从磁盘加载班级列表（即“刷新”）。
 * 尽量保持当前班级；若其已不存在则回退到第一个班级。
 * 返回 boolean 表示是否成功（供调用方决定是否弹错误 toast）。
 */
async function loadClasses() {
  classLoading.value = true
  try {
    const res = await window.configPanelApi?.listClasses?.()
    if (!res) return false
    classes.value = res.classes || []
    const prev = activeClassId.value || res.activeClassId || ''
    const keep = classes.value.some(c => c.classId === prev)
    applyActiveClass(keep ? prev : (classes.value[0]?.classId || ''))
    return true
  } catch (error) {
    console.error('加载班级列表失败', error)
    return false
  } finally {
    classLoading.value = false
  }
}

/* 父组件保存成功后（saveVersion 变化）重新加载班级列表，
   使"新建班级保存后切走再切回"能立即看到最新名单（不必手动刷新）。 */
watch(() => props.saveVersion, () => {
  if (props.saveVersion > 0) loadClasses()
})

function handleSwitchClass(classId) {
  if (!classId || classId === activeClassId.value) return
  applyActiveClass(classId)
}

async function handleRefreshClasses() {
  const ok = await loadClasses()
  if (!ok) showToast('error', '刷新失败', '无法读取班级列表')
}

function openClassDialog(mode) {
  classDialog.mode = mode
  classDialog.title = mode === 'add' ? '添加班级' : '重命名班级'
  classDialog.name = mode === 'rename'
    ? (classes.value.find(c => c.classId === activeClassId.value)?.name || '')
    : ''
  classDialog.show = true
}

async function confirmClassDialog() {
  const name = classDialog.name.trim()
  if (!name) return
  try {
    if (classDialog.mode === 'add') {
      const res = await window.configPanelApi?.createClass?.(name)
      if (res?.ok && res.class) {
        await loadClasses()
        applyActiveClass(res.class.classId)
        showToast('info', `班级「${name}」已创建`)
      } else {
        showToast('error', '创建班级失败', (res && res.message) || '未知错误')
      }
    } else if (activeClassId.value) {
      const res = await window.configPanelApi?.renameClass?.(activeClassId.value, name)
      if (res?.ok) {
        await loadClasses()
        showToast('info', `已重命名为「${name}」`)
      } else {
        showToast('error', '重命名失败', (res && res.message) || '未知错误')
      }
    }
  } catch (error) {
    console.error('班级操作失败', error)
    showToast('error', '班级操作失败', String((error && error.message) || error))
  }
  classDialog.show = false
}

function handleDeleteClass() {
  if (!activeClassId.value) return
  const cls = classes.value.find(c => c.classId === activeClassId.value)
  deleteDialog.id = activeClassId.value
  deleteDialog.name = cls?.name || ''
  deleteDialog.show = true
}

async function confirmDeleteClass() {
  const name = deleteDialog.name
  try {
    const res = await window.configPanelApi?.deleteClass?.(deleteDialog.id)
    if (res?.ok) {
      await loadClasses()
      showToast('info', `班级「${name}」已删除`)
    } else {
      showToast('error', '删除班级失败', (res && res.message) || '未知错误')
    }
  } catch (error) {
    console.error('删除班级失败', error)
    showToast('error', '删除班级失败', String((error && error.message) || error))
  }
  deleteDialog.show = false
}

onMounted(() => {
  loadClasses()
})

// ================================================================
//  1. 名单文本同步：textarea 的原始文本 ←→ 结构化 studentList 数组
// ================================================================

// textarea 中显示的原始文本（每行一个名字）
const rawListText = ref('')

/*
 *  syncingFromList 标志位 —— 防止死循环的关键
 *  当用户在 textarea 中输入文字时，syncTextToList 会 emit 新的 studentList
 *  父组件收到后更新 props.studentList，触发 watch，watch 又会调用 syncListToText
 *  如果不加标志位，syncListToText 会覆盖用户正在编辑的文本，造成输入跳动
 *  解决：syncTextToList 开始时置 true，用 setTimeout(0) 在事件循环末尾重置
 *  watch 检测到 syncingFromList===true 时跳过，不执行 syncListToText
 */
let syncingFromList = false

/*
 *  把 textarea 文本解析成学生数组，并通知父组件
 *  解析规则：先按换行分割，每行再按逗号分割，去空白、去重
 *  保留已有学生的权重值（通过 name 匹配），新学生默认 weight=1.0
 */
function syncTextToList() {
  syncingFromList = true
  const names = rawListText.value.split(/[\r\n]+/).flatMap(l => l.split(',')).map(n => n.trim()).filter(Boolean)
  const unique = [...new Set(names)]
  /* 保留已有学生的权重与信封颜色（按 name 匹配） */
  const existing = new Map(props.studentList.map(s => [s.name, s]))
  emit('update:studentList', unique.map(name => {
    const prev = existing.get(name)
    return { name, weight: prev?.weight ?? 1.0, letterColor: prev?.letterColor || 'rainbow' }
  }))
  setTimeout(() => { syncingFromList = false }, 0)
}

/*
 *  把学生数组转回 textarea 文本（每行一个名字）
 *  调用时机：外部数据变化时（watch 触发）、删除学生后
 */
function syncListToText() {
  rawListText.value = props.studentList.map(s => s.name).join('\n')
}

// ================================================================
//  2. 文件导入：读取用户选择的 txt/csv 文件
// ================================================================

/*
 *  处理文件选择器的 change 事件
 *  用 FileReader 读取文件内容，填入 textarea，触发同步
 *  注意：读取完成后要把 input.value 清空，否则再次选择同一文件不会触发 change
 */
function handleFileUpload(e) {
  const file = e.target.files[0]
  if (!file) return
  const reader = new FileReader()
  reader.onload = ev => {
    rawListText.value = ev.target.result.split(/[\r\n]+/).map(l => l.trim()).filter(Boolean).join('\n')
    syncTextToList()
    e.target.value = ''
  }
  reader.readAsText(file, 'utf-8')
}

// ================================================================
//  3. 权重操作：增删改查
// ================================================================

/*
 *  修改单个学生的权重值
 *  参数 i: 学生在数组中的索引
 *  参数 e: input 事件对象，e.target.value 是滑条的当前值
 *  注意：必须创建新数组再 emit，Vue 的响应式依赖引用变化
 */
function setWeight(i, val) {
  const list = [...props.studentList]
  list[i] = { ...list[i], weight: val }
  emit('update:studentList', list)
}

/* 信封颜色选项：彩 / 黄 / 蓝 */
const letterColorOptions = [
  { value: 'rainbow', label: '彩色信封（默认）' },
  { value: 'gold', label: '黄色信封' },
  { value: 'blue', label: '蓝色信封' }
]

/* 设置单个学生的信封颜色（抽卡浮窗中的信封按此显示） */
function setLetterColor(i, color) {
  const list = [...props.studentList]
  list[i] = { ...list[i], letterColor: color }
  emit('update:studentList', list)
}

/*
 *  删除指定索引的学生
 *  同时调用 syncListToText 更新 textarea，保持文本框与数组一致
 */
function removeStudent(i) {
  const list = [...props.studentList]
  list.splice(i, 1)
  emit('update:studentList', list)
  syncListToText()
}

/*
 *  将所有学生权重重置为 1.0
 */
function resetWeights() {
  emit('update:studentList', props.studentList.map(s => ({ ...s, weight: 1.0 })))
}

// ================================================================
//  4. 概率计算 & 滑条轨道填充
// ================================================================

/*
 *  计算所有学生权重的总和（响应式计算属性，任一学生权重变化时自动重算）
 */
const totalWeight = computed(() => props.studentList.reduce((sum, s) => sum + (s.weight || 0), 0))

/*
 *  计算单个学生在单次抽取中的被抽中概率
 *  公式：该学生权重 ÷ 总权重 × 100%
 *  返回格式：保留两位小数的百分比字符串，如 "12.50%"
 *  边界处理：总权重为 0 或该学生权重为 0 时返回 "0.00%"
 */
function studentProb(s) {
  const tw = totalWeight.value
  if (!tw || !s.weight) return '0.00%'
  return ((s.weight / tw) * 100).toFixed(2) + '%'
}

// ================================================================
//  5. 数据监听：外部数据变化时自动同步 textarea
// ================================================================

/*
 *  监听 props.studentList 的变化
 *  immediate: true  → 组件首次挂载时立即执行一次（加载已保存的配置）
 *  deep: true      → 监听数组内部元素的变化（权重值改变也算）
 *  syncingFromList  → 如果是用户输入触发的变更则跳过，避免死循环
 */
watch(() => props.studentList, () => {
  if (!syncingFromList) syncListToText()
}, { immediate: true, deep: true })

// ================================================================
//  7. 芯片点击跳转：点击芯片 → 滚动到权重列表对应行
// ================================================================

/*
 *  studentRefs 存储每个 roster-item DOM 元素的引用
 *  通过模板中的 :ref="el => { if (el) studentRefs[i] = el }" 填充
 *  键为数组索引，值为对应的 DOM 元素
 */
const studentRefs = {}

/*
 *  点击芯片后平滑滚动到对应学生的权重行
 *  同时将该行背景高亮为 #66ccff，1.2 秒后恢复
 *  注意：如果该学生行内有权重概率显示(.weight-prob)，高亮时文字改为白色以确保可读性
 */
function scrollToStudent(i) {
  const el = studentRefs[i]
  if (el) {
    el.scrollIntoView({ behavior: 'smooth', block: 'center' })
    const probEl = el.querySelector('.weight-prob')
    el.style.background = '#66ccffbb'
    if (probEl) probEl.style.color = '#fff'
    setTimeout(() => {
      el.style.background = ''
      if (probEl) probEl.style.color = ''
    }, 1200)
  }
}
</script>

<style scoped>
/*
 *  本组件完整样式表
 *  主题色：天依蓝 #66ccff
 *  所有颜色硬编码，不依赖外部 CSS 变量（除 --accent 用于部分控件）
 */

/* ===== 主题色 & 全局 ===== */
.tab-page {
  --accent: #66ccff;
  animation: slide-in 0.3s cubic-bezier(0.25, 0, 0.25, 1);
}
@keyframes slide-in {
  from { opacity: 0; transform: translateX(24px); }
  to   { opacity: 1; transform: translateX(0); }
}

/* ===== 班级管理 ===== */
.class-bar {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
}
.class-actions {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
  margin-left: auto;
}
.class-empty-hint {
  margin-top: 10px;
  font-size: 12px;
  color: #99a;
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
  background: rgba(102, 204, 255, 0.35);
  border-radius: 3px;
}
::-webkit-scrollbar-thumb:hover {
  background: rgba(102, 204, 255, 0.55);
}
::-webkit-scrollbar-button {
  display: none;
}
::-webkit-scrollbar-corner {
  background: transparent;
}

.cfg-input {
  width: 80px;
  padding: 4px 8px;
  border: 1px solid #ddd;
  border-radius: 6px;
  font-size: 13px;
  text-align: center;
}

/* ===== 名单导入区域 ===== */
.import-capsule {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 8px 12px;
  margin-bottom: 12px;
  border-radius: 999px;
  border: 1.5px solid #898989;
  background: #fff;
}
.import-hint {
  flex: 1;
  font-size: 12px;
  color: #888;
  line-height: 1.4;
}
.upload-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 7px 16px 5px;
  border-radius: 999px;
  background: var(--accent);
  color: #fff;
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
  white-space: nowrap;
  flex-shrink: 0;
}
.upload-btn:hover {
  filter: brightness(1.06);
}
.count-badge {
  padding: 7px 8px 5px;
  font-size: 12px;
  color: #fff;
  white-space: nowrap;
  flex-shrink: 0;
  background: var(--accent);
  border-radius: 999px;
  font-weight: 600;
  align-items: center;
  display: inline-flex;
}

/* ===== 名单左右布局 ===== */
.roster-layout {
  display: flex;
  gap: 14px;
  align-items: flex-start;
}
.roster-left {
  flex: 0 0 34%;
  display: flex;
  flex-direction: column;
}
.roster-right {
  flex: 1;
  padding: 2px;
  max-height: 220px;
  overflow-y: auto;
}

/* ===== Textarea 稿纸区 ===== */
.roster-textarea {
  width: 100%;
  height: 220px;
  resize: none;
  border: 1.5px solid #c8d4e0;
  border-radius: 10px;
  padding: 10px 12px;
  font-size: 14px;
  font-family: 'UI', 'Bahnschrift', 'Microsoft YaHei UI', sans-serif;
  line-height: 2;
  letter-spacing: 0.5px;
  outline: none;
  transition: border-color 0.2s, box-shadow 0.2s;
  background-color: #fdfcf8;
  color: #334;
  box-shadow: inset 0 1px 3px rgba(0, 0, 0, 0.04);
  white-space: nowrap;
}
.roster-textarea:hover {
  border-color: #a0b8cc;
}
.roster-textarea:focus {
  border-color: var(--accent);
  box-shadow: 0 0 0 3px rgba(102, 204, 255, 0.12), inset 0 1px 3px rgba(0, 0, 0, 0.04);
  background-color: #fffef9;
}

/* ===== 学生芯片（右侧预览） ===== */
.name-chip-grid {
  display: flex;
  flex-wrap: wrap;
  gap: 5px;
}
.name-chip {
  text-align: center;
  padding: 5px 8px;
  border-radius: 999px;
  background: var(--accent);
  color: #fff;
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
  transition: transform 0.15s, filter 0.15s;
  user-select: none;
  white-space: nowrap;
}
.name-chip:hover {
  filter: brightness(1.1);
}
.name-chip:active {
  transform: scale(0.96);
  filter: brightness(0.85);
}
.chip-empty-hint {
  display: flex;
  align-items: center;
  justify-content: center;
  height: 100%;
  min-height: 140px;
  color: #aab4c0;
  font-size: 13px;
  text-align: center;
  line-height: 1.6;
  user-select: none;
}

/* ===== 权重管理区域 ===== */
.weight-head-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin: 10px 0;
}


/* 空状态 */
.empty-tip {
  color: #ccc;
  font-size: 14px;
  text-align: center;
  padding: 16px 0;
}
.empty-arona-img {
  width: 30%;
  opacity: 0.8;
  display: block;
  margin: 0 auto 8px;
}

/* 学生列表 */
.student-table-wrap {
  max-height: 240px;
  overflow-y: auto;
}
.roster-list {
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.roster-item {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 10px;
  border-radius: 8px;
  background: #f7f9fb;
  transition: background 0.3s;
}
.roster-item:nth-child(even) {
  background: #eef2f7;
}
.roster-name {
  flex: 1;
  font-size: 13px;
  font-weight: 600;
  color: #334;
}
.roster-letter {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
}
.letter-dot {
  width: 14px;
  height: 14px;
  padding: 0;
  border: 2px solid rgba(0, 0, 0, 0.12);
  border-radius: 50%;
  cursor: pointer;
  transition: transform 0.15s, border-color 0.15s;
}
.letter-dot:hover {
  transform: scale(1.05);
}
.letter-dot.active {
  border-color: #334;
  transform: scale(1.1);
}
.letter-rainbow {
  background: #b983ff;
}
.letter-gold {
  background: #ffd93d;
}
.letter-blue {
  background: #4d96ff;
}
.roster-weight {
  display: flex;
  align-items: center;
  gap: 6px;
}
.roster-del {
  width: 24px;
  height: 24px;
  border: none;
  border-radius: 50%;
  background: #fee;
  color: #e55;
  font-size: 12px;
  cursor: pointer;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: all 0.2s;
}
.roster-del:hover {
  background: #fcc;
  color: #d33;
}
.roster-reset {
  padding: 4px 12px;
  border: 1px solid #dde;
  border-radius: 999px;
  background: #fff;
  cursor: pointer;
  font-size: 11px;
  color: #88a;
  font-family: inherit;
  white-space: nowrap;
  transition: all 0.2s;
}
.roster-reset:hover {
  border-color: #aab;
  color: #667;
}

.weight-prob {
  display: inline-block;
  min-width: 48px;
  text-align: right;
  font-size: 11px;
  font-weight: 500;
  color: var(--accent);
}
</style>


