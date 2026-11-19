# FloatingButton 视图说明文档

## 概述
`FloatingButton` 视图位于 `Main/Views/FloatingButton`，是常驻在 Windows 桌面的全局透明悬浮按钮窗口（`FloatingButtonWindow`）。该窗体运行在独立的 STA UI 线程（`Fcb_UI_Thread`）中，采用纯原生 WPF 矢量图形与高刷新率渲染，提供 60fps 呼吸光圈、毫秒级流畅平滑拖拽、带 CubicEase 贝塞尔弹性的环绕式半圆胶囊人数选择器，是一键触发点名抽卡的主入口。

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

### 3. 平滑鼠标拖拽与边界吸附 (`OnMainButtonMouseMove` / `ClampPosition`)
- **1. 标题**: 悬浮按钮拖拽判定与工作区吸附
- **2. 用途**: 区分用户的“单次点击（展开选择器）”与“按住拖动（移动位置）”，并在拖动结束或窗口缩放时将按钮限制在当前屏幕工作区范围内，防止拖出屏幕找不回来。
- **3. 参数**:
  - `currentScreenPos`: 鼠标当前屏幕绝对像素坐标。
  - **返回值** (`void`)。
- **4. 注意事项**:
  - 设有 4 像素移动阈值，移动距离未超过 4 像素时仅视为点击，绝不产生误拖拽抖动。
  - 吸附判定严格依据 `SystemParameters.WorkArea`，自动避开 Windows 任务栏占据的区域。
  - 拖拽完成后自动向核心调度器广播 `MoveNewPosition`，防抖记录最新坐标并在保存时持久化。

---

### 4. 环绕胶囊进退场贝塞尔动画 (`TogglePicker`)
- **1. 标题**: `FloatingButtonWindow.TogglePicker`
- **2. 用途**: 展开或收拢人数选择器。展开时触发由中心向外膨胀的弹性弹出动画（0.22s）；收拢时圆弧与按钮向中心主按钮迅速收缩收拢（0.18s），并优雅隐藏。
- **3. 参数**:
  - `open` (`bool`): `true` 表示展开，`false` 表示收拢。
  - **返回值** (`void`)。
- **4. 注意事项**:
  - 采用 `CubicEase` (EaseOut / EaseIn) 缓动函数，实现与《蔚蓝档案》UI 风格高度契合的高级弹性手感。
  - 窗体挂载了 `Deactivated` 失去焦点监听，当教师点击桌面其他任意区域时，胶囊自动平滑收缩，避免阻挡视线。

---

### 5. 人数调节与脉冲动效 (`ApplyPickCountVisual`)
- **1. 标题**: `FloatingButtonWindow.ApplyPickCountVisual`
- **2. 用途**: 响应控制器人数变化。刷新中心人数文本（1 ~ 10 人）、实时更新加减极值按钮的禁用/启用态（达到 1 时禁用减与 MIN，达到 10 时禁用加与 MAX），并触发数字的“心跳脉冲弹跳动画”。
- **3. 参数**:
  - `count` (`int`): 当前目标人数（1-10）。
  - **返回值** (`void`)。
- **4. 注意事项**:
  - 脉冲动效由 XAML 中的 `CountPulseStoryboard`（ScaleTransform 1.0 → 1.25 → 1.0）驱动，伴随 `button_click.wav` 短音效，提供极强的按键确认感。
