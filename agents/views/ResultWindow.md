# ResultWindow 视图说明文档

## 概述
`ResultWindow` 视图位于 `Main/Views/ResultWindow`，是 Blue Random 的核心招募演播结果浮窗（`DCompResultWindow`）。该窗体运行在独立的 STA 专职线程（`Frw_UI_Thread`）中，采用前沿的 **DirectComposition + Direct3D 11 + Direct2D** 纯底层硬件加速技术重构，具备全像素 Alpha 透明合成、60fps 超丝滑开箱盖章招募演出、WH_MOUSE_LL 全屏点击即关、专职光标自愈系统，以及演播结束后的前台焦点无缝还原能力。

---

## 架构要点

1. **单 Surface 即时模式渲染**: 
   整窗仅向 Windows 窗口管理器（DWM）提交单一 `IDCompositionSurface` 与单个 Visual。每帧将预渲染好的 D2D 位图按图层顺序（底板 → 信封光晕 → 信封卡片 → 学生姓名 → 底部提示胶囊）一次性绘制，彻底解决了此前多 Visual/多 Surface 在部分显卡驱动下出现部分图层被 DWM 吞掉或掉帧的技术难题。
2. **零软件光栅化**:
   采用无重定向位图扩展样式（`WS_EX_NOREDIRECTIONBITMAP`），绕过传统 GDI/WPF Layered Window 缓慢的 CPU 内存拷屏复制，实现 100% 显卡显存直通合成。

---

## 功能清单

### 1. 结果演播生命周期与开箱动画 (`DisplayResults` / `OnTick`)
- **1. 标题**: 招募结果演播全流程状态机
- **2. 用途**: 接收抽卡名单与视觉配置，全屏居中弹出演播窗，经历完整四阶段：
  - `Idle`: 待机静默态。
  - `Opening`: 进场动画态。白光信封伴随音效自上方滑入，展开信封底板，学生品阶信封根据人数自适应网格排版飞入。
  - `Ready`: 盖章就绪态。播放印章落地特效，显示学生姓名与等级标识。根据配置动态显示底部横条：若启用暂留则显示粉色“点击此处暂留结果”胶囊按钮（`#FF6699`），未启用则显示天蓝色“点击任意处关闭”胶囊按钮（`#66CCFF`），窗口进入可交互关闭状态。
  - `Closing`: 退场态。整窗快速缩小渐隐，触发音量淡出并恢复桌面原貌。
- **3. 参数**:
  - `students` (`List<CalledStudent>`): 抽取命中的学生实体列表。
  - `props` (`PickResultDialogConfig`): 本次展示的视觉与音效参数配置。
- **4. 注意事项**:
  - 每次展示前会自动调用 `ReleaseSurface()` 与 `ReleaseLayers()` 强制清理上一轮结果的显存纹理，杜绝结果残影。
  - 动画计算基于高精度时钟 `Stopwatch`，不受系统卡顿或单帧耗时波动影响，永远保持物理时间恒定。

---

### 2. 专职线程全屏点击关闭与主动暂留判定 (`InstallMouseHook` / `MouseHookCallback`)
- **1. 标题**: 独立专职线程 WH_MOUSE_LL 低级鼠标全局钩子与主动暂留判定
- **2. 用途**: 就绪后，全屏捕获鼠标点击事件与 Esc 键。若开启了暂留配置，点击底部横条触发主动暂留并关闭演播窗；点击屏幕其他区域或按 Esc 键则直接关闭演播窗且不暂留结果。
- **3. 参数**:
  - 安装 Win32 全局低级鼠标钩子 `SetWindowsHookExW(WH_MOUSE_LL, ...)`。
  - **返回值** (`IntPtr`): 钩子返回代码（当窗口已就绪时拦截点击并向主线程投递关闭消息；未就绪时原样传递给系统）。
- **4. 注意事项**:
  - **独立专职线程设计**: Windows 对 `WH_MOUSE_LL` 钩子有极严格的超时限制（若回调耗时超过数毫秒，系统会直接静默注销钩子导致全屏点击失效）。Blue Random 为鼠标钩子单独分配了纯粹的消息泵循环，回调内仅做原子状态比对与 `PostMessage`，耗时小于 10 微秒，稳定性 100%。
  - **DIP 命中判定**: 钩子线程拦截点击后，将屏幕物理坐标通过 `ScreenToClient` 换算至窗口客户区并除以 DPI 缩放比，精确比对底部横条矩形 `(_hintX, _hintY, _hintW, HintHeight)`；若命中则下发 `keepResult = true`，否则为 `false`。
  - 关闭完成后通过 `FrwClosedPayload` 向 Core 投递 `WindowClosed` 消息携带名单与暂留请求。

---

### 3. 光标状态自愈与防转圈机制 (`RefreshCursor` / `ProbeBusyCursor`)
- **1. 标题**: DirectComposition 原生光标防转圈与区域手型
- **2. 用途**: 彻底根除原生无边框窗口常见的光标继承 BUG（窗口刚弹出时，光标呈继承自系统的等待状态“漏斗/转圈”，且在静止不移动鼠标时无法恢复为箭头或手型）。
- **3. 参数**:
  - `ArrowCursor` 与 `HandCursor`: 基于 `LoadCursorW` 获取的真实 Win32 `HCURSOR` 句柄。
- **4. 注意事项**:
  - Win32 API `SetCursor` 只能接收由 `LoadCursorW` 产生的系统句柄，传资源宏 ID 会静默失效。
  - 在 `WM_SETCURSOR`、`WM_MOUSEMOVE` 以及每帧渲染后均主动刷新光标句柄；在开箱动画尚未就绪前锁定为箭头光标。
  - **交互感知光标**: 当启用暂留配置时，鼠标悬停在底部横条范围内自动呈现 `HandCursor`（手型），提示用户此处为可操作的暂留按钮；处于其他区域时呈现 `ArrowCursor`。未开启暂留时，整窗激活手型光标。

---

### 4. 抽卡演播前台焦点自动还原机制 (`DisplayResults` / `FinishClose`)
- **1. 标题**: 演播结束前台活动焦点精准归还
- **2. 用途**: 解决教师在全屏放映 PPT 时触发抽卡，当演播窗弹出并关闭后，PPT 放映失去焦点导致翻页笔（Presentation Clicker）、键盘 PageDown/方向键失效的问题。
- **3. 参数**:
  - `DisplayResults`: 窗口弹出前调用 Win32 `GetForegroundWindow()` 暂存当前前台焦点窗口句柄 `_previousForegroundHwnd`。
  - `FinishClose`: 窗口隐藏（`SW_HIDE`）后，通过 `IsWindow(_previousForegroundHwnd)` 校验并调用 `SetForegroundWindow(_previousForegroundHwnd)` 精准归还焦点。
  - **返回值** (`void`)。
- **4. 注意事项**:
  - 配合悬浮按钮的 `WS_EX_NOACTIVATE` 属性，教师点击悬浮球触发抽卡时，前台焦点仍属于 PPT；演播窗弹出后演播，演播完毕关闭后立刻无感物归原主，使翻页笔在抽卡前后恒定灵敏有效。

---

### 5. 离线帧导出诊断 (`DumpFrame`)
- **1. 标题**: 演播帧诊断图像转储
- **2. 用途**: 供开发者离线诊断 Direct2D 图层绘制排版、字体渲染或色彩对齐。
- **3. 参数**:
  - 支持通过环境变量 `BLUERANDOM_DCOMP_DUMP=1` 开启。
- **4. 注意事项**:
  - 开启后演播窗口关闭时会自动将最终帧导出为 `%TEMP%\br_frw_dump.png`。