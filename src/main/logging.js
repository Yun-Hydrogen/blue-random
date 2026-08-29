/*
================================================================================
技术文档：src/main/logging.js
职责：应用日志的统一收集、文件持久化与分发。

================================================================================
架构概述
================================================================================
  日志来源有两个：
    1. 主进程：通过劫持 console.log/info/warn/error 自动捕获
    2. 渲染进程：通过 IPC 通道 renderer:log 主动上报

  日志同时写入两处：
    A. 内存环形缓冲区（logBuffer，最多 600 条）
    B. 磁盘日志文件（%LocalAppData%\BlueRandom\log.txt）

  磁盘日志的生命周期：
    1. 启动时清空 log.txt（initLogFile）
    2. 每条日志追加写入（pushLog → appendFile，互斥锁保护）
    3. 配置面板通过 IPC 拉取文件内容（getLogs）
    4. 日志文件仅保留当前运行时的内容，下次启动清空

  竞态保护设计：
    ┌──────────────────────────────────────────────────────────┐
    │  writeQueue（Promise 链）                                  │
    │                                                          │
    │  pushLog("info", "A")  →  writeQueue = writeQueue        │
    │                              .then(() => appendFile(A))  │
    │  pushLog("warn", "B")  →  writeQueue = writeQueue        │
    │                              .then(() => appendFile(B))  │
    │  getLogs()             →  await writeQueue               │
    │                              .then(() => readFile())     │
    │                                                          │
    │  关键：所有文件操作串行化，保证写入顺序一致，               │
    │        读取操作等待所有待处理写入完成后再执行。             │
    └──────────────────────────────────────────────────────────┘

================================================================================
核心函数一览
================================================================================
  函数                       | 作用
  ───────────────────────────┼──────────────────────────────────────────────
  initLogFile()              | 启动时清空/创建 log.txt
  pushLog(level, text)       | 写入内存缓冲区 + 追加到磁盘日志
  getLogs(maxLines)          | 从磁盘读取最近 N 条日志（返回 Promise）
  attachConsoleLogger()      | 劫持 console，自动捕获主进程日志
  registerRendererLogIpc()   | 注册 IPC 通道，接收渲染进程日志
================================================================================
*/
const fs = require('fs');
const path = require('path');
const { app } = require('electron');

/* ---- 日志文件路径（惰性求值，首次 pushLog 时确定）---- */
let _logFilePath = null;
function getLogFilePath() {
  if (!_logFilePath) {
    _logFilePath = path.join(app.getPath('userData'), 'log.txt');
  }
  return _logFilePath;
}

/*
 * initLogFile() —— 清空日志文件（每次启动时调用）
 *
 * 在 app.whenReady() 之后调用（因为需要 app.getPath('userData') 可用）。
 * 写入空字符串截断文件，确保本次运行只看到本轮日志。
 *
 * 为什么清空而非追加：
 *   日志是调试工具，旧日志容易干扰排查。每次启动重新开始，
 *   日志文件只反映"本次运行"的状态。文件大小可控，不会无限增长。
 */
function initLogFile() {
  const p = getLogFilePath();
  const dir = path.dirname(p);
  if (!fs.existsSync(dir)) {
    fs.mkdirSync(dir, { recursive: true });
  }
  fs.writeFileSync(p, '', 'utf8');
}

/*
 * 关键操作信息标记（日志简化）。
 * info 级日志仅保留命中的“关键操作”，其余 info 一律丢弃；
 * warn / error 不受限制，始终记录。
 */
const KEY_INFO_MARKERS = [
  '应用启动',
  '进行了一次抽奖',
  '抽中的元素',
  '保存配置请求',
  '配置已保存',
  '重置配置',
  '配置加载成功',
  '管理员提权',
  'UIAccess',
  'uiaccess',
  '计划任务',
  '发现新版本',
  '已是最新版本',
  '检查更新失败',
  '开始检查更新'
];

/* 判断一条 info 日志是否为“关键操作” */
function isKeyInfo(text) {
  return KEY_INFO_MARKERS.some((marker) => String(text).includes(marker));
}

/* ---- 内存环形缓冲区（保留，供未来可能的内存查询使用）---- */
const LOG_BUFFER_LIMIT = 600;
const logBuffer = [];

/*
 * writeQueue —— 文件写入互斥锁
 *
 * 这是一个 Promise 链。每次文件写入操作都通过 .then() 追加到链尾，
 * 保证前一个写入完成前不会开始下一个写入。
 *
 * 为什么需要互斥锁：
 *   fs.appendFile 是异步的。如果不加锁，并发调用 pushLog 可能导致
 *   日志行交错写入（如 "line1\nli" + "ne2\n" → "line1\nline2\n" 乱序）。
 *
 * 为什么不用 fs.appendFileSync：
 *   同步 I/O 会阻塞主进程的事件循环。日志写入频率不高（非每帧），
 *   互斥锁方案既不影响性能，也保证写入顺序。
 */
let writeQueue = Promise.resolve();

/*
 * pushLog(level, text) —— 写入一条日志到内存缓冲区 + 磁盘文件。
 *
 * 参数：
 *   level — 日志级别字符串（'info' | 'warn' | 'error'）
 *   text  — 日志正文（任意类型，会转为字符串）
 *
 * 行为：
 *   1. 生成唯一 ID（时间戳 + 4 位随机 hex）
 *   2. 生成 ISO 8601 时间戳
 *   3. 追加到内存 logBuffer（环形，600 条上限）
 *   4. 追加到磁盘 log.txt（JSON 行，互斥锁串行化）
 *
 * 磁盘格式：每行一条 JSON
 *   {"id":"1719552000000-a3f2c1","level":"info","text":"应用启动","time":"2026-06-28T12:00:00.000Z"}
 */
function pushLog(level, text) {
  const time = new Date().toISOString();
  const entry = {
    id: `${Date.now()}-${Math.random().toString(16).slice(2)}`,
    level,
    text: String(text),
    time
  };

  /* 日志简化：warn/error 始终记录；info 仅记录“关键操作” */
  if (level === 'info' && !isKeyInfo(entry.text)) {
    return;
  }

  /* ---- 内存缓冲区 ---- */
  logBuffer.push(entry);
  if (logBuffer.length > LOG_BUFFER_LIMIT) {
    logBuffer.splice(0, logBuffer.length - LOG_BUFFER_LIMIT);
  }

  /* ---- 磁盘追加（互斥锁保护）---- */
  const line = JSON.stringify(entry) + '\n';
  writeQueue = writeQueue.then(() => {
    return new Promise((resolve) => {
      fs.appendFile(getLogFilePath(), line, 'utf8', (err) => {
        if (err) {
          /*
           * 文件写入失败：使用原始 console.error 记录（避免递归）。
           * 这种情况极其罕见——通常是磁盘满或权限不足。
           * 不抛出异常，因为日志系统本身不应该导致应用崩溃。
           */
          const origError = console.error.bind ? console.error.bind(console) : console.error;
          origError('[logging] 日志写入磁盘失败:', err.message);
        }
        resolve();
      });
    });
  });
}

/*
 * getLogs(maxLines) —— 从磁盘读取最近 N 条日志。
 *
 * 参数：
 *   maxLines — 最多读取的行数，默认 500
 *
 * 返回值：Promise<Array<logEntry>>
 *   日志条目数组，按时间倒序（最新的在前），最多 maxLines 条。
 *   如果文件不存在或读取失败，返回空数组 []。
 *
 * 竞态保护：
 *   读取前先 await writeQueue，确保所有待处理的写入已完成。
 *   这样读取到的内容一定包含之前所有 pushLog 写入的数据。
 */
function getLogs(maxLines = 500) {
  return writeQueue.then(() => {
    return new Promise((resolve) => {
      fs.readFile(getLogFilePath(), 'utf8', (err, data) => {
        if (err) {
          /* 文件不存在或无法读取 → 返回空数组 */
          return resolve([]);
        }
        /* 按行分割，过滤空行，取最后 maxLines 行 */
        const lines = data.split('\n').filter(line => line.trim());
        const recent = lines.slice(-maxLines);
        /* 每行 JSON 解析，解析失败的行丢弃 */
        const entries = [];
        for (const line of recent) {
          try {
            entries.push(JSON.parse(line));
          } catch (_) {
            /* 损坏的日志行静默丢弃 */
          }
        }
        /* 反转：最新的在前（符合日志查看习惯） */
        entries.reverse();
        resolve(entries);
      });
    });
  });
}

/*
 * attachConsoleLogger() —— 劫持全局 console 对象，捕获所有主进程日志。
 *
 * 这是整个日志系统工作的关键。它替换了 console.log / info / warn / error，
 * 使开发者正常使用 console 的同时，所有输出自动进入日志系统。
 *
 * 工作原理：
 *   1. 保存原始的 console[method] 引用
 *   2. 用新函数替换它：
 *      a. 将参数转为字符串
 *      b. 调用 pushLog 写入缓冲区和磁盘
 *      c. 调用原始 console 方法，保持终端输出
 *
 * 参数序列化：
 *   字符串 → 直接拼接
 *   对象   → JSON.stringify() 序列化
 *   其他   → String() 强制转换
 *
 * 特殊处理：
 *   console.log 的日志级别映射为 'info'（因为 'log' 不是标准级别名）
 *
 * 调用位置：main.js 启动阶段，在 app.whenReady() 之前调用。
 *
 * 警告：
 *   - 不要在 pushLog 或原始 console 调用中再次触发 console 调用，
 *     会导致无限递归（虽然当前代码不会，但修改时需注意）
 *   - JSON.stringify 可能对循环引用对象抛出 TypeError，已被 try/catch 包裹
 */
function attachConsoleLogger() {
  ['log', 'info', 'warn', 'error'].forEach((method) => {
    const original = console[method].bind(console);

    console[method] = (...args) => {
      const text = args.map(arg => {
        if (typeof arg === 'string') return arg;
        try {
          return JSON.stringify(arg);
        } catch (_error) {
          return String(arg);
        }
      }).join(' ');

      pushLog(method === 'log' ? 'info' : method, text);
      original(...args);
    };
  });
}

/*
 * registerRendererLogIpc(ipcMain) —— 注册渲染进程日志上报通道。
 *
 * 渲染进程通过 window.logApi.send(level, text) 上报日志，
 * 经 preload.js → contextBridge → IPC → 本函数处理。
 *
 * IPC 通道：renderer:log（单向通知，由渲染进程 send，主进程 on 接收）
 *
 * 参数校验：
 *   - payload 必须是非空对象
 *   - payload.text 必须是字符串（忽略非字符串日志）
 *   - payload.level 默认 'info'
 */
function registerRendererLogIpc(ipcMain) {
  ipcMain.on('renderer:log', (_event, payload) => {
    if (!payload || typeof payload.text !== 'string') return;
    const level = typeof payload.level === 'string' ? payload.level : 'info';
    pushLog(level, payload.text);
  });
}

/*
 * registerGetLogsIpc(ipcMain) —— 注册日志查询 IPC 通道。
 *
 * 配置面板 TabLogs 通过此通道拉取磁盘日志。
 *
 * IPC 通道：config-panel:get-logs（请求-响应，invoke/handle 模式）
 *
 * 参数：
 *   maxLines — 可选，最多返回的行数（默认 500）
 *
 * 返回值：日志条目数组（时间倒序，最新在前）
 */
function registerGetLogsIpc(ipcMain) {
  ipcMain.handle('config-panel:get-logs', async (_event, maxLines) => {
    return getLogs(typeof maxLines === 'number' ? maxLines : 500);
  });
}

// ============================================================================
//  导出 —— 供 main.js / ipc.js 使用
// ============================================================================
module.exports = {
  initLogFile,
  attachConsoleLogger,
  pushLog,
  getLogs,
  registerRendererLogIpc,
  registerGetLogsIpc
};
