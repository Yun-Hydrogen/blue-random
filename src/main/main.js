/*
================================================================================
技术文档：src/main/main.js
职责：Electron 主进程的启动引导（Bootstrap）—— 整个应用的入口。

================================================================================
模块导入说明
================================================================================
  require('electron')      → Electron 框架：app（生命周期）、BrowserWindow（窗口）、
                             ipcMain（进程通信）、shell（系统外壳）、Notification（通知）
  require('fs')            → 文件系统，检查 uiaccess.dll 是否存在、读取 config.yml
  require('child_process') → 执行 chcp 命令设置终端编码
  require('js-yaml')       → YAML 解析器，启动前读取配置判断穿透策略
  require('./admin')       → Windows 权限模块
  require('./config')      → 配置读写模块
  require('./customs')     → 自定义资源模块（customs/ 目录）
  require('./ipc')         → IPC 通道注册模块
  require('./logging')     → 日志收集模块
  require('./tray')        → 系统托盘模块
  require('./update')      → 更新检查模块
  require('./windows')     → 窗口管理模块

================================================================================
启动流程（按时间顺序）
================================================================================
  1. 设置命令行参数（音频自动播放）
  2. 初始化用户数据目录 → 读取 config.yml → 根据 renderingBackend /
     disableDirectComposition / disableHardwareAcceleration 设置 Chromium 标志
  3. 初始化日志系统
  4. 注册全局异常处理器
  5. 注册第 1 组 IPC 通道（悬浮按钮 + 抽取功能）
  6. app.whenReady() 触发：
     a. 检查 UIAccess 配置 → 如需启动则通过 StartUIAccessProcess 重启并退出
     b. 检查管理员配置 → 如需提权则通过 RunAs 重启并退出
     c. 创建系统托盘（右键菜单：配置 / 重启 / 退出）
     d. 注册第 2 组 IPC 通道（配置面板专用）
     e. 异步检查更新（有新版本弹出系统通知）
     f. 预创建窗口（悬浮按钮 + 抽取结果）并启动看门狗
     g. 绑定 macOS activate 事件（Dock 点击时重建悬浮窗）
  7. 绑定 before-quit → 保存状态
  8. 绑定 window-all-closed → 驻留托盘不退出

================================================================================
维护建议
================================================================================
  - 本文件只做"编排"：决定启动顺序和模块间的协作关系
  - 不要在 main.js 中写业务逻辑 —— 业务逻辑应拆分到 src/main/*.js
  - 新增启动步骤时，在 app.whenReady() 中按依赖顺序添加
  - 修改启动流程后务必测试"首次启动"和"重启"两种路径

================================================================================
更新记录
================================================================================
  2026-08-29  新增自定义资源模块接入：
              - require('./customs')，启动时调用 customs.ensureInitialized()
                （迁移旧版内嵌图标到 customs/、校验 customIconId）
  2026-08-29  渲染后端调整：
              - 移除 OpenGL（use-angle gl 分支）
              - Vulkan 透明悬浮按钮黑帧：根因为首次创建时 GPU/DWM 合成未就绪
                （应用配置触发重建后即正常）；由 windows.js 的“首次隐藏 + 自动
                reload 后再显示”处理（见 createFloatingButtonWindow）
================================================================================
*/
const { app, BrowserWindow, ipcMain } = require('electron');
const fs = require('fs');
const { execSync } = require('child_process');

/*
 * Windows 终端编码修复。
 *
 * 部分 Windows 系统的命令行默认使用 GBK（代码页 936），
 * 导致 console 输出的中文显示为乱码。chcp 65001 将当前终端
 * 切换为 UTF-8 编码（代码页 65001），确保中文日志正常显示。
 *
 * try/catch 包裹：chcp 命令可能在极少数精简版 Windows 上不可用，
 * 失败时静默忽略，不影响应用启动。
 */
if (process.platform === 'win32') {
  try { execSync('chcp 65001', { stdio: 'ignore' }); } catch {}
}

/* ---- 加载各模块 ---- */
const admin = require('./admin');
const classes = require('./classes');
const customs = require('./customs');
const config = require('./config');
const ipc = require('./ipc');
const logging = require('./logging');
const tray = require('./tray');
const update = require('./update');
const windows = require('./windows');

// ============================================================================
//  启动前全局设置
// ============================================================================

/*
 * 调试模式判断。
 *
 * 满足任一条件即视为调试模式：
 *   - VITE_DEV_SERVER_URL 环境变量存在（npm run dev 启动的 Vite 开发服务器）
 *   - 命令行参数包含 -debug 或 --debug
 *
 * 调试模式下：
 *   - 配置面板窗口显示在任务栏（方便切换）
 *   - 配置面板窗口打开 DevTools
 *   - 悬浮按钮窗口不跳过任务栏
 */
const isDebugMode = !!process.env.VITE_DEV_SERVER_URL
  || process.argv.includes('-debug')
  || process.argv.includes('--debug');

/*
 * 允许音频自动播放（无需用户手势）。
 *
 * 默认情况下，Chromium 阻止网页在没有用户交互时自动播放音频。
 * 本应用的结果展示窗口需要在抽取后立即播放音效/BGM，
 * 因此通过命令行参数关闭此限制。
 *
 * 参数值 'no-user-gesture-required' 表示：
 *   AudioContext / <audio>.play() 无需用户先点击/触摸页面。
 */
app.commandLine.appendSwitch('autoplay-policy', 'no-user-gesture-required');

/*
 * 将 userData 目录重定向到 %LocalAppData%\BlueRandom。
 * 必须在读取配置文件之前调用。
 */
admin.configureUserDataPath();

/*
 * 渲染后端配置（全模式通用，需重启生效）：
 *
 *   renderingBackend（默认 d3d9）：
 *     D3D9 不依赖 DXGI/DirectComposition，使用自有呈现路径。
 *     UIAccess 下 D3D9 的 DWM 旧版兼容层不受限制，原生 GPU 呈现。
 *     可选值：d3d9 / vulkan。（OpenGL 已移除：性能与兼容性均差）
 *
 *   disableDirectComposition（默认 true）：
 *     与 renderingBackend 可组合测试。
 *
 *   disableHardwareAcceleration（默认 false）：
 *     启用时 app.disableHardwareAcceleration() 完全回退到 CPU 软件渲染。
 *
 *   首次启动无配置文件时使用默认值（d3d9 + disable-direct-composition）。
 *
 *   已知问题：Vulkan 下透明悬浮按钮首次创建可能渲染黑帧（GPU 合成未就绪），
 *   由 windows.js 的“首窗口隐藏 + 自动 reload 后再显示”处理（见 createFloatingButtonWindow）。
 */
try {
  const fs = require('fs');
  const path = require('path');
  const yaml = require('js-yaml');
  const cfgPath = path.join(app.getPath('userData'), 'config.yml');
  if (fs.existsSync(cfgPath)) {
    const raw = yaml.load(fs.readFileSync(cfgPath, 'utf8'));
    const backend = (raw?.admin?.renderingBackend || 'd3d9').toLowerCase();

    if (backend === 'vulkan') {
      app.commandLine.appendSwitch('use-angle', 'vulkan');
    } else {
      /* d3d9（默认） */
      app.commandLine.appendSwitch('use-angle', 'd3d9');
    }

    /* disableDirectComposition：默认 true，与 renderingBackend 可组合 */
    if (raw?.admin?.disableDirectComposition !== false) {
      app.commandLine.appendSwitch('disable-direct-composition');
    }

    /* disableHardwareAcceleration：默认 false，启用时全 CPU 软件渲染 */
    if (raw?.admin?.disableHardwareAcceleration === true) {
      app.disableHardwareAcceleration();
    }
  } else {
    /* 首次启动无配置文件，使用默认 d3d9 + disable-direct-composition */
    app.commandLine.appendSwitch('use-angle', 'd3d9');
    app.commandLine.appendSwitch('disable-direct-composition');
  }
} catch (_) { /* 配置文件不存在或格式错误，静默跳过 */ }
windows.setDebugMode(isDebugMode);

/* 劫持 console，使所有主进程日志进入统一缓冲 */
logging.attachConsoleLogger();
console.log('[main] 日志系统已就绪，isDebugMode:', isDebugMode);

/* 注册 IPC 通道，接收渲染进程上报的日志 */
logging.registerRendererLogIpc(ipcMain);

// ============================================================================
//  全局异常处理（最后防线）
//
//  捕获未被 try/catch 处理的异常和 Promise 拒绝。
//  应用不会崩溃退出，但会记录错误日志供排查。
//  理想情况下，所有业务代码应有自己的 try/catch，
//  这里的 handler 只作为"兜底"安全网。
// ============================================================================
process.on('uncaughtException', (error) => {
  console.error('Uncaught exception:', error);
});

process.on('unhandledRejection', (reason) => {
  console.error('Unhandled rejection:', reason);
});

/*
 * 注册第 1 组 IPC 通道（悬浮按钮 + 抽取功能）。
 *
 * 这些通道在应用启动时就注册，伴随整个生命周期。
 * 第 2 组（配置面板专用）在 configPanel 窗口创建后注册，
 * 避免通道命名冲突。
 */
ipc.registerIpcHandlers();


// ============================================================================
//  app.whenReady() —— 应用就绪后的主启动流程
//
//  Electron 的 app 模块在准备好后触发此 Promise。
//  在此之前不能创建窗口或使用大多数 Electron API。
//  这是实际业务逻辑的起点。
// ============================================================================
app.whenReady().then(() => {
  const startupConfig = config.refreshConfig();

  /*
   * 初始化日志文件。
   *
   * 每次启动清空 log.txt，确保日志只反映"本次运行"的内容。
   * 必须在 app.getPath('userData') 可用后调用（即 app.whenReady() 之后）。
   * 放在 refreshConfig() 之后，以便日志系统记录配置加载过程中的输出。
   */
  logging.initLogFile();

  /* 多班级初始化：迁移旧名单到默认班级、校验 activeClassId */
  classes.ensureInitialized();

  /* 自定义资源初始化：迁移旧版内嵌图标到 customs/、校验 customIconId */
  customs.ensureInitialized();

  console.log('[main] 应用启动', JSON.stringify({
    version: app.getVersion(),
    isAdmin: admin.isProcessElevated(),
    isUiAccess: admin.isUiAccessProcess(),
    backend: startupConfig.admin?.renderingBackend || 'd3d9',
    userData: app.getPath('userData')
  }));

  // --------------------------------------------------------------------------
  //  第 1 步：检查是否需要管理员提权重启
  //
  //  触发条件（全部满足）：
  //    1. 配置中 requireAdminOnLaunch = true
  //    2. 当前运行在 Windows 上
  //    3. 当前进程不是管理员（已是管理员则跳过）
  //
  //  如果满足条件：通过 PowerShell Start-Process -Verb RunAs 以管理员
  //  权限重启。用户会看到 UAC 弹窗，确认后新进程获得管理员权限。
  // --------------------------------------------------------------------------
  if (startupConfig.admin && startupConfig.admin.requireAdminOnLaunch
    && admin.IS_WINDOWS && !admin.isProcessElevated()) {
    console.log('[main] requireAdminOnLaunch=on，非管理员，请求提权');
    const result = admin.requestAdminRelaunch();
    if (result.ok) {
      console.log('[main] 管理员提权已发起，退出当前进程');
      windows.setQuitting(true);
      app.exit(0);
      return;
    }
    console.error('[main] 管理员提权失败:', result.detail || result.message);
  }

  // --------------------------------------------------------------------------
  //  第 2 步：检查是否需要 UIAccess 提权重启
  //
  //  触发条件（全部满足）：
  //    1. 配置中 uiAccessEnabled = true
  //    2. 当前运行在 Windows 上
  //    3. 当前进程不是 UIAccess 进程（避免无限循环重启）
  //    4. 当前进程具有管理员权限（UIAccess 的前置条件，README 要求）
  //    5. uiaccess.dll 已内联进程序并成功解出到用户数据目录
  //
  //  如果满足条件：通过 uiaccess.dll 的 StartUIAccessProcess 导出函数
  //  （koffi 进程内调用，见 src/main/uiaccess.js）启动新进程，
  //  然后退出当前进程。新进程以 UIAccess 权限启动（状态由 IsUIAccess() 检测）。
  //  注：uiaccess.dll 已内联进程序（app.asar），此处由 getDefaultUiAccessDllPath()
  //  从内嵌数据解出到用户数据目录后供 LoadLibrary 加载。
  // --------------------------------------------------------------------------
  if (startupConfig.admin && startupConfig.admin.uiAccessEnabled
    && admin.IS_WINDOWS && !admin.isUiAccessProcess()) {
    if (admin.isProcessElevated()) {
      const dllPath = admin.getDefaultUiAccessDllPath();
      if (dllPath && fs.existsSync(dllPath)) {
        console.log('[main] uiAccessEnabled=on，发起 UIAccess 提权');
        const result = admin.requestUiAccessRelaunch();
        if (result.ok) {
          console.log('[main] UIAccess 提权已发起，退出当前进程');
          /* 新进程已启动，退出当前进程 */
          windows.setQuitting(true);
          app.exit(0);
          return;  // ← 注意：这里的 return 终止了 whenReady 回调的后续执行
        }
        console.error('[main] UIAccess 提权失败:', result.detail || result.message);
      } else {
        console.error('[main] UIAccess DLL 缺失:', dllPath);
      }
    } else {
      console.warn('[main] UIAccess 已开启但非管理员，跳过（需先通过"启动时管理员"提权）');
    }
  }

  // --------------------------------------------------------------------------
  //  第 3 步：以下代码仅在"无需重启"时执行
  //          即：权限已满足，或用户未启用提权功能
  // --------------------------------------------------------------------------

  /*
   * 创建系统托盘图标和右键菜单。
   *
   * 托盘菜单有两个选项：
   *   配置 → 打开原生配置面板（ConfigPanel.vue，通过 IPC 通信）
   *   退出 → 关闭应用（调用 app.quit()）
   *
   * 注意：托盘创建后应用不会在关闭所有窗口时退出（见 window-all-closed 事件），
   *        用户需通过托盘菜单显式退出。
   */
  console.log('[main] 正常启动：创建托盘');
  tray.createTray({
    onOpenConfig: () => windows.openConfigPanelWindow(),
    onRestart: () => {
      app.relaunch();
      app.exit(0);
    },
    onQuit: () => app.quit()
  });

  /*
   * 注册第 2 组 IPC 通道（配置面板专用）。
   *
   * 与第 1 组分开注册的原因：
   *   这些通道在 configPanel 窗口创建后才有意义。
   *   分开注册避免了在窗口不存在时收到无效请求。
   *   详见 ipc.js 中的 registerConfigPanelIpc()。
   */
  ipc.registerConfigPanelIpc();
  console.log('[main] 配置面板 IPC 已注册');

  /*
   * 异步检查更新（不阻塞主线程）。
   *
   * 调用 update.checkUpdateFromMain() 向 GitHub Releases API 请求最新版本。
   * 如果检测到新版本（status === 'update'）：
   *   弹出系统原生通知，点击通知可跳转到 GitHub Releases 下载页面。
   *
   * 通知使用 Electron 的 Notification API（Windows 10+ 使用系统通知中心）。
   * 如果系统不支持通知（如 Windows 7），跳过。
   *
   * .catch() 捕获网络错误，仅记录日志不影响启动。
   */
  update.checkUpdateFromMain().then(res => {
    if (res.ok && res.status === 'update') {
      const { Notification, shell } = require('electron');
      if (Notification.isSupported()) {
        const notification = new Notification({
          title: res.title || '发现新版本',
          body: '点击此处前往下载页面',
        });
        notification.on('click', () => {
          if (res.releaseUrl) shell.openExternal(res.releaseUrl);
        });
        notification.show();
      }
    }
  }).catch(err => {
    console.error('[main] 更新检查失败:', err);
  });

  /*
   * 预创建窗口 + 启动看门狗。
   *
   * 悬浮按钮窗口和抽取结果窗口在启动时预先创建（隐藏状态），
   * 用户首次点击悬浮按钮时无需等待窗口初始化，体验更流畅。
   *
   * 看门狗（watchdog）是一个定时器，持续检查悬浮窗口是否存活，
   * 如果意外销毁则自动重建 —— 确保悬浮按钮始终可用。
   * 详见 windows.js 中的 startFloatingWindowWatchdog()。
   */
  windows.createFloatingButtonWindow();
  windows.createPickResultWindowInstance();
  windows.startFloatingWindowWatchdog();
  console.log('[main] 窗口已预创建，看门狗已启动');



  /*
   * macOS 专属：Dock 图标点击时重建悬浮窗。
   *
   * 在 macOS 上关闭所有窗口后，点击 Dock 图标会触发 activate 事件。
   * 此时如果没有任何窗口存在，重建悬浮按钮窗口。
   *
   * 在 Windows/Linux 上此事件通常不会触发，但保留处理逻辑无害。
   */
  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) {
      windows.createFloatingButtonWindow();
    }
  });
});


// ============================================================================
//  应用退出相关事件
// ============================================================================

/*
 * before-quit：应用即将退出前的最后清理。
 *
 * 执行顺序：
 *   1. 标记退出状态（阻止看门狗重建窗口）
 *   2. 停止看门狗定时器
 *
 * 注：不再自动保存悬浮按钮位置 —— 配置仅在用户手动触发保存时落盘。
 */
app.on('before-quit', () => {
  console.log('[main] before-quit: 停止看门狗');
  windows.setQuitting(true);
  windows.stopFloatingWindowWatchdog();
});

/*
 * window-all-closed：所有窗口关闭时触发。
 *
 * 默认行为：Electron 在所有窗口关闭后自动退出应用。
 *
 * 本应用的策略：不退出，保持托盘图标驻留。
 * 用户需通过托盘菜单 → "退出" 显式退出应用。
 * 因此这个事件处理函数体为空（阻止默认退出行为）。
 */
app.on('window-all-closed', () => {
  /* 不执行 app.quit() —— 保持托盘驻留 */
});
