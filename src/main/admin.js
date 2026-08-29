/*
================================================================================
技术文档：src/main/admin.js
职责：Windows 权限能力封装 —— 管理员提升、UIAccess、开机计划任务。

================================================================================
模块导入说明
================================================================================
  require('electron')      → Electron 桌面框架，提供 app（应用生命周期）、
                             shell（系统外壳）等 API
  require('child_process') → Node.js 子进程模块，用于调用外部命令行程序
  require('fs')            → 文件系统模块，用于检查文件/目录是否存在
  require('path')          → 路径处理模块，用于拼接和解析文件路径

================================================================================
核心功能一览
================================================================================
  函数                         | 作用
  ─────────────────────────────┼──────────────────────────────────────────────
  configureUserDataPath()      | 将用户数据目录统一到 %LocalAppData%\BlueRandom
  isProcessElevated()          | 判断当前进程是否以管理员权限运行
  requestAdminRelaunch()       | 以管理员权限（UAC 弹窗）重新启动本应用
  requestUiAccessRelaunch()    | 通过 uiaccess.dll 的 StartUIAccessProcess 导出以 UIAccess 权限重启
  createAdminStartupTask()     | 创建 Windows 计划任务实现开机自启
  getDefaultExePath()          | 获取当前 .exe 文件的绝对路径
  getDefaultUiAccessDllPath()  | 返回内嵌 uiaccess.dll 解出后的可用路径（委托 uiaccess.js）
  ─────────────────────────────┴──────────────────────────────────────────────
  内部辅助函数：
  quoteForPowerShell()         | 对 PowerShell 字符串中的单引号做转义
  getPowerShellPath()          | 获取 powershell.exe 的绝对路径
  buildUiAccessCommandLine()   | 构建完整命令行（供 StartUIAccessProcess 的 cmdLine 使用）

================================================================================
关键概念
================================================================================
  UAC（用户账户控制）：
    Windows 安全机制。以管理员权限运行需要 UAC 弹窗确认。
    本文件通过 PowerShell Start-Process -Verb RunAs 触发 UAC 提权。

  UIAccess：
    比管理员更高级的窗口置顶层。允许应用窗口显示在系统 UI（如任务管理器、
    开始菜单）之上。需要：① 数字签名 ② 安装在受信任目录（Program Files）
    ③ 通过 RunUIAccess 的 uiaccess.dll（其 StartUIAccessProcess 导出函数）以
       UIAccess 权限启动（进程内 koffi 调用，见 src/main/uiaccess.js）。

  plan9（计划任务）：
    Windows Task Scheduler。本文件用 schtasks.exe 命令行工具创建
    "用户登录时运行"的启动任务，实现开机自启。

================================================================================
维护建议
================================================================================
  - 此文件仅处理权限/系统集成，不要混入窗口创建、IPC、业务逻辑
  - 新增系统级功能优先在本文件添加，保持 main.js 入口文件简洁
  - 修改 PowerShell 脚本时注意引号转义：PowerShell 单引号 '' 对应 JS '\'\''
  - 所有 spawnSync/execFileSync 操作均为同步阻塞，不要在主线程高频调用

================================================================================
更新记录
================================================================================
  2026-08-28  UIAccess 重启链路修复：
              - 移除 requestUiAccessRelaunch() 中的 app.isPackaged 检查
                （“仅支持正式版”限制源自旧 rundll32/外链 dll 方案，内联后已无必要，
                 开发模式 electron.exe 同样可被 StartUIAccessProcess 以 UIAccess 启动）
              - 修复开发模式 UIAccess 重启“找不到应用”：
                StartUIAccessProcess 经 SYSTEM 服务以 CreateProcessAsUserW 创建新进程，
                cwd 固定为 C:\WINDOWS\system32；把 argv[1] 相对应用入口（.）转成绝对路径，
                避免解析到 System32 导致 "Unable to find Electron app at ..."
  2026-08-28  configureUserDataPath() 增加 sessionData 重定向：
              - 将 Chromium 会话/缓存目录（Cache、GPUCache、Local Storage、Network 等）
                从散落在配置目录根部重定向到统一位置，配置目录保持干净
  2026-08-28  sessionData 目标调整为 userData/session 子目录：
              - 从系统临时目录（%TEMP%\BlueRandom\session）改为
                %LocalAppData%\BlueRandom\session：同一卷读写无差异，且不会被
                外部清理工具误删，缓存可长期复用
              - 新增“高级设置 → 清理缓存”入口（config-panel:clear-cache），
                可手动删除该目录释放空间
================================================================================
*/
const { app } = require('electron');
const { execFileSync, spawnSync } = require('child_process');
const fs = require('fs');
const path = require('path');
const uiaccess = require('./uiaccess');

// ============================================================================
//  模块级常量 —— 应用启动时确定，运行期间不变
// ============================================================================

/* 是否为 Windows 系统。所有权限功能仅 Windows 有效，macOS/Linux 直接跳过。 */
const IS_WINDOWS = process.platform === 'win32';

/* 计划任务的默认名称，显示在 Windows 任务计划程序中。 */
const ADMIN_TASK_DEFAULT_NAME = 'Blue Random (Admin)';

/* 用户数据文件夹名。config.yml、日志等存储在此目录下。 */
const USERDATA_DIR_NAME = 'BlueRandom';




// ============================================================================
//  1. configureUserDataPath() —— 统一用户数据目录
// ============================================================================

/*
 * 将 Electron 的 userData 目录从默认的 %AppData%\Roaming 重定向到
 * %LocalAppData%，避免 Roaming 目录的权限问题（部分企业环境会对
 * Roaming 做网络同步，可能阻止应用写入文件）。
 *
 * 结果路径（Windows 10/11）：
 *   C:\Users\<用户名>\AppData\Local\BlueRandom\
 *
 * 同时把 Chromium 的会话/缓存数据（Cache、Code Cache、GPUCache、
 * DawnGraphiteCache、DawnWebGPUCache、blob_storage、Local Storage、
 * Session Storage、Network、Shared Dictionary 等）通过 app.setPath('sessionData')
 * 一次性重定向到 userData 下的 session/ 子目录，避免这些可丢弃的缓存目录
 * 散落在配置文件夹根部污染视线。
 *
 * 重定向后配置目录结构：
 *   config.yml          持久化配置
 *   classes/            多班级数据（项目自身，必须保留）
 *   log.txt             日志
 *   uiaccess.dll        UIAccess 内联解出
 *   session/            Chromium 会话/缓存（可随时通过“高级设置 → 清理缓存”删除）
 *
 * 非 Windows 系统不做任何操作（macOS/Linux 的默认路径已足够）。
 *
 * 调用时机：app.whenReady() 之前，确保后续所有文件操作使用正确路径。
 */
function configureUserDataPath() {
  if (!IS_WINDOWS) {
    return;
  }
  /* app.getPath('appData') 返回 %AppData% 路径（通常是 Roaming） */
  const appData = app.getPath('appData');
  /* path.resolve(..., '..', 'Local') → 上溯一级目录再进入 Local */
  const localRoot = path.resolve(appData, '..', 'Local');
  const targetPath = path.join(localRoot, USERDATA_DIR_NAME);
  /* 重设 userData，此后 app.getPath('userData') 返回新路径 */
  app.setPath('userData', targetPath);

  /* 重定向 Chromium 会话/缓存数据到 userData/session（见函数头注释） */
  app.setPath('sessionData', path.join(targetPath, 'session'));
}


// ============================================================================
//  2. 内部辅助函数 —— PowerShell 字符串处理
// ============================================================================

/*
 * 对传入 PowerShell 脚本的字符串做转义。
 *
 * PowerShell 中单引号 ' 是字符串字面量定界符，字符串内的单引号需写成两个 ''。
 * 例如：It's fine  →  It''s fine
 *
 * 为什么不用双引号：PowerShell 双引号内会做变量展开（$var），
 * 文件路径中包含 $ 字符时可能导致意外行为。单引号字符串是纯字面量。
 */
function quoteForPowerShell(text) {
  return String(text).replace(/'/g, "''");
}

/*
 * 获取 PowerShell 可执行文件的完整路径。
 *
 * 优先使用 %SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe
 * （64 位系统上的标准位置）。若文件不存在（极端情况），回退到 PATH 中的
 * 'powershell'，由系统自行解析。
 *
 * 为什么写死路径而非依赖 PATH：部分 Windows 环境 PATH 可能不完整，
 * 或第三方软件修改了 PATH 导致调用到错误版本。
 */
function getPowerShellPath() {
  if (!IS_WINDOWS) return 'powershell';
  const root = process.env.SystemRoot || process.env.WINDIR || 'C:\\Windows';
  const psPath = path.join(root, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
  return fs.existsSync(psPath) ? psPath : 'powershell';
}



/*
 * 将程序路径和参数列表拼接为完整命令行字符串（StartUIAccessProcess 的 cmdLine）。
 *
 * 每个参数用双引号包裹，参数内的双引号需转义为 \"。
 *
 * 示例输入：
 *   exePath = C:\My App\app.exe
 *   args    = ['--debug']
 * 示例输出：
 *   "C:\My App\app.exe" "--debug"
 *
 * 注意：appName 传未加引号的 exePath（同 CreateProcess lpApplicationName），
 *       cmdLine 使用本函数输出（含引号），二者分别作为 StartUIAccessProcess 前两个参数。
 */
function buildUiAccessCommandLine(exePath, args) {
  const quote = (value) => `"${String(value).replace(/"/g, '\\"')}"`;
  const safeArgs = Array.isArray(args) ? args : [];
  return [quote(exePath), ...safeArgs.map(arg => quote(arg))].join(' ');
}


// ============================================================================
//  3. isProcessElevated() —— 判断当前是否以管理员身份运行
// ============================================================================

/*
 * 判断当前进程是否以管理员权限运行。
 *
 * 唯一依据：RunUIAccess uiaccess.dll 的 IsProcessElevated(hProcess) 导出
 * （内部按进程令牌判断），传入 GetCurrentProcess() 伪句柄即可，
 * 由 src/main/uiaccess.js（koffi）在进程内调用。
 * 已完全移除 PowerShell / .NET 旧方案。
 *
 * 返回值：true/false；DLL 不可用或调用失败时保守返回 false。
 */
function isProcessElevated() {
  if (!IS_WINDOWS) return false;
  try {
    return uiaccess.isProcessElevated() === true;
  } catch (_) {
    return false;
  }
}

/*
 * 判断当前进程是否以 UIAccess 权限运行。
 *
 * 唯一依据：uiaccess.dll 的 IsUIAccess() 导出（按进程令牌判断）。
 * 不使用命令行参数（--uiaccess）作为检测手段。
 */
function isUiAccessProcess() {
  try {
    return uiaccess.isUiAccess();
  } catch (_) {
    return false;
  }
}


// ============================================================================
//  4. requestAdminRelaunch() —— 管理员权限重启
// ============================================================================

/*
 * 以管理员权限重新启动当前应用。
 *
 * 原理：
 *   用 PowerShell 的 Start-Process -Verb RunAs 启动一个新进程。
 *   -Verb RunAs 会触发 Windows UAC 弹窗，用户确认后新进程获得管理员权限。
 *   旧进程在启动新进程后立即退出（app.exit(0)）。
 *
 * 调用流程（main.js 中的使用）：
 *   1. 用户点击"管理员重启"按钮
 *   2. 通过 IPC → main.js → requestAdminRelaunch()
 *   3. 本函数构造 PowerShell 命令并执行
 *   4. 返回 { ok: true } → main.js 调用 app.exit(0) 退出当前进程
 *   5. UAC 弹窗 → 用户确认 → 新进程以管理员权限启动
 *
 * 返回值：
 *   { ok: true,  message: '...' }  —— 提权命令已发送
 *   { ok: false, message: '...', detail: '...' } —— 失败（含错误详情）
 *
 * 注意：
 *   - 仅 Windows 可用，其他系统返回 ok:false
 *   - 新进程会继承当前进程的命令行参数（如 --debug）
 *   - 如果当前进程已经是管理员，新进程仍会弹 UAC（Windows 设计如此）
 *
 * 调试：
 *   如果提权失败，检查 detail 字段中的 PowerShell 错误输出。
 *   常见原因：用户点击了 UAC 弹窗的"否"、安全软件拦截、PowerShell 被禁用。
 */
function requestAdminRelaunch() {
  if (!IS_WINDOWS) {
    return { ok: false, message: '当前系统不支持管理员提升。' };
  }

  /* process.execPath = 当前 Node.js/Electron 可执行文件的完整路径 */
  const exePath = process.execPath;
  /* process.argv = 命令行参数数组，argv[0] 是 Electron，从 argv[1] 开始是应用参数 */
  const args = process.argv.slice(1);

  /* 将每个参数用单引号包裹并转义，供 PowerShell 数组使用 */
  const argList = args.length > 0
    ? args.map(arg => `'${quoteForPowerShell(arg)}'`).join(', ')
    : '';

  /* 构造 PowerShell 脚本：
   *   $exe   = 可执行文件路径
   *   $args  = 参数数组（可选）
   *   Start-Process -Verb RunAs → 以管理员权限启动 */
  const script = [
    `$exe = '${quoteForPowerShell(exePath)}'`,
    args.length > 0
      ? `$args = @(${argList}); Start-Process -FilePath $exe -ArgumentList $args -Verb RunAs`
      : 'Start-Process -FilePath $exe -Verb RunAs'
  ].join('; ');

  /* spawnSync：同步执行外部命令，等待完成后返回结果
   *   -NoProfile：不加载 PowerShell 用户配置（加快启动）
   *   -Command：执行指定的脚本字符串
   *   windowsHide：不弹出命令行黑窗口 */
  const result = spawnSync(getPowerShellPath(), ['-NoProfile', '-Command', script], {
    encoding: 'utf8',
    windowsHide: true
  });

  /* 检查执行结果：
   *   result.error  — 系统级错误（如找不到 exe）
   *   result.status — 退出码（0 = 成功，非 0 = 失败，null = 被信号终止） */
  if (result.error || result.status !== 0) {
    const detail = [result.error ? String(result.error) : '', result.stderr || '', result.stdout || '']
      .join('\n')
      .trim();
    console.error('[admin] 管理员提权失败:', detail || 'command failed');
    return { ok: false, message: '管理员权限请求失败或被取消。', detail: detail || 'command failed' };
  }

  console.log('[admin] 管理员提权命令已执行, exe=' + exePath);
  return { ok: true, message: '已请求管理员权限，即将重新启动。' };
}


// ============================================================================
//  5. 路径辅助函数
// ============================================================================

/* 返回当前应用程序 .exe 文件的绝对路径（如 C:\Program Files\BlueRandom\BlueRandom.exe）。 */
function getDefaultExePath() {
  return app.getPath('exe');
}

/*
 * 返回内嵌 uiaccess.dll 解出后的可用路径（实现见 src/main/uiaccess.js）。
 * 说明：uiaccess.dll 已“编译进程序”（app.asar 内的主进程包），
 * 运行时需物化到磁盘才能被 LoadLibrary/koffi 加载。
 */
function getDefaultUiAccessDllPath() {
  return uiaccess.getDllPath();
}


// ============================================================================
//  6. requestUiAccessRelaunch() —— UIAccess 权限重启
// ============================================================================

/*
 * 通过 rundll32 调用 uiaccess.dll，以 UIAccess 权限重新启动应用。
 *
 * 什么是 UIAccess：
 *   Windows 的一种特殊权限级别，允许应用程序窗口显示在系统 UI 元素
 *   （如任务管理器、开始菜单、UAC 弹窗）之上。普通"置顶"窗口无法覆盖
 *   这些系统 UI，UIAccess 可以。
 *
 * 使用条件（由本函数及调用方共同保证）：
 *   1. 必须是 Windows 系统
 *   2. uiaccess.dll 已内联进程序，并能从用户数据目录成功解出
 *   3. 应用程序必须经过数字签名（Windows 强制要求，正式版由打包流程保证）
 *
 * 调用方式（RunUIAccess README · 集成到其他应用程序）：
 *   进程内直接调用导出函数 StartUIAccessProcess(appName, cmdLine, flag, pPid, dwSession)，
 *   由 src/main/uiaccess.js（koffi）完成绑定，不再使用 rundll32 / PowerShell 子进程。
 *   按 README：集成版不会自动提权，本应用由主进程在调用前通过 isProcessElevated()
 *   判断（不满足则提示先开启"启动时管理员"）。
 *
 * 开发模式（npm run dev）下同样可用：exe 为 electron.exe，StartUIAccessProcess
 *   以 UIAccess 权限启动它并携带项目路径参数（不再受"仅正式版"限制——
 *   该限制源自旧的外链 dll/rundll32 方案，内联后已消除）。
 *
 * 返回值：{ ok, message, detail? }
 *
 * 调试：
 *   - "无法加载 uiaccess.dll"：检查 build/prepare-uiaccess.js 是否成功生成内联模块
 *   - UIAccess 不生效：检查数字签名是否有效（右键 exe → 属性 → 数字签名）
 */
function requestUiAccessRelaunch() {
  /* ---- 前置检查 ---- */
  if (!IS_WINDOWS) {
    return { ok: false, message: '当前系统不支持 UIAccess。' };
  }

  /* ---- 构建启动参数 ---- */
  const exePath = getDefaultExePath();

  /* 原样携带当前进程的命令行参数。
   * 注意：UIAccess 状态由 uiaccess.dll 的 IsUIAccess() 检测，
   * 不再通过 --uiaccess 命令行参数识别。
   *
   * 关键点：开发模式（electron .）下 argv[1] 是应用入口，可能是相对路径 '.',
   * 而 StartUIAccessProcess 通过 SYSTEM 服务以 CreateProcessAsUserW 创建新进程，
   * 其工作目录是 C:\WINDOWS\system32（lpCurrentDirectory 传 NULL），
   * 相对路径会解析到 System32，导致 "Unable to find Electron app at ..."。
   * 因此开发模式下必须把应用入口转成绝对路径（相对发起进程 cwd）。 */
  let args = process.argv.slice(1);
  if (process.defaultApp && args.length > 0
    && !path.isAbsolute(args[0]) && !args[0].startsWith('-')) {
    args = [path.resolve(process.cwd(), args[0]), ...args.slice(1)];
  }

  /* cmdLine 为完整命令行（buildUiAccessCommandLine 负责对路径/参数加引号），
   * 交给 uiaccess.js 进程内调用 StartUIAccessProcess。 */
  const cmdLine = buildUiAccessCommandLine(exePath, args);

  return uiaccess.startUiAccessProcess({ appName: exePath, cmdLine });
}


// ============================================================================
//  7. createAdminStartupTask() —— Windows 计划任务（开机自启）
// ============================================================================

/*
 * 创建或更新 Windows 计划任务，实现用户登录时自动启动应用。
 *
 * 原理：
 *   使用 Windows 内置命令行工具 schtasks.exe 创建计划任务。
 *   如果同名任务已存在，/F 参数强制覆盖（更新）。
 *
 * schtasks 参数说明：
 *   /Create     创建新任务
 *   /F          强制覆盖同名任务（无此参数则报错"已存在"）
 *   /RL HIGHEST 以最高可用权限运行（管理员权限）
 *   /SC ONLOGON 触发器：用户登录时运行
 *   /TN <name>  任务名称（显示在任务计划程序中）
 *   /TR <path>  要运行的程序路径
 *   /RU <user>  以哪个用户身份运行（可选，默认当前用户）
 *
 * 参数：
 *   taskName   — 任务名称（可选，默认 'Blue Random (Admin)'）
 *   exePath    — 要启动的 .exe 文件路径（必填，需校验存在性）
 *   runAsUser  — 运行身份用户名（可选，默认取当前登录用户名）
 *                注意：当前 IPC 调用方未传递此参数，实际始终使用当前用户
 *
 * 执行策略：
 *   - 如果当前进程已是管理员 → 直接执行 schtasks
 *   - 如果当前进程不是管理员 → 通过 PowerShell Start-Process -Verb RunAs
 *     以管理员权限执行 schtasks（会弹 UAC）
 *
 * 返回值：
 *   { ok: true,  message: '...' }
 *   { ok: false, message: '...', detail: '...' }
 *
 * 调试：
 *   - 在 PowerShell 中运行 Get-ScheduledTask 查看已创建的任务
 *   - 在 taskschd.msc（任务计划程序 GUI）中检查任务属性
 *   - 常见失败原因：杀毒软件拦截 schtasks、用户名包含特殊字符、exePath 无效
 */
function createAdminStartupTask({ taskName, exePath, runAsUser }) {
  if (!IS_WINDOWS) {
    return { ok: false, message: '仅支持 Windows 计划任务。' };
  }

  /* 校验 exe 路径有效性：非空 + 文件确实存在 */
  if (!exePath || !fs.existsSync(exePath)) {
    return { ok: false, message: '可执行文件路径无效或不存在。' };
  }

  /* 任务名称使用默认值兜底 */
  const safeTaskName = taskName || ADMIN_TASK_DEFAULT_NAME;
  /* 用户名：优先使用显式传入的，其次读取环境变量，最后留空 */
  const userName = runAsUser || process.env.USERNAME || '';

  /* 构建 schtasks 命令行参数数组 */
  const taskArgs = [
    '/Create',
    '/F',           /* 强制覆盖同名任务 */
    '/RL', 'HIGHEST', /* 以最高权限运行 */
    '/SC', 'ONLOGON', /* 触发器：登录时 */
    '/TN', safeTaskName,
    '/TR', `"${exePath}"`   /* 程序路径用双引号包裹以处理空格 */
  ];

  /* 如果获取到了用户名，指定以该用户身份运行 */
  if (userName) {
    taskArgs.push('/RU', userName);
  }

  try {
    if (isProcessElevated()) {
      /* 情况 A：当前已是管理员 → 直接调用 schtasks */
      execFileSync('schtasks', taskArgs, { stdio: 'ignore' });
    } else {
      /* 情况 B：当前不是管理员 → 通过 PowerShell 提权调用 schtasks
       *
       * 为什么不能直接用 spawnSync + RunAs：
       *   schtasks 本身不弹 UAC，必须由管理员进程调用。
       *   因此需要 PowerShell Start-Process -Verb RunAs 先提权。 */
      const psArgs = taskArgs.map(arg => `"${arg.replace(/"/g, '\\"')}"`).join(' ');
      const command = `Start-Process -FilePath 'schtasks.exe' -ArgumentList '${quoteForPowerShell(psArgs)}' -Verb RunAs -Wait`;
      execFileSync('powershell', ['-NoProfile', '-Command', command], { stdio: 'ignore' });
    }
    console.log('[admin] 计划任务创建成功, 名称=' + safeTaskName + ', 路径=' + exePath);
    return { ok: true, message: '计划任务已创建或更新。' };
  } catch (error) {
    console.error('[admin] 计划任务创建失败:', error.message || String(error));
    return { ok: false, message: '计划任务创建失败或被取消。', detail: String(error) };
  }
}


// ============================================================================
//  导出 —— 供其他模块（main.js, ipc.js, config.js）引用
// ============================================================================

module.exports = {
  ADMIN_TASK_DEFAULT_NAME,    /* 计划任务默认名，config.js 中 DEFAULT_CONFIG 引用 */
  IS_WINDOWS,                 /* 是否为 Windows，各处条件判断 */
  configureUserDataPath,
  createAdminStartupTask,
  getDefaultExePath,
  getDefaultUiAccessDllPath,
  isProcessElevated,
  isUiAccessProcess,
  requestAdminRelaunch,
  requestUiAccessRelaunch
};
