# FloatingButton 视图说明文档

## 概述
`FloatingButton` 视图位于 `Main/Views/FloatingButton`，是常驻在 Windows 桌面的全局透明悬浮按钮窗口（`FloatingButtonWindow`）。该窗体运行在独立的 STA UI 线程（`Fcb_UI_Thread`）中，采用纯原生 WPF 矢量图形与高刷新率渲染，具备极坐标圆弧排版、60fps 呼吸光圈、自主屏幕坐标平滑拖拽与边缘吸附、CubicEase 贝塞尔弹性展开动效、全链路无焦点（NoActivate）抗抢焦设计、原生触控支持与生命周期防误关守卫，是一键触发点名抽卡的主入口。

---

## 功能清单

### 1. 极坐标环绕胶囊几何生成 (`CreateArcCapsuleGeometry` / `PositionOnArc`)
- **1. 标题**: 极坐标动态圆弧几何排版系统
- **2. 用途**: 根据主悬浮按钮的缩放基准尺寸（BaseSize），在极坐标空间中动态计算 120° 跨度的环绕胶囊背景 Path、辐射状分割线（`RadialDivider`）以及按钮控件的绝对角度与倾斜切线（`tiltFactor`），使各个操作按钮精准贴合在圆弧轨迹上。
- **3. 参数**:
  - `cx`, `cy` (`double`): 悬浮球的几何中心点坐标。
  - `rMid` (`double`): 环绕胶囊的中轴半径。
  - `thickness` (`double`): 胶囊环形厚度。
  - `startDeg`, `endDeg` (`double`): 圆弧起始与结束角度（如 -150° 至 -30°）。
  - **返回值** (`PathGeometry`): 用于填充半透明深色毛玻璃底板的闭合几何图形。
- **4. 注意事项**:
  - 当用户在配置面板中自由拖动按钮尺寸滑块时，本系统自动重构整套极坐标系，确保任何尺寸下圆弧与按钮边缘贴合无缝，无锯齿与变形。

---

### 2. 毫秒级抗全屏置顶保活机制 (`OnLoaded -> topTimer`)
- **1. 标题**: 悬浮按钮 Win32 原生置顶保活心跳
- **2. 用途**: 克服 Windows 操作系统在运行 PPT 全屏放映、电子白板软件或全屏游戏时自动将 WPF `Topmost=True` 窗口下沉的系统缺陷。
- **3. 参数**:
  - 内部启动 1 秒间隔的 `DispatcherTimer`。
  - **返回值** (`void`)。
- **4. 注意事项**:
  - 每次心跳调用 `WindowsSystemService.RefreshTopMost(hwnd)`，强制执行 `SWP_NOACTIVATE` 级别的顶层重置，保证悬浮球在课堂全屏投影时绝不消失，且绝不抢夺黑板或 PPT 的交互焦点。

---

### 3. 原生 DragMove 平滑拖拽与左右边缘分屏禁用 (`DragMove` / `WM_GETMINMAXINFO` / `ClampPosition`)
- **1. 标题**: 原生 DragMove 拖拽与 Win32 左右分屏贴靠禁用
- **2. 用途**: 使用 Windows 原生的 `Window.DragMove()`（系统底层 `WM_SYSCOMMAND SC_MOVE`），享受操作系统原生指针捕获与 DPI 零漂移的丝滑跟手体验；同时通过 Win32 层剥离最大化属性与尺寸硬锁定，彻底禁止悬浮按钮在拖动到屏幕最左侧或最右侧边缘时被 Windows 识别为左右半屏分屏贴靠。
- **3. 参数**:
  - `DragMove()`: 系统级平滑拖动调用。
  - `ResizeMode = "NoResize"`，Win32 样式剥离 `WS_MAXIMIZEBOX`、`WS_MINIMIZEBOX` 与 `WS_THICKFRAME`。
  - `WS_EX_TOOLWINDOW`: 注入工具窗口扩展样式，表明该窗口为桌面小工具，不参与常规分屏排版。
  - `WM_GETMINMAXINFO`: 消息钩子将最大追踪尺寸锁定为当前窗口固定尺寸（240x240），彻底断绝操作系统将悬浮窗拉伸为半屏的途径。
  - **返回值** (`void`)。
- **4. 注意事项**:
  - 设有 4 像素移动死区阈值，位移小于 4 像素时仅判定为单次点击，绝不误触拖拽。
  - `DragMove()` 在用户松手后返回，紧接着调用 `ClampPosition()` 依据当前屏幕 `SystemParameters.WorkArea` 执行边距补偿吸附，让圆形主按钮 100% 紧贴屏幕物理边缘，并发送 `[Post] @Fcb.PositionChanged` 事件完成防抖持久化。

---

### 4. 触控输入原生适配与手势屏蔽 (`OnMainButtonTouchDown/Move/Up` / `Stylus`)
- **1. 标题**: 触控独立交互、防鼠标提升与系统手势拦截
- **2. 用途**: 在教学一体机、触控平板或手写屏上提供独立的触控点击与平滑拖拽体验，彻底杜绝 Windows 触控长按水波纹、边缘轻扫手势干扰以及 WPF 触控提升为鼠标事件导致的二次触发。
- **3. 参数**:
  - `TouchEventArgs`: 原生触摸事件，在 `TouchDown` / `TouchMove` / `TouchUp` 中统一标记 `e.Handled = true` 并通过 `CaptureTouch` 锁定单指触点；位移超过 4 像素阈值时直接按 DIP 增量更新窗口坐标并调用 `ClampPosition()`，抬起后上报 `PositionChanged`。
  - `IsTouchPromotedMouseEvent`: 在 `MouseDown` / `MouseMove` / `MouseUp` 入口过滤 `e.StylusDevice?.TabletDevice?.Type == TabletDeviceType.Touch`，作为双重防重保护（同时保留手写笔/鼠标正常交互）。
  - **返回值** (`void`)。
- **4. 注意事项**:
  - 在初始化时显式调用 `Stylus.SetIsPressAndHoldEnabled(MainButtonBorder, false)`、`Stylus.SetIsFlicksEnabled(this, false)`、`Stylus.SetIsTapFeedbackEnabled(this, false)` 与 `Stylus.SetIsTouchFeedbackEnabled(this, false)`，禁止系统在长按或轻触悬浮球时弹出右键动画圆圈与触控反馈。
  - 触控拖拽不调用 `DragMove()`（避免依赖 `Mouse.LeftButton == Pressed` 及触发 Windows 11 Snap 贴靠），而是通过 `TransformFromDevice` 将屏幕坐标换算为 DIP 增量直接移动窗口。

---

### 5. 全链路无焦点（NoActivate）交互体系 (`WS_EX_NOACTIVATE` / `WM_MOUSEACTIVATE`)
- **1. 标题**: 悬浮按钮 NoActivate 无焦点抢占设计
- **2. 用途**: 彻底解决课堂放映 PPT 时，移动或点击悬浮按钮导致 PPT 全屏放映窗口（`screenClass`）失去活动焦点，从而使翻页笔（Presentation Clicker）、方向键、空格键失效的严重教学故障。
- **3. 参数**:
  - `ShowActivated = false`, `Focusable = false`（XAML 层面配置）。
  - `WS_EX_NOACTIVATE = 0x08000000`（Win32 窗口扩展样式）。
  - `WM_MOUSEACTIVATE -> MA_NOACTIVATE = 3`（Win32 消息过滤返回值）。
- **4. 注意事项**:
  - 无论是用鼠标点击、手指轻触还是拖拽移动悬浮球，操作系统均不将活动窗口焦点转移至悬浮按钮，前台正在全屏放映的 PPT 或白板程序恒定保持在 Active 状态，翻页操作完全无感流畅。

---

### 6. 窗口生命周期守卫与 Alt+F4 安全拦截 (`OnPreviewKeyDown` / `OnWindowClosing`)
- **1. 标题**: 悬浮按钮生命周期防误关守卫
- **2. 用途**: 防止教师在误按键盘快捷键 `Alt+F4` 时将悬浮按钮从底层彻底销毁，并修复此前由此导致托盘点击“显示悬浮球”抛出 `InvalidOperationException` 的崩溃缺陷。
- **3. 参数**:
  - `PreviewKeyDown`: 拦截 `Key.System && Key.F4`（Alt+F4），标记 `e.Handled = true` 并转为 `Hide()`。
  - `Closing`: 拦截外部关闭请求，若非宿主销毁指令（`!_isRealClose`），设置 `e.Cancel = true` 并仅执行 `Hide()`。
  - **返回值** (`void`)。
- **4. 注意事项**:
  - 窗口底层 HWND 与 WPF Window 实例在整个应用程序运行周期内始终保持健康存活，托盘菜单点击“显示悬浮球”时调用 `Show()` 100% 顺畅唤醒。
  - 仅在应用程序彻底退出（收到 IPC `Destroy` 命令）时，解除 `Closing` 拦截并执行真正的窗口物理销毁与 IPC 注销。

---

### 7. 环绕胶囊进退场贝塞尔动画 (`TogglePicker`)
- **1. 标题**: `FloatingButtonWindow.TogglePicker`
- **2. 用途**: 展开或收拢人数选择器。展开时触发由中心向外膨胀的弹性弹出动画（0.22s）；收拢时圆弧与按钮向中心主按钮迅速收缩收拢（0.18s），并优雅隐藏。
- **3. 参数**:
  - `open` (`bool`): `true` 表示展开，`false` 表示收拢。
  - **返回值** (`void`)。
- **4. 注意事项**:
  - 采用 `CubicEase` (EaseOut / EaseIn) 缓动函数，实现与《蔚蓝档案》UI 风格高度契合的高级弹性手感。
  - 窗体挂载了 `Deactivated` 失去焦点监听，当教师点击桌面其他任意区域时，胶囊自动平滑收缩，避免阻挡视线。

---

### 8. 人数调节与脉冲动效 (`ApplyPickCountVisual`)
- **1. 标题**: `FloatingButtonWindow.ApplyPickCountVisual`
- **2. 用途**: 响应控制器人数变化。刷新中心人数文本（1 ~ 10 人）、实时更新加减极值按钮的禁用/启用态（达到 1 时禁用减与 MIN，达到 10 时禁用加与 MAX），并触发数字的“心跳脉冲弹跳动画”。
- **3. 参数**:
  - `count` (`int`): 当前目标人数（1-10）。
  - **返回值** (`void`)。
- **4. 注意事项**:
  - 脉冲动效由 XAML 中的 `CountPulseStoryboard`（ScaleTransform 1.0 → 1.25 → 1.0）驱动，伴随 `button_click.wav` 短音效，提供极强的按键确认感。