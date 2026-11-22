# WindowsManager 模块说明文档

## 概述
`WindowsManager` 模块位于 `Main/Modules/WindowsManager`，是 Blue Random 的多线程窗口调度系统、系统原生托盘中枢与视图业务控制器聚合层。本模块基于严格的 COM 单线程套间（STA）隔离原则，负责创建并管理悬浮按钮、结果演播窗、结果暂留窗与配置面板的独立线程与消息循环；承载原生 Win32 系统托盘图标（`TrayService`）；并包含解耦出来的各个视图业务控制器（`FloatingButtonController`、`LastResultPresenter` 与 `LastResultAutoCloseController`）。

---

## 功能清单

### 1. 多 STA 线程并发拉起与初始化 (`WindowManager.InitializeAllThreads`)
- **1. 标题**: `WindowManager.InitializeAllThreads`
- **2. 用途**: 为悬浮球（FCB）、结果浮窗（FRW）、结果暂留窗（LRW）和 WebUI 配置面板（CP）分别拉起专职的后台 STA 线程与消息循环，并初始化系统托盘（`Tray_UI_Thread`）。
- **3. 参数**:
  - 无输入参数。
  - **返回值** (`void`)。
- **4. 注意事项**:
  - **线程隔离优势**: 
    - `Fcb_UI_Thread`: 专职承载悬浮球拖拽与弹性展开动画，不受复杂渲染或 Chromium 网页加载卡顿的影响。
    - `Frw_UI_Thread`: 专职承载 DirectComposition / Direct2D 演播动画，自有 Win32 消息泵。
    - `Cp_UI_Thread`: 唯一承载 WebView2 浏览器的 UI 线程（`EBWebView` 目录），首次打开时惰性初始化，彻底规避跨线程访问 COM 控件导致的 `0x80010106`（RPC_E_WRONG_THREAD）异常。
    - `Lrw_UI_Thread`: 专职承载右上角常驻卡片，按需平滑滑入滑出。
  - 所有线程均标记 `IsBackground = true` 与 `ApartmentState.STA`。

---

### 2. 原生系统托盘体系 (`TrayService`)
- **1. 标题**: `TrayService`
- **2. 用途**: 在独立 STA 线程（`Tray_UI_Thread`）创建并维护 Windows 任务栏系统托盘图标，响应左键点击/双击激活配置面板，右键唤出纯代码构建的 Fluent 圆角上下文菜单（配置面板、显示悬浮球、重启应用、退出程序）。
- **3. 参数**:
  - `Initialize()`: 绑定 `Default` 交互桌面，创建隐藏 `HwndSource`（`BlueRandomTraySink`）并调用 `Shell_NotifyIcon(NIM_ADD, ...)`。
  - `Dispose()`: 程序退出时调用 `Shell_NotifyIcon(NIM_DELETE, ...)` 移除托盘图标，销毁 `HwndSource` 并关闭托盘 Dispatcher。
- **4. 注意事项**:
  - 完全使用原生 Win32 P/Invoke 实现（`NOTIFYICONDATA`），不依赖 `System.Windows.Forms.NotifyIcon`。
  - 托盘图标优先从当前可执行文件提取关联图标，其次回退至 `Resources/image/app.ico` 与系统默认图标。

---

### 3. 悬浮按钮业务控制器 (`FloatingButtonController`)
- **1. 标题**: `FloatingButtonController`
- **2. 用途**: 托管悬浮球的全部业务状态与逻辑控制。负责外观配置归一化计算、图标多级解析、停靠坐标边界校验、抽取人数范围夹取（1-10 人），并在用户确认时向核心调度器广播 `RandomTriggered` 事件。
- **3. 参数**:
  - `ApplyProps(FloatingButtonConfig props)`: 将用户配置转换为视图视觉属性并规划屏幕坐标。
  - `SetPickCount(int count)`: 设定抽取人数并夹取到合法区间 [1, 10]。
  - `StepPickCount(int delta)`: 步进增减人数。
  - `ApplyDefaultPickCount(int count)`: 在人数胶囊收起时热更新默认抽取人数。
  - `ConfirmPick()`: 提交抽卡请求，向 `IpcBroker` 发送 `[Post] @Fcb.RandomTriggered` 消息包。
  - `SetPickerOpen(bool open)`: 更新选择器展开状态。
- **4. 注意事项**:
  - 严格保持与 `FloatingButtonWindow` 的职责边界：几何排版与动画位于 View，状态判定与事件分发位于 Controller。
  - 若调用来自非 UI 线程，内部自动通过 `_view.Dispatcher.Invoke` 切回安全线程。

---

### 4. 暂留窗方块数据映射器 (`LastResultPresenter`)
- **1. 标题**: `LastResultPresenter`
- **2. 用途**: 将抽卡结果学生实体列表（`CalledStudent`）转换为暂留窗专用的方块磁贴视图模型列表（`List<LastResultRow>`），根据人数与姓名视觉长度计算卡片尺寸，并按信封品阶与卡片不透明度生成冻结的淡色背景与边框画刷。
- **3. 参数**:
  - `BuildRows(IReadOnlyList<CalledStudent> students, double opacity = 1.0)`: **返回值** (`List<LastResultRow>`)。
  - `ComputeBlockWidth(string? name)`: 根据姓名视觉字符长度将卡片宽度映射在 `[96, 140]` DIP 之间。
  - `PaletteFor(string? letterColor, double opacity)`: 按品阶生成淡色背景与柔和边框画刷。
  - `BadgeFor(string? letterColor)` / `BrushOf(uint argb)`: 生成渐变徽标画刷或将 `0xAARRGGBB` 转换为已 `Freeze()` 的 `SolidColorBrush`。
- **4. 注意事项**:
  - 纯无状态静态设计，不持有任何 UI 控件，所有生成的 `SolidColorBrush` 均已冻结，安全支持跨线程读取。

---

### 5. 暂留窗自动关闭控制器 (`LastResultAutoCloseController`)
- **1. 标题**: `LastResultAutoCloseController`
- **2. 用途**: 结果暂留浮窗的防重版本号管理与倒计时控制器。负责自动关闭秒数维护、版本号累加防重、动画/计时推进回调与关闭完成回调。
- **3. 参数**:
  - 提供两种构造模式：动画回调模式（由 View 驱动 `DoubleAnimation`）与 `DispatcherTimer` 每秒进度回调模式（便于单元测试）。
  - `Start(int seconds)` / `Start()`: 启动指定秒数的倒计时；若 `seconds <= 0` 则代表不自动关闭，触发隐藏进度条回调。
  - `Stop()`: 停止当前正在进行的倒计时，递增版本号使旧的延迟回调作废。
  - `Seconds` / `Version` / `IsRunning`: 当前配置秒数、版本号计数器与运行状态。
- **4. 注意事项**:
  - 当浮窗被快速重复打开时，递增的 `Version` 保证上一轮残留的动画完成事件绝不会误关闭新一轮的浮窗。
  - 完全解耦 XAML 控件，仅通过函数式回调（`Action`）与外部驱动交互。

