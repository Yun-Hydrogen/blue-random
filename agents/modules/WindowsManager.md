# WindowsManager 模块说明文档

## 概述
`WindowsManager` 模块位于 `Main/Modules/WindowsManager`，是 Blue Random 的多线程窗口调度系统、系统原生托盘中枢与视图业务控制器聚合层。本模块基于严格的 COM 单线程套间（STA）隔离原则，负责创建并管理悬浮按钮、结果演播窗、结果暂留窗与配置面板的独立线程与 WPF Dispatcher 消息循环；承载原生 Win32 系统托盘图标（`TrayService`）；并包含解耦出来的各个视图业务控制器（`FloatingButtonController`、`LastResultPresenter` 与 `LastResultAutoCloseController`）。

---

## 功能清单

### 1. 多 STA 线程并发拉起与初始化 (`WindowManager.InitializeAllThreads`)
- **1. 标题**: `WindowManager.InitializeAllThreads`
- **2. 用途**: 为悬浮球（FCB）、结果浮窗（FRW）、结果暂留窗（LRW）和 WebUI 配置面板（CP）分别拉起专职的后台 STA 线程与 Win32/WPF 消息循环，并初始化系统托盘。
- **3. 参数**:
  - 无输入参数。
  - **返回值** (`void`)。
- **4. 注意事项**:
  - **线程隔离优势**: 
    - `Fcb_UI_Thread`: 专职承载高帧率鼠标拖拽与呼吸动画，不受复杂图表或 Chromium 网页加载卡顿的影响。
    - `Frw_UI_Thread`: 专职承载 DirectComposition / Direct2D 演播动画，自有专职消息泵。
    - `Cp_UI_Thread`: 唯一承载 WebView2 浏览器的 UI 线程，彻底规避跨线程访问 COM 控件导致的 `0x80010106`（RPC_E_WRONG_THREAD）异常。
    - `Lrw_UI_Thread`: 专职承载右上角常驻卡片，按需平滑滑入滑出。
  - 所有线程均标记 `IsBackground = true` 与 `ApartmentState.STA`。

---

### 2. 原生系统托盘体系 (`TrayService`)
- **1. 标题**: `TrayService`
- **2. 用途**: 创建并维护 Windows 任务栏系统托盘图标，响应左键点击激活配置面板，右键唤出现代化深色亚克力/圆角上下文菜单（开始抽卡、配置、重启、退出等）。
- **3. 参数**:
  - `Initialize()`: 创建纯 Win32 消息侦听隐藏窗口并调用 `Shell_NotifyIcon(NIM_ADD, ...)`。
  - `Dispose()`: 程序退出时调用 `Shell_NotifyIcon(NIM_DELETE, ...)` 安全移除托盘图标，防止遗留僵尸图标。
- **4. 注意事项**:
  - 完全使用原生 Win32 P/Invoke 实现（`NOTIFYICONDATA`），不依赖陈旧笨重的 `System.Windows.Forms.NotifyIcon`。
  - 支持 Windows 任务栏重启广播消息（`TaskbarCreated`），当 Explorer 崩溃自愈后自动重新挂载托盘图标。

---

### 3. 悬浮按钮业务控制器 (`FloatingButtonController`)
- **1. 标题**: `FloatingButtonController`
- **2. 用途**: 托管悬浮球的全部业务状态与逻辑控制。负责外观配置归一化计算、图标多级解析、停靠坐标边界校验、抽取人数范围夹取（1-10 人），并在用户确认时向核心调度器广播 `RandomTriggered` 事件。
- **3. 参数**:
  - `ApplyProps(FloatingButtonConfig props)`: 将用户配置转换为视图视觉属性并规划屏幕坐标。
  - `SetPickCount(int count)`: 设定抽取人数并夹取到合法区间 [1, 10]。
  - `StepPickCount(int delta)`: 步进增减人数。
  - `ApplyDefaultPickCount(int count)`: 在人数胶囊收起时热更新默认抽取人数。
  - `ConfirmPick()`: 提交抽卡请求，向 `IpcBroker` 发送 `RandomTriggered` 消息包。
- **4. 注意事项**:
  - 严格保持与 `FloatingButtonWindow` 的职责边界：几何排版与动画位于 View，状态判定与事件分发位于 Controller。
  - 若调用来自非 UI 线程，内部自动通过 `_view.Dispatcher.Invoke` 切回安全线程。

---

### 4. 暂留窗列表行数据映射器 (`LastResultPresenter`)
- **1. 标题**: `LastResultPresenter`
- **2. 用途**: 将抽卡结果学生实体列表（`CalledStudent`）转换为暂留窗专用的视图行模型列表（`LastResultRow`），根据学生权重与信封品质生成并冻结专属的线性渐变圆点画刷。
- **3. 参数**:
  - `BuildRows(IReadOnlyList<CalledStudent> students)`: **返回值** (`List<LastResultRow>`)。
  - `BrushOf(uint argb)`: 将 0xAARRGGBB 十六进制数值转换为已调用 `Freeze()` 的只读安全画刷。
- **4. 注意事项**:
  - 纯无状态静态设计，不持有任何 UI 控件，所有生成的 `Brush` 均已冻结，安全支持跨线程读取。

---

### 5. 暂留窗自动关闭控制器 (`LastResultAutoCloseController`)
- **1. 标题**: `LastResultAutoCloseController`
- **2. 用途**: 结果暂留浮窗的防重版本号管理与倒计时控制器。负责自动关闭秒数维护、版本号累加防重、进度推进回调与关闭完成回调。
- **3. 参数**:
  - `Start(int seconds)`: 启动指定秒数的倒计时；若 `seconds <= 0` 则代表不自动关闭，触发隐藏进度条回调。
  - `Stop()`: 停止当前正在进行的倒计时，递增版本号使旧的延迟回调作废。
  - `Seconds`: 当前配置的倒计时秒数。
  - `Version`: 当前版本号计数器。
- **4. 注意事项**:
  - 当浮窗被快速重复打开（"显示 → 立即再次抽取"）时，递增的 `Version` 保证上一轮残留的动画完成事件绝不会误关闭新一轮的浮窗。
  - 完全解耦 XAML 控件，仅通过函数式回调（`Action`）与外部驱动交互。
