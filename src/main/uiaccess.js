/*
================================================================================
技术文档：src/main/uiaccess.js
职责：RunUIAccess（shc0743/RunUIAccess）的进程内集成封装。

核心思路（RunUIAccess README · 集成到其他应用程序）：
  - uiaccess.dll 由 build/prepare-uiaccess.js 以 base64 内联进主进程包，
    运行时在本模块从内嵌数据解出到用户数据目录（Windows LoadLibrary 需要物理文件）；
  - 使用 koffi（基于 N-API 的动态 FFI，Electron 主进程可直接使用，无需 rebuild）
    在【进程内】直接调用 DLL 导出函数，不再通过 rundll32 子进程：
      StartUIAccessProcess(appName, cmdLine, flag, pPid, dwSession)
          —— 启动 UIAccess 进程（README：最常用）
      IsUIAccess()                   —— 当前进程是否以 UIAccess 权限运行
      IsProcessElevated(hProcess)    —— 进程是否以管理员权限运行
  - 按 README 要求：集成版不会自动提权，权限判断由调用方完成
    （admin.js 的 isProcessElevated / 主进程启动流程）。

依赖说明：
  - koffi 为运行时依赖（写入 package.json dependencies），
    electron-builder 会自动解包其原生 .node 二进制。
================================================================================
*/
const { app } = require('electron');
const fs = require('fs');
const path = require('path');

/* 内联的 uiaccess.dll（base64，由 build/prepare-uiaccess.js 生成） */
const UIACCESS_DLL_B64 = require('./uiaccess-inline');

/* koffi 绑定缓存（单例） */
let _lib = null;

/*
 * 将内嵌的 uiaccess.dll 解出到用户数据目录。
 *
 * 解出位置：%LocalAppData%\BlueRandom\uiaccess.dll（可写目录）。
 * 幂等：已存在且字节数一致则直接复用；否则覆盖写入。
 * 返回 dll 的物理路径；失败返回 null。
 */
function getDllPath() {
  try {
    const extractPath = path.join(app.getPath('userData'), 'uiaccess.dll');
    const buf = Buffer.from(UIACCESS_DLL_B64, 'base64');
    if (!fs.existsSync(extractPath) || fs.statSync(extractPath).size !== buf.length) {
      fs.writeFileSync(extractPath, buf);
      console.log('[uiaccess] uiaccess.dll 已从内嵌数据解出: ' + extractPath);
    }
    return extractPath;
  } catch (error) {
    console.error('[uiaccess] uiaccess.dll 提取失败:', error.message);
    return null;
  }
}

/*
 * 按需加载 uiaccess.dll 并绑定导出函数（缓存单例）。
 *
 * 延迟加载 koffi：即使 koffi 缺失/加载失败，也不影响应用其余功能。
 * 返回 { StartUIAccessProcess, IsUIAccess, IsProcessElevated } 或 null。
 */
function loadLib() {
  if (_lib) return _lib;
  try {
    const koffi = require('koffi');
    const dllPath = getDllPath();
    if (!dllPath) return null;

    const lib = koffi.load(dllPath);
    _lib = {
      StartUIAccessProcess: lib.func(
        'bool __stdcall StartUIAccessProcess(str16 appName, str16 cmdLine, uint32_t flag, uint32_t * pPid, uint32_t dwSession)'
      ),
      IsUIAccess: lib.func('bool __stdcall IsUIAccess()'),
      IsProcessElevated: lib.func('bool __stdcall IsProcessElevated(void * hProcess)')
    };
    console.log('[uiaccess] uiaccess.dll 已加载，导出函数绑定完成');
    return _lib;
  } catch (error) {
    console.error('[uiaccess] uiaccess.dll 加载失败:', error.message);
    return null;
  }
}

/* 当前进程是否以 UIAccess 权限运行（失败时保守返回 false） */
function isUiAccess() {
  try {
    const lib = loadLib();
    return lib ? Boolean(lib.IsUIAccess()) : false;
  } catch (error) {
    console.error('[uiaccess] IsUIAccess 调用失败:', error.message);
    return false;
  }
}

/*
 * 当前进程是否以管理员权限运行。
 *
 * 返回 true/false；加载或调用失败时返回 null，调用方（admin.js 的
 * isProcessElevated）此时保守返回 false（已无 PowerShell 回退）。
 */
function isProcessElevated() {
  try {
    const lib = loadLib();
    if (!lib) return null;
    const koffi = require('koffi');
    /* GetCurrentProcess() 返回当前进程伪句柄，可直接用于令牌查询 */
    const GetCurrentProcess = koffi.load('kernel32.dll').func('void * __stdcall GetCurrentProcess()');
    return Boolean(lib.IsProcessElevated(GetCurrentProcess()));
  } catch (error) {
    console.error('[uiaccess] IsProcessElevated 调用失败:', error.message);
    return null;
  }
}

/*
 * 以 UIAccess 权限启动目标进程（README · StartUIAccessProcess 导出函数）。
 *
 * 参数：
 *   appName — 目标可执行文件绝对路径（同 CreateProcess lpApplicationName）
 *   cmdLine — 完整命令行（含引号，如 "C:\...\app.exe" --debug）
 *   flag    — 创建标志，默认 0（CREATE_NEW_CONSOLE 等，无需时传 0）
 *
 * 说明：pPid 传 NULL（无需接收 PID）；dwSession 取当前交互会话 id
 *      （WTSGetActiveConsoleSessionId），确保启动到用户桌面会话。
 *
 * 返回：{ ok, message, detail? }
 */
function startUiAccessProcess({ appName, cmdLine, flag = 0 }) {
  try {
    const lib = loadLib();
    if (!lib) {
      return { ok: false, message: '无法加载 uiaccess.dll，请检查构建产物。' };
    }

    const koffi = require('koffi');
    const k32 = koffi.load('kernel32.dll');
    const WTSGetActiveConsoleSessionId = k32.func('uint32_t __stdcall WTSGetActiveConsoleSessionId()');
    const GetLastError = k32.func('uint32_t __stdcall GetLastError()');

    const sessionId = WTSGetActiveConsoleSessionId();
    /* pPid 传 null：本应用不需要接收新进程 PID */
    const ok = lib.StartUIAccessProcess(appName, cmdLine, flag, null, sessionId);

    if (!ok) {
      const code = GetLastError();
      console.error(`[uiaccess] StartUIAccessProcess 失败, GetLastError=${code}`);
      return {
        ok: false,
        message: 'UIAccess 启动失败。',
        detail: `StartUIAccessProcess 返回 FALSE, GetLastError=${code}`
      };
    }

    console.log('[uiaccess] StartUIAccessProcess 已发起, appName=' + appName);
    return { ok: true, message: '已请求 UIAccess 权限，即将重新启动。' };
  } catch (error) {
    console.error('[uiaccess] StartUIAccessProcess 调用异常:', error.message);
    return { ok: false, message: 'UIAccess 启动失败。', detail: String(error.message) };
  }
}

module.exports = {
  getDllPath,
  isProcessElevated,
  isUiAccess,
  loadLib,
  startUiAccessProcess
};
