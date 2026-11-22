# ConfigPanel 视图与页面说明文档

## 概述
`ConfigPanel` 位于 `Main/Views/ConfigPanel`（原生 WPF 窗体宿主）与 `WebUI/ConfigPanel`（Vue 3 + Vite + riz-ui 前端单页面应用）。作为系统的控制台界面，它承载完整的配置查看与修改、多班级名单录入、个性化多媒体素材上传、Windows 高级权限设定、运行日志诊断以及版本检测功能。

---

## 原生宿主窗体功能清单 (`ConfigPanelWindow`)

### 1. 窗口隐藏式秒开机制 (`OnWindowClosing`)
- **1. 标题**: `ConfigPanelWindow.OnWindowClosing` 隐藏式常驻设计
- **2. 用途**: 拦截用户的窗口关闭事件（如点击右上角 ✕ 按钮或快捷键关闭），取消窗口的实际销毁（Dispose），仅调用 `Hide()` 隐藏窗口。
- **3. 参数**:
  - `e` (`CancelEventArgs`): 窗体关闭事件参数，将 `e.Cancel` 设为 `true`。
  - **返回值** (`void`)。
- **4. 注意事项**:
  - 保持 Chromium (WebView2) 渲染上下文与 DOM 树驻留内存，使后续从系统托盘再次点击“配置面板”时能够实现瞬间呼出，根除 Chromium 冷启动白屏。
  - 只有在收到 IPC `Destroy` 指令（整个程序彻底退出）时，才解除此拦截并彻底关闭窗体。

---

### 2. 窗口前台激活与焦点守卫 (`OnIpcMessageReceived -> ShowConfigPanel`)
- **1. 标题**: `ConfigPanelWindow` 前台激活与置顶抖动
- **2. 用途**: 响应来自托盘菜单的 `ShowConfigPanel` 唤醒指令。将窗口从最小化还原，呼至 Windows 桌面最顶层并获取输入焦点。
- **3. 参数**:
  - `msg` (`IpcMessage`): 携带 `Command = "ShowConfigPanel"` 的消息包。
  - **返回值** (`void`)。
- **4. 注意事项**:
  - 采用 `Topmost = true; Topmost = false;` 的瞬态抖动模式，既能突破 Windows 系统的前台激活限制将配置面板带至最前，又不会永久锁定置顶，避免阻挡用户日常操作。
  - 若调用时 WebView2 尚未初始化就绪，内部会自动触发 `EnsureReadyAsync()` 补充初始化。
  - 另处理 `FetchConfig` / `OverwriteConfig` 指令，通过 `PushConfigUpdatedAsync` 向前端推送最新 YAML。

---

## 前端选项卡页面功能清单 (`WebUI/ConfigPanel/views`)

### 3. 名单管理页面 (`TabRoster.vue`)
- **1. 标题**: 名单管理选项卡 (`TabRoster`)
- **2. 用途**: 提供多班级管理（新建/刷新/重命名/删除/切换）、学生名单文本录入与 txt/csv 文件导入、芯片预览、权重滑块调整（0–5，步长 0.1）以及单轮重复抽取开关。
- **3. 参数**:
  - 接收 `studentList`、`allowRepeatDraw`、`saveVersion` props，通过 `update:studentList`、`update:allowRepeatDraw`、`update:activeClassId` 等事件上报给 `ConfigPanel.vue`。
- **4. 注意事项**:
  - 单个学生权重范围为 0–5（默认 1.0）；权重设为 0 表示在正权重人数充足时暂不优先抽中。
  - 班级新建/重命名/删除通过 `GetClassesJson` / `CreateClassJson` / `RenameClass` / `DeleteClass` 直连后端；切换班级与名单/权重修改在点击底部“应用”时通过 `SaveConfigYaml` 统一同步落盘。

---

### 4. 悬浮按钮设置页面 (`TabFloating.vue`)
- **1. 标题**: 悬浮按钮选项卡 (`TabFloating`)
- **2. 用途**: 定制常驻桌面的悬浮球行为与外观。包含默认抽取人数预设（1–10 人）、实时位置 X/Y 读取与编辑、持续置顶开关、任务栏可见开关、按钮大小缩放（50%–200%）、自定义本地图标上传与预览、图标大小（16–96px）以及边框颜色选择器。
- **3. 参数**:
  - 绑定 `draft.floatingButton` 与 `draft.pickCountDialog` 配置草稿对象。
- **4. 注意事项**:
  - 上传自定义图标时通过 HTML5 Canvas 转码为最大 256px 的 32 位 PNG 并转为 Base64 调用 `SaveCustomResourceJson` 暂存，点击“应用”后绑定生成的 `customIconId`。
  - 调整默认抽取人数后，保存时会同步触发 `@Fcb.ApplyDefaultPickCount`，在悬浮按钮未展开时即时静默同步。

---

### 5. 结果浮窗设置页面 (`TabResult.vue`)
- **1. 标题**: 结果浮窗选项卡 (`TabResult`)
- **2. 用途**: 定制抽卡演播窗的视觉与听觉体验，以及配置常驻右上角的“结果暂留浮窗”。包括浮窗背景/边框色与不透明度；结果暂留浮窗开关、底色、边框色、不透明度与自动关闭时长（0–100s，0 为不自动关闭）；自定义抽取音效上传/试听/移除与音量；自定义 BGM 上传/移除/试听、音量、起始位置时间轴与淡入淡出时长。
- **3. 参数**:
  - 绑定 `draft.pickResultDialog` 配置草稿对象。
- **4. 注意事项**:
  - 版权音乐（BGM）与自定义抽取音效不随仓库分发，页面提供格式校验、时长探测与上传交互。
  - 暂留浮窗自动关闭时间设为 0 时表示“不自动关闭”，支持随时点击卡片右上角 ✕ 或底部“点击此处关闭”按钮滑出关闭。

---

### 6. 高级设置页面 (`TabAdvanced.vue`)
- **1. 标题**: 高级设置选项卡 (`TabAdvanced`)
- **2. 用途**: 集中管理 Windows 系统底层特权、配置维护、安全锁与在线更新。支持创建/更新 Windows 计划任务开机自启（`CreateStartupTask`）、开启/关闭启动时管理员权限与 UIAccess 置顶增强、打开配置目录、清理 WebView2 缓存、重置所有配置、重启/管理员重启、设置/更改/关闭管理员密码锁（至少 4 位，SHA-256 存储在 `.SHA256`），以及一键检查 GitHub Releases 更新（`CheckUpdateJson`，展示远端 Release 的 `changelog.md`；发现新版本时支持弹出进度 Dialog 下载 `BlueRandom.exe` 并自动替换重启）。
- **3. 参数**:
  - 绑定 `draft.admin`、`appInfo`、`securityEnabled` 及更新检查状态 props。
- **4. 注意事项**:
  - 开启 UIAccess 需先开启“启动时获得管理员权限”；开启后下次启动程序将具备穿透全屏窗口的顶级置顶特权。
  - 密码保护开启后，打开配置面板会覆盖解锁遮罩，需输入正确密码解锁（忘记密码仅可通过重置所有配置恢复）。

---

### 7. 关于应用页面 (`TabAbout.vue`)
- **1. 标题**: 关于应用选项卡 (`TabAbout`)
- **2. 用途**: 纯展示页面，呈现应用 Logo、版本号、当前版本发布代号（如 `Momoi`）、开源与字体许可信息（IPA Font License、Font Awesome Free、AGPLv3、RunUIAccess MIT）、相关链接以及版权免责声明。
- **3. 参数**:
  - 接收 `appInfo` prop。
- **4. 注意事项**:
  - 在线检查更新功能位于 `TabAdvanced.vue`，本页面仅作静态信息与链接展示。

---

### 8. 运行日志面板 (`TabLogs.vue`)
- **1. 标题**: 运行日志面板 (`TabLogs`)
- **2. 用途**: 固定展示在配置面板右侧栏，实时查看应用程序在后台产生的运行日志，支持省电懒加载遮罩（点击“加载日志”后每 800ms 轮询 `GetLogsJson(500)`）、管理员/UIAccess/版本徽标展示以及清空当前视图。
- **3. 参数**:
  - 接收 `appInfo` prop，暴露 `load`、`reload`、`clear` 方法供 `ConfigPanel.vue` 调用。
- **4. 注意事项**:
  - 本组件采用异步按需加载拆分（`tab-logs` chunk），避免拖慢配置面板首屏冷启动速度。
  - 日志按时间正序展示（最新日志在最底部并自动平滑滚动到底部），最多保留最近 300 条显示。

