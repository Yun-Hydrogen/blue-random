/*
================================================================================
技术文档：src/main/ipc.js
职责：主进程 IPC（进程间通信）通道集中注册。

================================================================================
基础概念
================================================================================
  IPC（Inter-Process Communication）：
    Electron 应用分为"主进程"（Node.js）和"渲染进程"（Chromium 网页）。
    两者不能直接互相调用函数，需要通过 IPC 通道传递消息。

  ipcMain.handle(channel, handler)：
    主进程注册一个"请求-响应"通道。渲染进程用 invoke() 发送请求并等待返回值。
    类似 HTTP GET/POST —— 请求方发消息，处理方返回结果。

  ipcMain.on(channel, listener)：
    主进程注册一个"单向通知"通道。渲染进程用 send() 发送消息，不等待返回值。
    类似 UDP 或 fire-and-forget —— 发出后不管结果。

  命名规范：
    统一使用"模块名:动作"格式，例如：
      floating-button:drag-start      floating-picker:confirm
      pick-result:close               config-panel:save-config

================================================================================
模块导入（文件顶部集中引用，避免函数内部重复 require）
================================================================================
  require('electron')      → Electron 框架：ipcMain（IPC）、app（生命周期）、
                              dialog（系统对话框）
  require('./config')      → 配置读写模块
  require('./windows')     → 窗口管理模块
  require('./admin')       → Windows 权限模块
  require('./update')      → 更新检查模块
  require('fs')            → Node.js 文件系统  require('path')          → Node.js 路径处理

================================================================================
更新记录
================================================================================
  2026-08-28  新增 config-panel:clear-cache 通道：
              - 清理 Chromium 会话/缓存数据（userData/session 目录，可丢弃）
              - 返回 { ok, removed, failed, message }，占用中文件跳过并统计
  2026-08-29  新增自定义资源（customs）通道：
              - config-panel:save-custom（写 customs/*.yml，可选删除旧资源）
              - config-panel:get-custom / delete-custom
              - floating-button:get-config 按 customIconId 解析为 data URL
              - pick-result:get-config 按 bgmCustomId 解析为 bgmDataUrl/bgmDuration
  2026-08-29  上传改为内存暂存（staged，点击“应用”才落盘）：
              - 新增 config-panel:save-custom-staged（数据暂存主进程内存，不写盘）
              - save-config 将 staged 引用转正为 customs 文件、删除被替换/移除的
                旧资源、并清空剩余暂存；未应用的上传在关闭面板时舍弃
              - config-panel:close 清空暂存；get-custom 支持读取 staged 数据
  2026-08-29  抽取音效 customs 化：
              - save-config 处理 gachaSoundCustomId 的 staged 转正与旧资源删除
              - pick-result:get-config 解析 gachaSoundDataUrl 供结果窗口播放
  2026-08-29  新增配置面板密码保护（安全管理）通道：
              - config-panel:get-security-status（查询是否已开启，security.isEnabled()）
              - config-panel:verify-password（锁屏解锁 / 更改 / 关闭前验证）
              - config-panel:set-password（SHA256 写入 <配置根目录>/.SHA256）
              - config-panel:disable-password（需验证当前密码）
              - config-panel:reset-all-config（忘记密码：清除所有配置）
  2026-08-29  重置所有配置扩展为“几乎清理整个配置目录”：
              - config-panel:reset-config 复用 security.resetAllConfig()，除重置
                config.yml 外，同时删除 .SHA256（密码）、清空 classes/（班级）、
                customs/（自定义资源），并返回清理说明
================================================================================
*/

// ============================================================================
//  模块导入 —— 全部在此集中引用，避免函数体内部重复 require()
// ============================================================================
const { ipcMain, app, dialog } = require('electron');
const fs = require('fs');
const path = require('path');
const config = require('./config');
const classes = require('./classes');
const customs = require('./customs');
const security = require('./security');
const windows = require('./windows');
const admin = require('./admin');
const update = require('./update');
const logging = require('./logging');


// ============================================================================
//  第 1 组：悬浮按钮与抽取功能（应用启动时注册，伴随整个生命周期）
// ============================================================================

function registerIpcHandlers() {

  /*
   *  floating-button:get-config
   *  方向：渲染 → 主（请求-响应）
   *  用途：悬浮按钮窗口（Floating.vue）初始化时获取按钮外观配置。
   *  返回：floatingButton 对象（sizePercent, iconDataUrl, borderColor 等）。
   */
  ipcMain.handle('floating-button:get-config', () => {
    const cfg = config.refreshConfig();
    const fb = { ...cfg.floatingButton };
    /* 自定义图标存于 customs/，运行时解析为 data URL 供渲染层直接显示 */
    if (fb.customIconId) {
      const dataUrl = customs.getCustomDataUrl(fb.customIconId);
      fb.iconDataUrl = dataUrl || '';
    }
    return {
      ...fb,
      uiAccessEnabled: cfg.admin?.uiAccessEnabled || false
    };
  });

  /*
   *  floating-picker:get-config
   *  方向：渲染 → 主（请求-响应）
   *  用途：获取人数选择器配置（默认抽取人数）。
   *  返回：pickCountDialog 对象。
   */
  ipcMain.handle('floating-picker:get-config', () => {
    return config.refreshConfig().pickCountDialog;
  });

  /*
   *  floating-picker:confirm
   *  方向：渲染 → 主（单向通知）
   *  用途：用户确认抽取人数后，执行按权重随机抽取并打开结果窗口。
   *  参数：payload.count — 用户选择的抽取人数（1-10）。
   *  流程：
   *    1. 校验 count 范围 1-10
   *    2. 调用 windows.pickStudentsByWeight(count) 执行抽取
   *    3. 将结果传给 windows.openPickResultWindow() 展示
   */
  ipcMain.on('floating-picker:confirm', (_event, payload) => {
    const selectedCount = Math.round(Number(payload && payload.count)) || 1;
    const count = Math.min(10, Math.max(1, selectedCount));
    console.log(`进行了一次抽奖，人数为：${count}`);
    const pickedStudents = windows.pickStudentsByWeight(count);
    if (pickedStudents.length > 0) {
      console.log(`抽中的元素：${pickedStudents.map(s => s.name).join(', ')}`);
    }
    windows.openPickResultWindow(pickedStudents);
  });

  /*
   *  pick-result:get-results
   *  方向：渲染 → 主（请求-响应）
   *  用途：结果窗口打开时获取当前抽取结果列表。
   *  返回：学生姓名数组。
   */
  ipcMain.handle('pick-result:get-results', () => {
    return windows.getCurrentPickResults();
  });

  /*
   *  pick-result:get-config
   *  方向：渲染 → 主（请求-响应）
   *  用途：结果窗口获取音效/外观配置（音量、面板颜色、BGM 起始位置等）。
   *  返回：pickResultDialog 对象。
   */
  ipcMain.handle('pick-result:get-config', () => {
    const pickResult = { ...config.refreshConfig().pickResultDialog };
    /* 背景音乐存于 customs/，运行时解析为 data URL 供结果窗口播放，并附带时长 */
    if (pickResult.bgmCustomId) {
      const custom = customs.loadCustom(pickResult.bgmCustomId);
      if (custom && custom.base64) {
        pickResult.bgmDataUrl = `data:${custom.mimeType || 'audio/mpeg'};base64,${custom.base64}`;
        pickResult.bgmDuration = custom.duration || 0;
      }
    }
    /* 抽取音效存于 customs/，运行时解析为 data URL 供结果窗口播放 */
    if (pickResult.gachaSoundCustomId) {
      const custom = customs.loadCustom(pickResult.gachaSoundCustomId);
      if (custom && custom.base64) {
        pickResult.gachaSoundDataUrl = `data:${custom.mimeType || 'audio/wav'};base64,${custom.base64}`;
      }
    }
    return pickResult;
  });

  /*
   *  pick-result:close
   *  方向：渲染 → 主（单向通知）
   *  用途：结果窗口关闭时通知主进程隐藏窗口并恢复悬浮按钮。
   */
  ipcMain.on('pick-result:close', () => {
    windows.closePickResultWindow();
  });

  /*
   *  floating-button:drag-start / drag-move / drag-end
   *  方向：渲染 → 主（单向通知）
   *  用途：悬浮按钮拖拽事件。渲染进程计算偏移量，主进程移动 BrowserWindow。
   *
   *  为什么不在渲染进程直接移动窗口：
   *    BrowserWindow 的位置只能由主进程控制（Electron 安全模型）。
   *    渲染进程通过 IPC 把拖拽偏移量发给主进程，由主进程执行 setPosition()。
   */
  ipcMain.on('floating-button:drag-start', (event) => {
    windows.handleDragStart(event);
  });

  ipcMain.on('floating-button:drag-move', (event, payload) => {
    windows.handleDragMove(event, payload);
  });

  ipcMain.on('floating-button:drag-end', (event) => {
    windows.handleDragEnd(event);
  });

  /*
   *  floating-button:set-ignore-mouse
   *  方向：渲染 → 主（单向通知）
   *  用途：切换悬浮窗口的鼠标穿透模式。
   *  参数：ignore — true 时鼠标事件穿透窗口（点击到下层应用），
   *               false 时窗口正常捕获鼠标事件。
   *  场景：悬浮按钮空闲时穿透（不遮挡操作），hover/拖拽/选择器打开时捕获。
   */
  ipcMain.on('floating-button:set-ignore-mouse', (event, ignore) => {
    windows.setIgnoreMouseEvents(event, ignore);
  });

  /*
   *  floating-button:set-expanded
   *  方向：渲染 → 主（单向通知）
   *  用途：切换悬浮窗口的展开/收缩状态。
   *  参数：payload.expanded — true 展开（显示人数选择器环绕 UI），
   *                          false 收缩（仅显示按钮）。
   *        payload.size     — 展开时的窗口尺寸 { width, height }。
   */
  ipcMain.on('floating-button:set-expanded', (_event, payload) => {
    windows.setFloatingButtonExpanded(payload);
  });

  /*
   *  floating-button:set-shape
   *  方向：渲染 → 主（单向通知）
   *  用途：设置窗口命中区域（SetWindowRgn）。D3D9 下透明窗口
   *        不支持像素级穿透，setShape 是唯一穿透方案。
   *  参数：rects — [{ x, y, width, height }] 矩形数组。
   */
  ipcMain.on('floating-button:set-shape', (_event, rects) => {
    windows.setFloatingButtonShape(rects);
  });
}


// ============================================================================
//  第 2 组：配置面板（configPanel 窗口创建后动态注册，避免命名冲突）
//
//  为什么分两组注册：
//    main.js 中先调用 registerIpcHandlers()（第 1 组），
//    再创建 configPanel 窗口、然后调用 registerConfigPanelIpc()（第 2 组）。
//    分开注册确保配置面板 IPC 通道在对应窗口启动后才生效。
// ============================================================================

function registerConfigPanelIpc() {

  /*
   *  config-panel:get-config
   *  方向：渲染 → 主（请求-响应）
   *  用途：配置面板打开时获取完整配置对象。
   *  返回：整个 config 对象（studentList, floatingButton, pickResultDialog 等）。
   */
  ipcMain.handle('config-panel:get-config', () => {
    return config.refreshConfig();
  });

  /*
   *  安全相关通道（密码保护）
   *  config-panel:get-security-status  → 查询是否已开启密码保护
   *  config-panel:verify-password      → 验证密码（锁屏解锁 / 更改/关闭前验证）
   *  config-panel:set-password         → 设置/更改密码（SHA256 写入 .SHA256）
   *  config-panel:disable-password     → 关闭密码保护（需验证当前密码）
   *  config-panel:reset-all-config     → 忘记密码：清除所有配置
   */
  ipcMain.handle('config-panel:get-security-status', () => ({
    enabled: security.isEnabled()
  }));
  ipcMain.handle('config-panel:verify-password', (_event, payload) => {
    return security.verifyPassword(payload?.password);
  });
  ipcMain.handle('config-panel:set-password', (_event, payload) => {
    return security.setPassword(payload?.password);
  });
  ipcMain.handle('config-panel:disable-password', (_event, payload) => {
    return security.disablePassword(payload?.password);
  });
  ipcMain.handle('config-panel:reset-all-config', () => {
    return security.resetAllConfig();
  });

  /*
   *  config-panel:save-config
   *  方向：渲染 → 主（请求-响应）
   *  用途：用户在配置面板中点击"应用"后保存配置。
   *  参数：payload — Vue draft 对象的完整配置（未经归一化）。
   *  流程：
   *    1. config.normalizeConfig(payload) — 清洗数据（范围校验、默认值填充）
   *    2. config.saveConfig(normalized)  — 写入 config.yml
   *    3. windows.refreshFloatingButtonWindow() — 重建悬浮窗以应用新外观
   */
  ipcMain.handle('config-panel:save-config', (_event, payload) => {
    console.log('[ipc] 保存配置请求, 字段数=' + Object.keys(payload || {}).length);

    /* ---- 自定义资源：staged 转正 + 旧资源清理（上传的数据在“应用”时才落盘） ---- */
    const prevCfg = config.loadConfig();
    const prevIconId = prevCfg?.floatingButton?.customIconId || '';
    const prevBgmId = prevCfg?.pickResultDialog?.bgmCustomId || '';
    const prevGachaId = prevCfg?.pickResultDialog?.gachaSoundCustomId || '';
    const srcFb = (payload && payload.floatingButton) || {};
    const srcPick = (payload && payload.pickResultDialog) || {};

    /* 图标：staged 引用 → 落盘为真实 custom id；未变更的真实 id 保持不变 */
    let iconId = srcFb.customIconId || '';
    if (iconId.startsWith('staged_custom_')) {
      const promoted = customs.stagedPromote(iconId);
      iconId = promoted ? promoted.id : '';
    }
    /* BGM：同上 */
    let bgmId = srcPick.bgmCustomId || '';
    if (bgmId.startsWith('staged_custom_')) {
      const promoted = customs.stagedPromote(bgmId);
      bgmId = promoted ? promoted.id : '';
    }
    /* 抽取音效：同上 */
    let gachaId = srcPick.gachaSoundCustomId || '';
    if (gachaId.startsWith('staged_custom_')) {
      const promoted = customs.stagedPromote(gachaId);
      gachaId = promoted ? promoted.id : '';
    }
    /* 删除被替换/移除的旧资源（未变更则新旧相等，不删除） */
    if (prevIconId && prevIconId !== iconId) customs.deleteCustom(prevIconId);
    if (prevBgmId && prevBgmId !== bgmId) customs.deleteCustom(prevBgmId);
    if (prevGachaId && prevGachaId !== gachaId) customs.deleteCustom(prevGachaId);
    /* 应用完成后清空剩余暂存（未引用的 staged 全部舍弃） */
    customs.stagedClear();

    /* 组装新 payload 并归一化（此时 customIconId/bgmCustomId 已是真实 id 或空） */
    const nextPayload = {
      ...payload,
      floatingButton: { ...srcFb, customIconId: iconId },
      pickResultDialog: { ...srcPick, bgmCustomId: bgmId, gachaSoundCustomId: gachaId }
    };
    const normalized = config.normalizeConfig(nextPayload);

    /* 手动保存时：把当前名单/权重同步写入 activeClassId 对应的班级文件 */
    const activeId = normalized.activeClassId || classes.getActiveClassId();
    const existing = activeId ? classes.loadClass(activeId) : null;
    if (existing) {
      classes.saveClass({
        classId: activeId,
        name: existing.name,
        studentList: normalized.studentList || []
      });
    }

    config.saveConfig(normalized);
    windows.refreshFloatingButtonWindow();
    console.log('[ipc] 配置已保存，悬浮窗已刷新');
    return { ok: true };
  });

  /*
   * config-panel:get-floating-position
   * 方向：渲染 → 主（请求-响应）
   * 用途：获取悬浮按钮窗口的当前屏幕位置（实时）。
   * 返回：{ x, y } 或 null（悬浮窗不存在）。
   */
  ipcMain.handle('config-panel:get-floating-position', () => {
    return windows.getFloatingButtonPosition();
  });

  /*
   * config-panel:list-classes
   * 方向：渲染 → 主（请求-响应）
   * 用途：列出全部班级与当前班级 id（即“刷新”）。
   * 返回：{ activeClassId, classes: [{ classId, name, studentList }] }
   */
  ipcMain.handle('config-panel:list-classes', () => {
    return {
      activeClassId: classes.getActiveClassId(),
      classes: classes.listClasses()
    };
  });

  /*
   * config-panel:create-class
   * 方向：渲染 → 主（请求-响应）
   * 用途：创建一个新班级（空名单），立即落盘。
   * 参数：{ name }。返回：{ ok, class }。
   */
  ipcMain.handle('config-panel:create-class', (_event, payload) => {
    const cls = classes.createClass(String(payload?.name || '').trim());
    console.log('[ipc] 添加班级:', cls.classId, cls.name);
    return { ok: true, class: cls };
  });

  /*
   * config-panel:rename-class
   * 方向：渲染 → 主（请求-响应）
   * 用途：重命名班级，立即落盘。参数：{ classId, name }。
   */
  ipcMain.handle('config-panel:rename-class', (_event, payload) => {
    const ok = classes.renameClass(String(payload?.classId || ''), String(payload?.name || ''));
    console.log('[ipc] 重命名班级:', payload?.classId, ok);
    return { ok };
  });

  /*
   * config-panel:delete-class
   * 方向：渲染 → 主（请求-响应）
   * 用途：删除班级文件；若删除的是当前班级，回退到第一个剩余班级。
   * 参数：{ classId }。返回：{ ok }。
   */
  ipcMain.handle('config-panel:delete-class', (_event, payload) => {
    const classId = String(payload?.classId || '');
    const cfg = config.refreshConfig();
    const wasActive = cfg.activeClassId === classId;
    const ok = classes.deleteClass(classId);
    if (ok && wasActive) {
      const remaining = classes.listClasses();
      const next = remaining.length > 0 ? remaining[0].classId : '';
      config.saveConfig(config.normalizeConfig({ ...cfg, activeClassId: next }));
      console.log('[ipc] 当前班级已删除，回退到:', next || '(无)');
    }
    console.log('[ipc] 删除班级:', classId, ok);
    return { ok };
  });

  /*
   *  config-panel:close
   *  方向：渲染 → 主（单向通知）
   *  用途：关闭配置面板窗口。
   *  参数：payload.saved — true 表示关闭前已保存（需刷新悬浮窗），
   *                        false 表示取消（无需额外操作）。
   */
  ipcMain.on('config-panel:close', (_event, payload) => {
    /* 关闭配置面板：清空未应用的内存暂存资源（下次打开即舍弃） */
    customs.stagedClear();
    windows.closeConfigPanelWindow(Boolean(payload && payload.saved));
  });

  /*
   *  config-panel:get-app-info
   *  方向：渲染 → 主（请求-响应）
   *  用途：配置面板 > 高级设置 Tab 获取系统信息（进程权限、路径、版本号）。
   *  返回：{ isAdmin, isUiAccess, isWindows, uiAccessDllExists,
   *           configPath, configDir, exePath, version }
   */
  ipcMain.handle('config-panel:get-app-info', () => {
    return {
      isAdmin: admin.isProcessElevated(),
      isUiAccess: admin.isUiAccessProcess(),
      isWindows: admin.IS_WINDOWS,
      uiAccessDllExists: Boolean(admin.getDefaultUiAccessDllPath()),
      configPath: config.getConfigPath(),
      configDir: config.getConfigDir(),
      exePath: admin.getDefaultExePath(),
      version: app.getVersion()
    };
  });

  /*
   *  config-panel:admin-elevate
   *  方向：渲染 → 主（请求-响应）
   *  用途：以管理员权限重新启动应用（通过 PowerShell Start-Process -Verb RunAs）。
   *  行为：成功则 150ms 后退出当前进程（新进程以管理员身份启动）。
   *  返回：{ ok, message, detail? } — 与 admin.requestAdminRelaunch() 一致。
   */
  ipcMain.handle('config-panel:admin-elevate', () => {
    console.log('[ipc] 用户请求管理员提权重启');
    const result = admin.requestAdminRelaunch();
    if (result.ok) {
      windows.setQuitting(true);
      setTimeout(() => app.exit(0), 150);
    }
    return result;
  });

  /*
   *  config-panel:restart
   *  方向：渲染 → 主（请求-响应）
   *  用途：普通重启应用（不提升权限，保留当前权限级别）。
   *  行为：80ms 后调用 app.relaunch() + app.exit(0)。
   *  注意：app.relaunch() 仅在打包版本有效，开发模式下行为与 app.exit(0) 相同。
   */
  ipcMain.handle('config-panel:restart', () => {
    windows.setQuitting(true);
    setTimeout(() => {
      app.relaunch();
      app.exit(0);
    }, 80);
    return { ok: true };
  });

  /*
   *  config-panel:create-startup-task
   *  方向：渲染 → 主（请求-响应）
   *  用途：创建或更新 Windows 计划任务（用户登录时自动启动应用）。
   *  参数：payload.taskName — 计划任务名称
   *        payload.exePath  — 要启动的 .exe 文件路径
   *  返回：{ ok, message, detail? } — 与 admin.createAdminStartupTask() 一致。
   */
  ipcMain.handle('config-panel:create-startup-task', (_event, payload) => {
    console.log('[ipc] 创建计划任务, 路径=' + (payload.exePath || '自动检测'));
    const result = admin.createAdminStartupTask({
      taskName: payload.taskName,
      exePath: payload.exePath
    });
    console.log('[ipc] 计划任务结果:', result.ok ? '成功' : '失败', result.message || '');
    return result;
  });

  /*
   *  config-panel:open-config-file / open-config-dir
   *  方向：渲染 → 主（请求-响应）
   *  用途：在系统默认应用中打开配置文件（config.yml）或所在文件夹。
   *  实现：调用 Electron shell.openPath()，在资源管理器/默认编辑器中打开。
   *  返回：{ ok, message } — 成功或失败的反馈。
   */
  ipcMain.handle('config-panel:open-config-file', () => {
    return config.openConfigFile();
  });

  ipcMain.handle('config-panel:open-config-dir', () => {
    return config.openConfigDir();
  });

  /*
   *  config-panel:clear-cache
   *  方向：渲染 → 主（请求-响应）
   *  用途：清理 Chromium 会话/缓存数据（sessionData 目录，即 userData/session）。
   *  说明：缓存全部为可丢弃数据，删除不影响任何配置。运行中部分文件可能被占用
   *        删除失败（如正在写入的缓存），返回统计信息，下次启动会自动重建。
   *  返回：{ ok, removed, failed, message }。
   */
  ipcMain.handle('config-panel:clear-cache', () => {
    const sessionDir = app.getPath('sessionData');
    let removed = 0;
    let failed = 0;
    try {
      if (fs.existsSync(sessionDir)) {
        for (const entry of fs.readdirSync(sessionDir)) {
          try {
            fs.rmSync(path.join(sessionDir, entry), { recursive: true, force: true });
            removed++;
          } catch (_) {
            failed++;
          }
        }
      }
    } catch (error) {
      console.error('[ipc] 清理缓存失败:', error.message);
      return { ok: false, message: '清理缓存失败: ' + error.message };
    }
    console.log(`[ipc] 清理缓存完成, 移除=${removed}, 失败=${failed}`);
    return {
      ok: true,
      removed,
      failed,
      message: failed > 0
        ? `已清理 ${removed} 项，${failed} 项被占用（将在下次启动时重建）`
        : `已清理 ${removed} 项缓存`
    };
  });

  /*
   *  config-panel:save-custom
   *  方向：渲染 → 主（请求-响应）
   *  用途：保存一个自定义资源到 customs/ 目录（自动生成 custom_<时间戳>.yml）。
   *  参数：{ fileName, mimeType, purpose, base64, oldCustomId? }
   *        oldCustomId — 若提供且与新 id 不同，保存后删除旧资源（避免堆积）。
   *  返回：{ ok, id }。
   */
  ipcMain.handle('config-panel:save-custom', (_event, payload) => {
    const { fileName, mimeType, purpose, base64, duration, oldCustomId } = payload || {};
    const custom = customs.saveCustom({ fileName, mimeType, purpose, base64, duration });
    if (!custom) return { ok: false, message: '保存自定义资源失败' };
    if (oldCustomId && oldCustomId !== custom.id) {
      customs.deleteCustom(oldCustomId);
    }
    console.log('[ipc] 保存自定义资源:', custom.id, purpose || '');
    return { ok: true, id: custom.id };
  });

  /*
   *  config-panel:get-custom
   *  方向：渲染 → 主（请求-响应）
   *  用途：按 id 读取一个自定义资源（staged 内存暂存 或 customs/ 磁盘文件）。
   *  参数：{ id }。返回：资源对象 { id, fileName, mimeType, purpose, base64 } 或 null。
   */
  ipcMain.handle('config-panel:get-custom', (_event, payload) => {
    const id = String(payload?.id || '');
    /* staged 前缀 → 从内存暂存读取（未应用的上传） */
    if (id.startsWith('staged_custom_')) return customs.stagedGetCustom(id);
    return customs.loadCustom(id);
  });

  /*
   *  config-panel:delete-custom
   *  方向：渲染 → 主（请求-响应）
   *  用途：按 id 删除一个自定义资源文件。
   *  参数：{ id }。返回：{ ok }。
   */
  ipcMain.handle('config-panel:delete-custom', (_event, payload) => {
    const id = String(payload?.id || '');
    const ok = customs.deleteCustom(id);
    console.log('[ipc] 删除自定义资源:', id, ok);
    return { ok };
  });

  /*
   *  config-panel:save-custom-staged
   *  方向：渲染 → 主（请求-响应）
   *  用途：将上传的自定义资源暂存到主进程内存（不落盘）。
   *        数据仅在点击“应用”保存配置时写入 customs/ 目录；
   *        关闭配置面板 / 未应用则被舍弃。
   *  参数：{ fileName, mimeType, purpose, base64, duration? }。
   *  返回：{ ok, id } —— id 为 staged_custom_<时间戳> 引用。
   */
  ipcMain.handle('config-panel:save-custom-staged', (_event, payload) => {
    const { fileName, mimeType, purpose, base64, duration } = payload || {};
    const stagedId = customs.stagedSaveCustom({ fileName, mimeType, purpose, base64, duration });
    if (!stagedId) return { ok: false, message: '暂存自定义资源失败' };
    console.log('[ipc] 暂存自定义资源:', stagedId, purpose || '');
    return { ok: true, id: stagedId };
  });

  /*
   *  config-panel:check-update
   *  方向：渲染 → 主（请求-响应）
   *  用途：从 GitHub Releases API 检查是否有新版本。
   *  返回：{ status, title, detail, releaseUrl? } — 'update' / 'ok' / 'error'。
   *  注意：此操作涉及网络请求，可能耗时 1-3 秒。
   */
  ipcMain.handle('config-panel:check-update', () => {
    return update.checkUpdateFromMain();
  });

  /*
   *  config-panel:pick-exe-file
   *  方向：渲染 → 主（请求-响应，异步）
   *  用途：打开系统原生文件选择对话框，让用户选取一个 .exe 文件。
   *  返回：选中文件的绝对路径字符串，用户取消则返回 null。
   *  注意：dialog.showOpenDialog 返回 Promise，使用 async/await 等待用户操作。
   */
  ipcMain.handle('config-panel:pick-exe-file', async () => {
    const result = await dialog.showOpenDialog({
      title: '选择可执行文件',
      filters: [
        { name: '可执行文件', extensions: ['exe'] },
        { name: '所有文件', extensions: ['*'] }
      ],
      properties: ['openFile']
    });
    if (result.canceled || result.filePaths.length === 0) return null;
    return result.filePaths[0];
  });

  /*
   *  config-panel:reset-config
   *  方向：渲染 → 主（请求-响应）
   *  用途：重置整个配置目录（恢复出厂默认）。
   *  流程：
   *    1. security.resetAllConfig() — 删除 .SHA256（密码）+ 清空 classes/（班级）
   *       + 清空 customs/（自定义资源）+ 重置 config.yml 为默认
   *    2. refreshFloatingButtonWindow() — 重建悬浮窗
   *  注意：此操作不可撤销，前端会弹出二次确认对话框。
   */
  ipcMain.handle('config-panel:reset-config', () => {
    console.log('[ipc] 重置配置为默认值（清除密码/班级/自定义资源）');
    const result = security.resetAllConfig();
    windows.refreshFloatingButtonWindow();
    return { ok: true, message: result.message };
  });

  /*
   *  config-panel:get-logs
   *  方向：渲染 → 主（请求-响应）
   *  用途：配置面板 > 日志 Tab 拉取磁盘日志文件内容。
   *  参数：maxLines（可选）— 最多返回的行数，默认 500。
   *  返回：日志条目数组（时间倒序，最新在前）。
   *  竞态保护：读取前等待所有待处理写入完成（logging 模块内部互斥锁）。
   */
  ipcMain.handle('config-panel:get-logs', async (_event, maxLines) => {
    return logging.getLogs(typeof maxLines === 'number' ? maxLines : 500);
  });

  /*
   *  config-panel:open-devtools
   *  方向：渲染 → 主（请求-响应）
   *  用途：打开指定窗口的 DevTools（开发/生产均可用）。
   *  参数：target — 'floating' | 'config' | 'result'
   */
  ipcMain.on('config-panel:open-devtools', (_event, target) => {
    let win = null;
    if (target === 'floating') {
      win = windows.getFloatingButtonWindow();
    } else if (target === 'config') {
      win = windows.getConfigPanelWindow();
    } else if (target === 'result') {
      win = windows.getPickResultWindow();
    }
    if (win && !win.isDestroyed()) {
      win.webContents.openDevTools({ mode: 'detach' });
    }
  });
}


// ============================================================================
//  导出 —— main.js 在启动流程中按顺序调用这两个注册函数
//
//  调用顺序（见 main.js）：
//    1. ipc.registerIpcHandlers()        ← app.whenReady() 中最先调用
//    2. ...创建托盘、悬浮窗、configPanel...
//    3. ipc.registerConfigPanelIpc()     ← configPanel 窗口创建后调用
// ============================================================================
module.exports = {
  registerIpcHandlers,
  registerConfigPanelIpc
};
