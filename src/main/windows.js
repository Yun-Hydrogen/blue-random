/*
================================================================================
技术文档：src/main/windows.js
职责：主进程窗口管理与随机抽取算法 —— 这是最复杂的模块。

================================================================================
模块导入说明
================================================================================
  require('electron')      → Electron 框架：BrowserWindow（窗口创建）、screen（显示器信息）、
                             shell（打开外部链接）
  require('path')          → 路径拼接（preload.js、index.html 的路径解析）
  require('./config')      → 配置读写模块

================================================================================
核心功能一览
================================================================================
  类别       | 函数                                   | 作用
  -----------|----------------------------------------|------------------------------
  状态管理   | setDebugMode / setQuitting             | 设置全局状态标志
  状态管理   | getFloatingButtonWindow / getPickResultWindow / getConfigPanelWindow | 获取窗口引用
  状态管理   | getCurrentPickResults                  | 获取当前抽取结果
  -----------|----------------------------------------|------------------------------
  悬浮按钮   | createFloatingButtonWindow             | 创建透明无边框悬浮按钮窗口
  悬浮按钮   | refreshFloatingButtonWindow            | 关闭旧的悬浮窗（触发重建）
  悬浮按钮   | getFloatingButtonWindowSize            | 计算悬浮窗所需尺寸（含扩展区）
  悬浮按钮   | setFloatingButtonShape                | 设置窗口命中区域（SetWindowRgn）
  悬浮按钮   | fadeInFloatingButtonWindow / fadeOutFloatingButtonWindow | 窗口级 setOpacity 淡入淡出
  悬浮按钮   | getFloatingButtonPosition             | 获取悬浮按钮当前屏幕位置（实时）
  悬浮按钮   | startFloatingWindowWatchdog / stopFloatingWindowWatchdog | 看门狗（自动修复）
  -----------|----------------------------------------|------------------------------
  拖拽       | handleDragStart / handleDragMove / handleDragEnd | 悬浮按钮拖拽三阶段
  拖拽       | setIgnoreMouseEvents                   | 切换鼠标穿透模式
  -----------|----------------------------------------|------------------------------
  抽取结果   | createPickResultWindowInstance         | 预创建抽取结果窗口（全屏透明）
  抽取结果   | openPickResultWindow                   | 展示抽取结果（发 IPC + 显示窗口）
  抽取结果   | closePickResultWindow                  | 隐藏结果窗口 + 恢复悬浮按钮
  -----------|----------------------------------------|------------------------------
  配置面板   | createConfigPanelWindow                | 创建原生配置面板窗口
  配置面板   | openConfigPanelWindow / closeConfigPanelWindow | 打开/关闭配置面板
  -----------|----------------------------------------|------------------------------
  抽取算法   | pickStudentsByWeight                   | 按权重随机抽取学生（核心算法）
  -----------|----------------------------------------|------------------------------
  工具       | clampBoundsToWorkArea                  | 将窗口约束在工作区内

================================================================================
关键设计决策
================================================================================
  1. 持久化窗口实例：
     抽取结果窗口和配置面板窗口创建后不销毁，用 hide()/show() 控制显隐。
     原因：UIAccess 模式下销毁重建会导致 DWM 复合链路失效（动画冻结）。

  2. 看门狗（Watchdog）：
     每 450ms 检查悬浮窗是否存活，意外销毁时自动重建。
     原因：Electron 窗口可能因系统原因（驱动崩溃、内存不足）意外关闭。

  3. 鼠标穿透（setShape / SetWindowRgn）：
     D3D9 下透明窗口不支持像素级命中测试。setShape 在 OS 层面
     裁剪窗口命中区域 —— 矩形内捕获点击，矩形外穿透。
     收缩态裁剪为按钮大小，展开态裁剪为控件区域大小。
     零 JS 开销，不依赖渲染后端透明度行为。

  4. 淡入淡出（win.setOpacity / SetLayeredWindowAttributes）：
     D3D9 下 JS/CSS 层级的 opacity 动画不被透明窗口合成器逐帧执行。
     改用窗口级 setOpacity()，通过 setInterval 每 16ms 更新一次
     窗口全局透明度。show() + blur() 确保焦点不残留。

  5. Vulkan 黑帧处理（2026-08-29）：
     Vulkan 后端下首个透明悬浮窗口会渲染成黑块（交换链创建失败；
     重建后的第二个窗口正常，实测“应用配置触发重建后即恢复”）。
     createFloatingButtonWindow 对本次运行的首个 Vulkan 窗口：
       隐藏创建（show:false）→ 加载完成后自动 destroy → closed 事件
       自动重建（第二个窗口为全新实例，正常显示），用户全程看不到黑块。
     看门狗会跳过带 __vulkanPendingDestroy 标记的待销毁首窗口。

================================================================================
更新记录
================================================================================
  2026-08-29  悬浮按钮窗口新增 Vulkan 黑帧处理（见“关键设计决策 5”）
              首窗口改为“隐藏加载 → 自动销毁重建”，替代无效的 reload 方案
  2026-08-29  pickStudentsByWeight 结果携带 letterColor（信封颜色，缺省 rainbow）
  2026-08-29  修复悬浮按钮坐标 X 始终为 0：
              - createFloatingButtonWindow 位置恢复判断改为显式排除 null/undefined，
                因 Number(null) === 0，会把“自动位置”误判为已保存并强制放至 x=0

================================================================================
维护建议
================================================================================
  - 新增窗口类型时，如需持久化实例必须做好 reset 机制（通过 IPC 通知渲染进程重置状态）
  - 修改抽取算法时注意两种模式（允许/禁止重复）的不同实现路径
  - 拖拽逻辑牵涉三个 IPC 通道 + 渲染进程 PointerEvent，修改时需两端同步
  - 看门狗间隔 450ms 是平衡CPU占用和响应速度的经验值，不要随意缩短
================================================================================
*/
const { BrowserWindow, screen, shell } = require('electron');
const path = require('path');
const config = require('./config');

// ============================================================================
//  模块级状态变量（闭包内的全局状态，供所有函数共享）
// ============================================================================

/* 拖拽会话：key = webContents.id, value = { startWinX, startWinY, width, height } */
const dragSessions = new Map();

let floatingButtonWindow = null;    // 悬浮按钮窗口实例
let pickResultWindow = null;        // 抽取结果窗口实例（持久化）
let configPanelWindow = null;       // 配置面板窗口实例

let isFloatingHiddenForPickCount = false;  // 悬浮窗是否因抽取/配置而临时隐藏
let isPickResultWindowReady = false;       // 抽取结果窗口是否已加载完成
let isConfigPanelOpen = false;             // 配置面板是否打开中
let isDebugMode = false;                   // 是否为调试模式
let isQuitting = false;                    // 应用是否正在退出

let currentPickResults = [];        // 当前抽取结果缓存
let pickResultToken = 0;            // 抽取结果批次计数器（自增）
let activePickResultToken = 0;      // 当前活跃的抽取批次 token

let floatingExpanded = false;       // 悬浮窗是否处于展开状态
let floatingExpandedSize = null;    // 展开时的窗口尺寸 { width, height }
/* Vulkan 黑帧处理：本次运行是否已执行过“首窗口隐藏加载→销毁重建” */
let vulkanFirstWindowDone = false;

let floatingWindowWatchdog = null;  // 看门狗定时器句柄

/* 动画时长（毫秒） */
const FLOATING_WINDOW_FADE_MS = 400;

/* 权重增强指数：weight^gamma 放大权重差异，默认 γ=1.5 */
const WEIGHT_BOOST_GAMMA = 1.5;


// ============================================================================
//  1. 状态读写 —— 简单的 getter/setter
// ============================================================================

function setDebugMode(value) { isDebugMode = Boolean(value); }
function setQuitting(value) { isQuitting = Boolean(value); }
function getFloatingButtonWindow() { return floatingButtonWindow; }
function getPickResultWindow() { return pickResultWindow; }
function getConfigPanelWindow() { return configPanelWindow; }
function getCurrentPickResults() { return currentPickResults; }

/*
 * 关闭旧的悬浮按钮窗口（配置变更时调用）。
 * 窗口被关闭后会触发 closed 事件 → floatingButtonWindow = null，
 * 看门狗检测到后自动重建（带新配置）。
 */
function refreshFloatingButtonWindow() {
  if (floatingButtonWindow && !floatingButtonWindow.isDestroyed()) {
    floatingButtonWindow.close();
  }
}


// ============================================================================
//  2. 悬浮按钮窗口 —— 创建、尺寸计算、展开/收缩
// ============================================================================

/*
 * 计算悬浮按钮窗口的展开态尺寸（容纳环绕人数选择器）。
 * 窗口始终为此大小，不再缩放。透明区域通过 setShape 实现鼠标穿透。
 */
function getFloatingButtonWindowSize() {
  const cfg = config.refreshConfig();
  const sizePx = Math.round(50 * (cfg.floatingButton.sizePercent / 100));
  
  const base = Math.max(40, sizePx);
  const actionBtnSize = Math.round(Math.max(36, base * 0.7));
  const ringThickness = actionBtnSize;
  const placementRadius = Math.round(Math.max(55, base * 1.05));
  const ringRadius = placementRadius + Math.round(ringThickness / 2);
  const windowSize = Math.round(Math.max(200, ringRadius * 2 + 40));
  
  return { width: windowSize, height: windowSize };
}

/*
 * 将窗口位置约束在屏幕工作区内。
 *
 * 工作区 = 屏幕去掉任务栏后的可用区域。
 * 允许"透明扩展区"（窗口比按钮大，按钮居中）越出屏幕边界，
 * 从而让视觉核心的按钮能贴近屏幕边缘。
 *
 * paddingX/paddingY = 透明扩展区宽度的一半。
 * 通过增大 minX/minY 和减小 maxX/maxY 的容差来实现。
 */
function clampBoundsToWorkArea(bounds, workArea) {
  if (!workArea) return bounds;
  
  const cfg = config.refreshConfig();
  const sizePx = Math.round(50 * (cfg.floatingButton.sizePercent / 100));
  
  const paddingX = (bounds.width - sizePx) / 2;
  const paddingY = (bounds.height - sizePx) / 2;
  
  const minX = workArea.x - paddingX;
  const minY = workArea.y - paddingY;
  const maxX = workArea.x + workArea.width - bounds.width + paddingX;
  const maxY = workArea.y + workArea.height - bounds.height + paddingY;
  
  return {
    ...bounds,
    x: Math.max(minX, Math.min(bounds.x, maxX)),
    y: Math.max(minY, Math.min(bounds.y, maxY))
  };
}

/*
 * 以窗口中心为锚点调整窗口尺寸。
 * 展开/收缩时窗口从中心向外均匀扩展或向内均匀收缩，避免窗口"跳动"。
 * 调整后自动 clamp 到工作区。
 */
function resizeFloatingButtonWindow({ width, height }) {
  if (!floatingButtonWindow || floatingButtonWindow.isDestroyed()) return;

  const current = floatingButtonWindow.getBounds();
  const centerX = current.x + current.width / 2;
  const centerY = current.y + current.height / 2;
  const targetWidth = Math.max(72, Math.round(width));
  const targetHeight = Math.max(72, Math.round(height));
  const nextBounds = {
    x: Math.round(centerX - targetWidth / 2),
    y: Math.round(centerY - targetHeight / 2),
    width: targetWidth,
    height: targetHeight
  };

  const display = screen.getDisplayNearestPoint({ x: Math.round(centerX), y: Math.round(centerY) });
  const clamped = clampBoundsToWorkArea(nextBounds, display && display.workArea ? display.workArea : null);
  floatingButtonWindow.setBounds(clamped, true);
}

/*
 * 切换悬浮窗口的展开/收缩状态。
 *
 * 重构（2026-07-18 setShape 方案）：
 *   窗口大小始终固定为展开态，不再缩放。
 *   expanded 仅用于状态追踪，窗口形状由像素级穿透 + setIgnoreMouseEvents 控制。
 */
function setFloatingButtonExpanded(payload) {
  floatingExpanded = Boolean(payload && payload.expanded);
}

/*
 * 获取悬浮按钮窗口的当前屏幕位置（实时）。
 * 配置面板“悬浮按钮”Tab 的位置输入实时显示此值（可直接编辑）。
 * 返回 { x, y }；窗口不存在时返回 null。
 */
function getFloatingButtonPosition() {
  if (!floatingButtonWindow || floatingButtonWindow.isDestroyed()) return null;
  const bounds = floatingButtonWindow.getBounds();
  return { x: bounds.x, y: bounds.y };
}


// ============================================================================
//  3. 通用窗口 opacity 动画
//
//  win.setOpacity() 调用 Windows SetLayeredWindowAttributes，OS 级别。
//  D3D9 下 JS/CSS 层级的 opacity 动画不被透明窗口合成器逐帧执行，
//  因此使用 setInterval + setOpacity 替代。
// ============================================================================

function animateWindowOpacity(win, fromOpacity, toOpacity, durationMs, signal) {
  return new Promise((resolve) => {
    if (!win || win.isDestroyed()) { resolve(); return; }
    if (signal && signal.aborted) { resolve(); return; }

    const onAbort = () => { clearInterval(timer); resolve(); };
    if (signal) signal.addEventListener('abort', onAbort, { once: true });

    const start = Date.now();
    const delta = toOpacity - fromOpacity;
    win.setOpacity(fromOpacity);

    const timer = setInterval(() => {
      if (!win || win.isDestroyed()) {
        clearInterval(timer);
        if (signal) signal.removeEventListener('abort', onAbort);
        resolve();
        return;
      }
      if (signal && signal.aborted) {
        clearInterval(timer);
        resolve();
        return;
      }
      const elapsed = Date.now() - start;
      const t = Math.min(1, elapsed / durationMs);
      win.setOpacity(fromOpacity + delta * t);
      if (t >= 1) {
        clearInterval(timer);
        if (signal) signal.removeEventListener('abort', onAbort);
        resolve();
      }
    }, 16);
  });
}


// ============================================================================
//  4. 悬浮按钮淡入淡出
//
//  使用 _floatingFadeCtrl（AbortController）防止并发动画冲突。
// ============================================================================

let _floatingFadeCtrl = null;

function _cancelFloatingFade() {
  if (_floatingFadeCtrl) {
    _floatingFadeCtrl.abort();
    _floatingFadeCtrl = null;
  }
}

async function fadeOutFloatingButtonWindow() {
  if (!floatingButtonWindow || floatingButtonWindow.isDestroyed()) return;
  if (!floatingButtonWindow.isVisible()) return;

  console.log('[windows] 悬浮按钮淡出');

  _cancelFloatingFade();
  const ctrl = new AbortController();
  _floatingFadeCtrl = ctrl;

  /* setOpacity 1→0 渐变（400ms），完成后 hide + 重置 opacity 为 1 */
  const currentOpacity = floatingButtonWindow.getOpacity();
  await animateWindowOpacity(
    floatingButtonWindow,
    Number.isFinite(currentOpacity) ? currentOpacity : 1,
    0,
    FLOATING_WINDOW_FADE_MS,
    ctrl.signal
  );

  if (ctrl.signal.aborted) return;

  if (floatingButtonWindow && !floatingButtonWindow.isDestroyed()) {
    floatingButtonWindow.hide();
    floatingButtonWindow.setOpacity(1);
  }
  if (_floatingFadeCtrl === ctrl) _floatingFadeCtrl = null;
}

async function fadeInFloatingButtonWindow() {
  if (!floatingButtonWindow || floatingButtonWindow.isDestroyed()) return;

  console.log('[windows] 悬浮按钮淡入');

  _cancelFloatingFade();
  const ctrl = new AbortController();
  _floatingFadeCtrl = ctrl;

  /*
   * setOpacity(0) → showInactive() → setOpacity 0→1 渐变（400ms）。
   * showInactive() 明确告诉 Windows 不激活此窗口，避免从全屏结果窗
   * 切回时 Windows 将焦点转移到 toolbar 窗口导致"半激活僵死"
   * （可见但点击无响应，须点其他窗口才能恢复）。
   */
  floatingButtonWindow.setOpacity(0);
  floatingButtonWindow.showInactive();
  await animateWindowOpacity(floatingButtonWindow, 0, 1, FLOATING_WINDOW_FADE_MS, ctrl.signal);

  if (_floatingFadeCtrl === ctrl) _floatingFadeCtrl = null;
}


// ============================================================================
//  5. 悬浮按钮窗口 —— 创建与看门狗
// ============================================================================

/*
 * 创建透明无边框的悬浮按钮窗口。
 *
 * 窗口特性：
 *   - 无边框（frame: false）、透明背景（transparent: true）
 *   - 始终置顶（alwaysOnTop: true，级别 'screen-saver' 覆盖全屏应用）
 *   - 不在任务栏显示（skipTaskbar: true）
 *   - 不显示在 Alt+Tab 切换列表中（type: 'toolbar'）
 *   - 所有平台允许获取焦点（focusable: true），避免从全屏结果窗
 *     切回时 toolbar 窗口陷入"半激活"僵死（点击无响应）。
 *     配合 showInactive() 使用：窗口能接收点击，但显示时不抢焦点。
 *
 * 启动时尝试恢复上次保存的位置（来自 config.yml 的 position.x/y）。
 * 如果无保存位置，系统自动选择默认位置。
 *
 * hide 事件处理：
 *   非退出/非临时隐藏时自动恢复显示（防御意外隐藏）。
 * closed 事件处理：
 *   非退出/非临时隐藏时自动重建窗口（防御意外销毁）。
 */
function createFloatingButtonWindow() {
  if (floatingButtonWindow && !floatingButtonWindow.isDestroyed()) {
    return floatingButtonWindow;
  }

  const cfg = config.refreshConfig();
  const baseSize = getFloatingButtonWindowSize();

  /*
   * Vulkan 后端已知问题：首个透明悬浮窗口渲染为黑块，重建后的窗口正常
   * （实测“应用配置触发重建后即恢复”）。因此本次运行的首次 Vulkan 窗口
   * 采用：隐藏创建 → 加载完成后自动销毁 → 由 closed 事件重建（第二个窗口
   * 为全新实例，正常显示），用户全程看不到黑块。
   */
  const isVulkan = (cfg.admin?.renderingBackend || 'd3d9') === 'vulkan';
  const doVulkanRecreate = isVulkan && !vulkanFirstWindowDone;
  if (doVulkanRecreate) vulkanFirstWindowDone = true;

  /* 位置恢复判断：显式排除 null/undefined（Number(null) === 0 会误判为已保存） */
  const px = cfg.floatingButton.position.x;
  const py = cfg.floatingButton.position.y;
  const hasSavedX = px !== null && px !== undefined && Number.isFinite(Number(px));
  const hasSavedY = py !== null && py !== undefined && Number.isFinite(Number(py));

  const windowOptions = {
    width: baseSize.width,
    height: baseSize.height,
    frame: false,
    resizable: false,
    minimizable: false,
    maximizable: false,
    hasShadow: false,
    transparent: true,
    alwaysOnTop: cfg.floatingButton.alwaysOnTop,
    skipTaskbar: isDebugMode ? false : !cfg.floatingButton.showInTaskbar,
    type: isDebugMode ? undefined : 'toolbar',
    focusable: true,
    /* Vulkan 首窗口：隐藏加载（用户看不到黑块），加载完成后销毁重建 */
    show: !doVulkanRecreate,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      autoplayPolicy: 'no-user-gesture-required'
    }
  };

  /* 如果有保存的位置，恢复 */
  if (hasSavedX && hasSavedY) {
    windowOptions.x = Math.round(Number(cfg.floatingButton.position.x));
    windowOptions.y = Math.round(Number(cfg.floatingButton.position.y));
  }

  const win = new BrowserWindow(windowOptions);
  floatingButtonWindow = win;
  console.log('[windows] 悬浮按钮窗口已创建', JSON.stringify({ width: baseSize.width, height: baseSize.height, hasSaved: hasSavedX && hasSavedY }));

  /* 增强置顶：screen-saver 级别可覆盖全屏应用 */
  if (cfg.floatingButton.alwaysOnTop) {
    win.setAlwaysOnTop(true, 'screen-saver');
  }
  /*
   * 所有虚拟桌面可见。
   * 浮窗作为工具覆盖层，应在所有桌面可用，避免 hide/show 时
   * Windows 将窗口激活到其"归属桌面"导致闪烁切屏。
   */
  if (typeof win.setVisibleOnAllWorkspaces === 'function') {
    win.setVisibleOnAllWorkspaces(true, { visibleOnFullScreen: true });
  }

  win.setMenuBarVisibility(false);

  /* 加载页面：开发模式走 Vite dev server，生产模式走本地文件 */
  if (process.env.VITE_DEV_SERVER_URL) {
    win.loadURL(process.env.VITE_DEV_SERVER_URL);
  } else {
    win.loadFile(path.join(__dirname, '../dist/index.html'));
  }

  /*
   * Vulkan 黑帧处理：首窗口隐藏加载完成后自动销毁，由 closed 事件重建
   * （第二个窗口为全新实例，正常显示；与“应用配置重建后正常”等效）。
   */
  if (doVulkanRecreate) {
    win.__vulkanPendingDestroy = true;
    win.webContents.once('did-finish-load', () => {
      setTimeout(() => {
        if (!win.isDestroyed() && !isQuitting && !isFloatingHiddenForPickCount) {
          console.log('[windows] Vulkan 首窗口加载完成，销毁重建（规避黑帧）');
          win.destroy();
        }
      }, 80);
    });
  }

  /* 禁止右键菜单（悬浮按钮不需要） */
  win.webContents.on('context-menu', (event) => event.preventDefault());

  /* hide 事件：意外隐藏时自动恢复 */
  win.on('hide', () => {
    if (isQuitting || isFloatingHiddenForPickCount) return;
    setTimeout(() => {
      if (!floatingButtonWindow || floatingButtonWindow.isDestroyed()) return;
      if (isQuitting || isFloatingHiddenForPickCount) return;
      if (!floatingButtonWindow.isVisible()) {
        floatingButtonWindow.showInactive();
      }
    }, 0);
  });

  /* closed 事件：意外销毁时自动重建 */
  win.on('closed', () => {
    floatingButtonWindow = null;
    if (!isQuitting && !isFloatingHiddenForPickCount) {
      setTimeout(() => {
        if (!isQuitting && !isFloatingHiddenForPickCount) {
          createFloatingButtonWindow();
        }
      }, 60);
    }
  });

  return win;
}

/*
 * 看门狗（Watchdog）：每 450ms 检查悬浮窗是否存活。
 *
 * 即使 hide/closed 事件处理了大部分意外情况，仍可能存在极端场景
 * （如渲染进程崩溃后窗口对象变为 null 但未触发 closed 事件）。
 * 看门狗作为最后防线，持续监控并自动修复。
 */
function startFloatingWindowWatchdog() {
  if (floatingWindowWatchdog) {
    clearInterval(floatingWindowWatchdog);
    floatingWindowWatchdog = null;
  }

  floatingWindowWatchdog = setInterval(() => {
    if (isQuitting || isFloatingHiddenForPickCount) return;

    if (!floatingButtonWindow || floatingButtonWindow.isDestroyed()) {
      console.log('[windows] 看门狗：悬浮按钮窗口已销毁，重建');
      createFloatingButtonWindow();
      return;
    }

    /* Vulkan 首窗口待销毁重建期间，不强制显示（避免黑块外露） */
    if (floatingButtonWindow.__vulkanPendingDestroy) return;

    if (!floatingButtonWindow.isVisible()) {
      console.log('[windows] 看门狗：悬浮按钮不可见，恢复显示');
      floatingButtonWindow.showInactive();
    }
  }, 450);
}

function stopFloatingWindowWatchdog() {
  if (floatingWindowWatchdog) {
    clearInterval(floatingWindowWatchdog);
    floatingWindowWatchdog = null;
  }
}


// ============================================================================
//  6. 抽取结果窗口 —— 持久化实例，hide/show 控制
// ============================================================================

/*
 * 关闭（隐藏）抽取结果窗口。
 *
 * 不销毁窗口（UIAccess 兼容性要求），仅 hide()。
 * blur() → hide() → setTimeout(show floating) 确保全屏窗口的
 * 焦点和 z-order 被 Windows 完全清理后再显示悬浮按钮，
 * 避免悬浮按钮进入半激活僵死状态（可见但点击无响应）。
 */
function closePickResultWindow() {
  console.log('[windows] 关闭结果窗口');
  if (!pickResultWindow || pickResultWindow.isDestroyed()) {
    currentPickResults = [];
    activePickResultToken = 0;
    isFloatingHiddenForPickCount = false;
    setTimeout(() => fadeInFloatingButtonWindow(), 0);
    return;
  }

  if (pickResultWindow.isVisible()) {
    /*
     * 先 blur 释放焦点，再 hide。
     * 全屏 alwaysOnTop 窗口可能残留激活状态，
     * 直接 hide 后 showInactive 悬浮按钮可能无法正常接收点击。
     */
    pickResultWindow.blur();
    pickResultWindow.hide();
  }
  
  currentPickResults = [];
  activePickResultToken = 0;
  isFloatingHiddenForPickCount = false;

  /*
   * setTimeout 确保 hide() 的窗口消息被 Windows 完全处理后再
   * 显示悬浮按钮，避免 z-order / 激活状态竞争。
   */
  setTimeout(() => {
    fadeInFloatingButtonWindow();
  }, 0);
}

/*
 * 预创建抽取结果窗口实例（启动时调用）。
 *
 * 窗口为全屏透明无边框，覆盖整个屏幕。
 * 创建后保持隐藏，等待 openPickResultWindow 触发显示。
 * 整个生命周期只创建一次，后续复用。
 */
function createPickResultWindowInstance() {
  if (pickResultWindow && !pickResultWindow.isDestroyed()) return;

  const win = new BrowserWindow({
    show: false,
    frame: false,
    transparent: true,
    fullscreen: true,
    resizable: false,
    minimizable: false,
    maximizable: false,
    movable: false,
    alwaysOnTop: true,
    skipTaskbar: isDebugMode ? false : !config.refreshConfig().floatingButton.showInTaskbar,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      autoplayPolicy: 'no-user-gesture-required'
    }
  });

  pickResultWindow = win;
  isPickResultWindowReady = false;
  win.setMenuBarVisibility(false);

  /* 浮窗已设为所有桌面可见，结果窗同样处理避免切屏 */
  if (typeof win.setVisibleOnAllWorkspaces === 'function') {
    win.setVisibleOnAllWorkspaces(true, { visibleOnFullScreen: true });
  }

  if (process.env.VITE_DEV_SERVER_URL) {
    win.loadURL(`${process.env.VITE_DEV_SERVER_URL}#/pick-result`);
  } else {
    win.loadURL(`file://${path.join(__dirname, '../dist/index.html')}#/pick-result`);
  }

  win.once('ready-to-show', () => { isPickResultWindowReady = true; });

  win.on('closed', () => {
    pickResultWindow = null;
    isPickResultWindowReady = false;
    currentPickResults = [];
    activePickResultToken = 0;
  });
}

/*
 * 展示抽取结果。
 *
 * 流程：
 *   1. 缓存结果数据和 token
 *   2. 确保窗口实例存在
 *   3. 通过 IPC 向渲染进程发送 pick-result:open 事件（含结果和 token）
 *   4. 显示窗口
 *   5. 隐藏悬浮按钮（渐隐动画）
 *
 * Token 机制：
 *   每次抽取分配一个新的 token（自增），渲染进程用 token 判断
 *   收到的 open 事件是否过期（防止快速连续抽取时的竞态）。
 */
function openPickResultWindow(results) {
  currentPickResults = Array.isArray(results) ? results : [];
  pickResultToken += 1;
  activePickResultToken = pickResultToken;
  createPickResultWindowInstance();

  if (!pickResultWindow || pickResultWindow.isDestroyed()) return;

  const openResultWindow = () => {
    if (!pickResultWindow || pickResultWindow.isDestroyed()) return;
    pickResultWindow.webContents.send('pick-result:open', {
      token: activePickResultToken,
      results: currentPickResults
    });
    pickResultWindow.show();
    pickResultWindow.focus();
  };

  if (isPickResultWindowReady) {
    openResultWindow();
  } else {
    pickResultWindow.once('ready-to-show', openResultWindow);
  }

  isFloatingHiddenForPickCount = true;
  fadeOutFloatingButtonWindow();
  console.log('[windows] 结果窗口已显示, token=' + activePickResultToken + ', 人数=' + currentPickResults.length);
}


// ============================================================================
//  7. 悬浮按钮拖拽
//
//  拖拽涉及三个 IPC 通道 + 渲染进程 PointerEvent：
//    floating-button:drag-start  → handleDragStart  → 记录窗口初始位置
//    floating-button:drag-move   → handleDragMove   → 移动窗口
//    floating-button:drag-end    → handleDragEnd    → 清理拖拽状态
// ============================================================================

function getDragSession(eventId) {
  return dragSessions.get(eventId);
}

function handleDragStart(event) {
  const win = BrowserWindow.fromWebContents(event.sender);
  if (!win) return;

  const bounds = win.getBounds();
  /* 保存拖拽起点：窗口的初始位置和尺寸 */
  dragSessions.set(event.sender.id, {
    startWinX: bounds.x,
    startWinY: bounds.y,
    width: bounds.width,
    height: bounds.height
  });
}

function handleDragMove(event, payload) {
  const win = BrowserWindow.fromWebContents(event.sender);
  const session = getDragSession(event.sender.id);
  if (!win || !session || !payload) return;

  const dx = Number(payload.dx);
  const dy = Number(payload.dy);
  if (Number.isNaN(dx) || Number.isNaN(dy)) return;

  /* 新位置 = 初始位置 + 累积偏移量 */
  win.setBounds({
    x: Math.round(session.startWinX + dx),
    y: Math.round(session.startWinY + dy),
    width: session.width,
    height: session.height
  });
}

function handleDragEnd(event) {
  dragSessions.delete(event.sender.id);
  console.log('[windows] 拖拽结束');
}

/*
 * 切换鼠标穿透模式。
 *
 * ignore=true  → 鼠标事件穿透窗口（点击到下层应用）
 * ignore=false → 窗口正常捕获鼠标事件
 *
 * { forward: true } 表示穿透的鼠标事件转发给下层窗口（而非直接丢弃）。
 *
 * 调用方：ipc.js 的 floating-button:set-ignore-mouse 通道。
 */
function setIgnoreMouseEvents(event, ignore) {
  const win = BrowserWindow.fromWebContents(event.sender);
  if (win && !win.isDestroyed()) {
    win.setIgnoreMouseEvents(ignore, { forward: true });
  }
}

function setFloatingButtonShape(rects) {
  if (!floatingButtonWindow || floatingButtonWindow.isDestroyed()) return;
  if (!Array.isArray(rects)) return;
  floatingButtonWindow.setShape(rects);
}


// ============================================================================
//  8. 按权重随机抽取算法（核心业务逻辑）
//
//  支持两种模式：
//    允许重复（allowRepeatDraw=true）：
//      每次抽取独立，同一个人可能被多次抽到。
//      使用累积权重法：所有权重求和，生成 [0, total) 随机数，
//      遍历池子减去权重直到 ≤0。
//
//    禁止重复（allowRepeatDraw=false）：
//      同一个人在一轮中最多被抽到一次。
//      权重 > 0 的学生使用逆变换采样（-ln(random)/weight）。
//      权重 = 0 的学生在正权重池不足时随机补充（Fisher-Yates 洗牌）。
//
//  权重增强：
//    允许重复模式下，实际权重 = weight^gamma（gamma=1.5），
//    放大高权重与低权重的差异，使"偏爱"效果更明显。
// ============================================================================

function pickStudentsByWeight(count) {
  const cfg = config.refreshConfig();
  const rawList = Array.isArray(cfg.studentList) ? cfg.studentList : [];

  /* 清洗名单：去掉空姓名、负权重归零；letterColor 缺省为 rainbow（彩） */
  const pool = rawList
    .map((s) => ({
      name: String(s.name || '').trim(),
      weight: Math.max(0, Number(s.weight) || 0),
      letterColor: ['rainbow', 'gold', 'blue'].includes(s.letterColor) ? s.letterColor : 'rainbow'
    }))
    .filter((s) => s.name);

  if (pool.length === 0 || count <= 0) return [];

  const targetCount = Math.max(0, count);
  const picked = [];
  const allowRepeatDraw = Boolean(cfg.allowRepeatDraw);

  // --------------------------------------------------------------------------
  //  模式 A：允许重复抽取
  // --------------------------------------------------------------------------
  if (allowRepeatDraw) {
    /* 对权重做指数增强（weight^1.5），扩大差异 */
    const weightedPool = pool.map((s) => ({
      name: s.name,
      letterColor: s.letterColor,
      weight: Math.pow(s.weight, WEIGHT_BOOST_GAMMA)
    }));
    const totalWeight = weightedPool.reduce((sum, s) => sum + s.weight, 0);

    /* 抽取 targetCount 次 */
    for (let i = 0; i < targetCount; i++) {
      let pickIndex = -1;
      if (totalWeight > 0) {
        /* 生成 [0, totalWeight) 随机数，依次减去权重找落点 */
        let roll = Math.random() * totalWeight;
        for (let j = 0; j < weightedPool.length; j++) {
          roll -= weightedPool[j].weight;
          if (roll <= 0) { pickIndex = j; break; }
        }
      }
      /* 兜底：所有权重为 0 时均匀随机选一个 */
      if (pickIndex < 0) {
        pickIndex = Math.floor(Math.random() * weightedPool.length);
      }
      picked.push({ name: weightedPool[pickIndex].name, letterColor: weightedPool[pickIndex].letterColor });
    }
    return picked;
  }

  // --------------------------------------------------------------------------
  //  模式 B：禁止重复抽取
  // --------------------------------------------------------------------------
  const positivePool = pool.filter((s) => s.weight > 0);
  const zeroPool = pool.filter((s) => s.weight <= 0);
  const hasPositiveWeight = positivePool.length > 0;

  /* 正权重池：使用逆变换采样（每个学生生成 -ln(rand)/weight 作为排序键） */
  if (hasPositiveWeight) {
    const keyed = positivePool.map((s) => ({
      name: s.name,
      letterColor: s.letterColor,
      key: -Math.log(Math.random()) / s.weight
    }));
    /* 按 key 升序排列，key 最小的优先被抽到 */
    keyed.sort((a, b) => a.key - b.key);
    const limit = Math.min(targetCount, keyed.length);
    for (let i = 0; i < limit; i++) {
      picked.push({ name: keyed[i].name, letterColor: keyed[i].letterColor });
    }
  }

  /* 零权重池：正权重池人数不足时，随机补充 */
  if (picked.length < targetCount && zeroPool.length > 0) {
    const remaining = zeroPool.slice();
    /* Fisher-Yates 洗牌（打乱顺序以随机选择） */
    for (let i = remaining.length - 1; i > 0; i--) {
      const j = Math.floor(Math.random() * (i + 1));
      [remaining[i], remaining[j]] = [remaining[j], remaining[i]];
    }
    const fillCount = Math.min(targetCount - picked.length, remaining.length);
    for (let i = 0; i < fillCount; i++) {
      picked.push({ name: remaining[i].name, letterColor: remaining[i].letterColor });
    }
  }

  return picked;
}


// ============================================================================
//  9. 配置面板窗口
// ============================================================================

function createConfigPanelWindow() {
  if (configPanelWindow && !configPanelWindow.isDestroyed()) {
    configPanelWindow.show();
    configPanelWindow.focus();
    configPanelWindow.webContents.send('config-panel:refresh');
    return configPanelWindow;
  }

  const win = new BrowserWindow({
    width: 980,
    height: 680,
    resizable: false,
    minimizable: true,
    maximizable: false,
    frame: true,
    transparent: false,
    show: false,
    skipTaskbar: false,
    icon: path.join(__dirname, '../dist/image/app.ico'),
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false
    }
  });

  configPanelWindow = win;
  win.setMenuBarVisibility(false);

  /*
   * 外部链接拦截 —— 防止 target="_blank" 弹出新的 Electron 窗口。
   * 配置面板的"关于"板块含有多个外部超链接（GitHub、蔚蓝档案官网等），
   * Electron 默认会为 target="_blank" 创建新 BrowserWindow，
   * 通过此 handler 拦截并转发到系统默认浏览器。
   */
  win.webContents.setWindowOpenHandler(({ url }) => {
    shell.openExternal(url);
    return { action: 'deny' };
  });

  win.once('ready-to-show', () => {
    win.show();
    win.focus();
  });

  if (process.env.VITE_DEV_SERVER_URL) {
    win.loadURL(`${process.env.VITE_DEV_SERVER_URL}#/config-panel`);
  } else {
    win.loadURL(`file://${path.join(__dirname, '../dist/index.html')}#/config-panel`);
  }

  win.on('closed', () => {
    configPanelWindow = null;
    isConfigPanelOpen = false;
  });

  return win;
}

function openConfigPanelWindow() {
  isConfigPanelOpen = true;
  createConfigPanelWindow();
}

function closeConfigPanelWindow(saved) {
  isConfigPanelOpen = false;

  if (saved) {
    refreshFloatingButtonWindow();
  }

  if (configPanelWindow && !configPanelWindow.isDestroyed()) {
    configPanelWindow.close();
  }
  configPanelWindow = null;
}


// ============================================================================
//  导出
// ============================================================================
module.exports = {
  closeConfigPanelWindow,
  closePickResultWindow,
  createConfigPanelWindow,
  createFloatingButtonWindow,
  createPickResultWindowInstance,
  fadeInFloatingButtonWindow,
  getConfigPanelWindow,
  getCurrentPickResults,
  getFloatingButtonPosition,
  getFloatingButtonWindow,
  getPickResultWindow,
  handleDragEnd,
  handleDragMove,
  handleDragStart,
  openConfigPanelWindow,
  openPickResultWindow,
  pickStudentsByWeight,
  refreshFloatingButtonWindow,
  setFloatingButtonExpanded,
  setFloatingButtonShape,
  setDebugMode,
  setIgnoreMouseEvents,
  setQuitting,
  startFloatingWindowWatchdog,
  stopFloatingWindowWatchdog
};
