/*
================================================================================
技术文档：src/main/config.js
职责：应用配置的完整生命周期管理 —— 定义、校验、读写、迁移、序列化。

================================================================================
模块导入说明
================================================================================
  require('electron')      → Electron 框架，提供 app（获取用户数据路径）、
                             shell（打开文件/文件夹）
  require('fs')            → Node.js 文件系统，用于读写 config.yml
  require('path')          → 路径拼接与解析
  require('js-yaml')       → YAML 解析器，将 config.yml 文本转为 JS 对象
  require('./admin')       → 权限模块，提供 Windows 相关常量

================================================================================
核心函数一览
================================================================================
  函数                         | 作用
  ─────────────────────────────┼──────────────────────────────────────────────
  clampNumber(v, min, max, d)  | 数值范围裁剪（内部工具）
  normalizeConfig(input)       | 清洗配置对象（范围校验 + 默认值填充）
  toConfigYamlWithComments(c)  | 将配置对象序列化为带中文注释的 YAML 文本
  saveConfig(config)           | 将配置写入 config.yml 磁盘文件
  loadConfig()                 | 从磁盘读取并解析 config.yml
  refreshConfig()              | 重新加载配置并返回（每次 IPC 请求时调用）
  getConfigPath()              | 获取 config.yml 的完整文件路径
  getConfigDir()               | 获取配置目录的路径
  getLegacyConfigPaths()       | 获取旧版配置文件的可能位置（用于迁移）
  writeDefaultConfigIfMissing()| 首次启动时创建默认配置或从旧版迁移
  openConfigFile()             | 用系统默认应用打开 config.yml
  openConfigDir()              | 在资源管理器中打开配置文件夹

================================================================================
数据流
================================================================================
  写入：Vue 面板 → IPC → normalizeConfig() → saveConfig() → config.yml
  读取：IPC 请求 → refreshConfig() → loadConfig() → config.yml → normalizeConfig()

================================================================================
维护建议
================================================================================
  - 新增配置项必须同步三处：DEFAULT_CONFIG（默认值）、normalizeConfig（校验）、
    toConfigYamlWithComments（YAML 序列化 + 中文注释）
  - Vue 端 useConfigPanel.js 的 draft 对象也需同步初始化
  - normalizeConfig 是安全边界：所有外部输入必须经过此函数清洗才能落盘
  - 不要直接在函数中修改 DEFAULT_CONFIG 对象（它是共享引用）
  - YAML 中布尔值写作 true/false（小写无引号），字符串需引号包裹

================================================================================
更新记录
================================================================================
  2026-08-29  悬浮按钮自定义图标改为 customs/ 目录存储：
              - floatingButton 新增 customIconId（引用 customs/ 目录下的资源 id），
                iconDataUrl 标记废弃（仅兼容旧配置读取，不再写入 config.yml）
              - 旧版内嵌图标由 customs.ensureInitialized() 启动时自动迁移
  2026-08-29  背景音乐改为 customs/ 可加载资源：
              - pickResultDialog 新增 bgmCustomId（引用 customs/ 资源，空=未上传）
  2026-08-29  渲染后端：移除 OpenGL（gl）支持，仅保留 d3d9 / vulkan
  2026-08-29  抽取音效改为 customs/ 可加载资源：
              - pickResultDialog 新增 gachaSoundCustomId（引用 customs/ 资源，空=未上传）
  2026-08-29  学生新增 letterColor（信封颜色：rainbow/gold/blue，缺省自动升级为 rainbow）
  2026-08-29  修复悬浮按钮坐标 X 始终为 0：
              - position.x/y 归一化与序列化改用显式排除 null/undefined/空串判断，
                因 Number(null) === 0，会把“null=自动位置”误判为有效坐标 0
================================================================================
*/
const { app, shell } = require('electron');
const fs = require('fs');
const path = require('path');
const yaml = require('js-yaml');
const admin = require('./admin');


// ============================================================================
//  DEFAULT_CONFIG —— 所有配置项的默认值（"出厂设置"）
//
//  这个对象定义了应用全部可配置字段的结构和默认值。
//  新增配置项时在这里添加字段，然后在 normalizeConfig 和
//  toConfigYamlWithComments 中同步处理。
//
//  注意：此对象被多处引用，不要直接修改它的属性值。
//  需要默认配置时用 normalizeConfig({}) 生成一份副本。
// ============================================================================
const DEFAULT_CONFIG = {
  /* ---- 抽取名单 ---- */
  studentList: [],           // 当前班级名单镜像（权威数据在 classes/ 下对应班级文件）
  allowRepeatDraw: true,     // 同一轮是否允许抽到同一人
  agreedEula: false,         // 是否已同意最终用户协议（EULA）
  activeClassId: '',         // 当前班级 id（对应 classes/ 下的班级文件）

  /* ---- 悬浮按钮 ---- */
  floatingButton: {
    sizePercent: 100,        // 按钮大小百分比（基准 50px），范围 50-200
    transparencyPercent: 20, // 透明度百分比（0=不透明，100=全透明），范围 0-100
    alwaysOnTop: true,       // 是否始终置顶于其他窗口之上
    showInTaskbar: false,    // 悬浮窗和结果浮窗是否在任务栏可见
    position: {              // 屏幕坐标，配置面板手动应用时保存
      x: null,               // null = 使用系统默认位置
      y: null
    },
    /*
     * 自定义图标引用（customs/ 目录下的资源 id，如 custom_1724912345678）。
     * 空字符串 = 使用内置默认图标 /image/app.ico。
     * 图片数据（base64）存于 customs/custom_<时间戳>.yml，此处只保存引用。
     * 旧版 iconDataUrl（config.yml 内嵌 base64）已废弃，由 customs.ensureInitialized()
     * 启动时自动迁移到 customs/ 目录。
     */
    customIconId: '',
    iconDataUrl: '',        // 已废弃（仅兼容旧配置读取，不再写入 config.yml）
    iconSize: 48,            // 图标显示尺寸（px），范围 16-128
    borderColor: '#ffffff'   // 按钮圆形边框颜色（hex）
  },

  /* ---- 人数选择 ---- */
  pickCountDialog: {
    defaultCount: 1          // 默认抽取人数，范围 1-10
  },

  /* ---- 抽奖结果弹窗 ---- */
  pickResultDialog: {
    defaultPlayGachaSound: true,  // 是否播放抽卡音效
    gachaSoundCustomId: '',       // 抽取音效资源引用（customs/ 目录，空=未上传音效）
    soundVolume: 80,              // 音效音量（UI 百分比），范围 0-100
    playMusic: false,             // 是否播放抽卡背景音乐
    bgmCustomId: '',              // BGM 资源引用（customs/ 目录，空=未上传背景音乐）
    musicVolume: 60,              // 音乐音量（UI 百分比），范围 0-100
    bgmStartTime: 0,              // BGM 起始秒数，范围 0-120
    bgmFadeDuration: 1.5,         // 淡入淡出秒数，范围 0.5-5
    panelOpacity: 0.9,            // 面板不透明度，范围 0.1-1.0
    panelBgColor: '#ffffff',      // 面板背景颜色（hex）
    panelBorderColor: '#66ccff',  // 面板边框颜色（hex）
    showDeco: true                // 结果浮窗顶部是否显示阿罗娜 & 普拉娜装饰图
  },

  /* ---- 高级设置（Windows 权限与计划任务） ---- */
  admin: {
    adminAutoStartAdmin: true,            // 计划任务以管理员身份运行
    adminAutoStartPath: '',               // 计划任务的目标 exe 路径（空=自动检测）
    adminAutoStartTaskName: admin.ADMIN_TASK_DEFAULT_NAME,  // 计划任务名称
    requireAdminOnLaunch: false,          // 启动时获取管理员权限（开启后显示 UIAccess 选项）
    uiAccessEnabled: false,              // 启用 UIAccess 置顶
    /*
     * Chromium 渲染后端（全模式通用，需重启）。
     *
     * d3d9   — ANGLE/D3D9（默认，所有模式推荐，UIAccess 兼容）
     * vulkan — ANGLE/Vulkan（透明窗口依赖 DirectComposition 合成）
     * （OpenGL 已移除：性能与兼容性均差）
     */
    renderingBackend: 'd3d9',
    /*
     * 禁用 DirectComposition（GDI 呈现）。
     * 与 renderingBackend 组合使用：不同 ANGLE 后端 +
     * 启用/禁用 DirectComposition 的组合可能产生不同性能表现。
     * 默认 true，需重启生效。
     */
    disableDirectComposition: true,
    /*
     * 禁用 GPU 硬件加速（完全回退到 CPU 软件渲染）。
     * 默认 false。启用后所有渲染由 CPU 完成，
     * 包括 CSS 动画、WebGL、Canvas 2D 等。需重启。
     */
    disableHardwareAcceleration: false
  }
};


// ============================================================================
//  clampNumber(value, min, max, fallback) —— 数值范围裁剪
//
//  将任意输入值限制在 [min, max] 范围内。如果输入不是有效数字，返回 fallback。
//
//  示例：
//    clampNumber(150, 0, 100, 50) → 100   （超出上限，裁剪）
//    clampNumber(-5,  0, 100, 50) → 0     （低于下限，裁剪）
//    clampNumber("abc", 0, 100, 50) → 50  （无效输入，返回 fallback）
//    clampNumber(42,  0, 100, 50) → 42    （范围内，原样返回）
//
//  为什么需要这个函数：
//    用户可能通过手动编辑 config.yml 输入非法值（负数、超大值、文字）。
//    normalizeConfig 在清洗数据时大量使用此函数兜底。
// ============================================================================
function clampNumber(value, min, max, fallback) {
  const num = Number(value);
  if (Number.isNaN(num)) return fallback;
  return Math.min(max, Math.max(min, num));
}

/* 信封颜色取值：rainbow（彩，默认）/ gold（黄）/ blue（蓝） */
const LETTER_COLORS = ['rainbow', 'gold', 'blue'];
function normalizeLetterColor(value) {
  return LETTER_COLORS.includes(value) ? value : 'rainbow';
}


// ============================================================================
//  normalizeConfig(input) —— 配置清洗与归一化（核心安全边界）
//
//  这是整个配置系统最关键的函数。它接收可能包含任意值的输入对象，
//  对每个字段做类型检查、范围裁剪、默认值填充，返回一个"干净"的配置对象。
//
//  为什么需要归一化：
//    1. 用户通过配置面板修改时，Vue 可能产生 undefined/null/越界值
//    2. 用户手动编辑 config.yml 时可能写错类型（如 sizePercent: "abc"）
//    3. 旧版本升级时可能缺少新增字段
//    4. 归一化确保应用代码读取到的配置始终合法，无需在业务逻辑中反复校验
//
//  调用方：
//    - ipc.js 的 config-panel:save-config（面板保存时）
//    - config.js 的 loadConfig()（启动加载时）
//    - config.js 的 IPC 通道（重置配置时）
// ============================================================================
function normalizeConfig(input) {
  /* 防御：输入可能为 null/undefined/非对象 */
  const source = input && typeof input === 'object' ? input : {};

  // --------------------------------------------------------------------------
  //  studentList —— 学生名单数组
  //  支持两种元素格式：
  //    字符串 "张三" → 转为 { name: "张三", weight: 1.0, letterColor: 'rainbow' }
  //    对象 { name: "张三", weight: 2.5 } → 保留，letterColor 缺省为 'rainbow'
  //  过滤掉空名字的条目
  //  letterColor：信封颜色（rainbow/gold/blue），旧配置缺省自动升级为 rainbow
  // --------------------------------------------------------------------------
  const rawStudents = Array.isArray(source.studentList) ? source.studentList : [];
  const students = rawStudents.map(s => {
    if (typeof s === 'string') return { name: s.trim(), weight: 1.0, letterColor: 'rainbow' };
    if (s && typeof s === 'object') return {
      name: String(s.name || '').trim(),
      weight: Number.isFinite(Number(s.weight)) ? Number(s.weight) : 1.0,
      letterColor: normalizeLetterColor(s.letterColor)
    };
    return null;
  }).filter(s => s && s.name);

  // --------------------------------------------------------------------------
  //  allowRepeatDraw / agreedEula —— 布尔字段
  //  只有严格的 true/false 才直接使用，其他值 fallback 到默认值
  // --------------------------------------------------------------------------
  const allowRepeatDraw =
    typeof source.allowRepeatDraw === 'boolean' ? source.allowRepeatDraw : DEFAULT_CONFIG.allowRepeatDraw;
  const agreedEula = typeof source.agreedEula === 'boolean' ? source.agreedEula : DEFAULT_CONFIG.agreedEula;
  const activeClassId = typeof source.activeClassId === 'string' ? source.activeClassId : DEFAULT_CONFIG.activeClassId;

  // --------------------------------------------------------------------------
  //  floatingButton —— 悬浮按钮配置
  // --------------------------------------------------------------------------
  const fb = source.floatingButton && typeof source.floatingButton === 'object' ? source.floatingButton : {};
  const fbPos = fb.position && typeof fb.position === 'object' ? fb.position : {};
  const alwaysOnTop =
    typeof fb.alwaysOnTop === 'boolean' ? fb.alwaysOnTop : DEFAULT_CONFIG.floatingButton.alwaysOnTop;
  const showInTaskbar =
    typeof fb.showInTaskbar === 'boolean' ? fb.showInTaskbar : DEFAULT_CONFIG.floatingButton.showInTaskbar;

  const floatingButton = {
    sizePercent: clampNumber(fb.sizePercent, 50, 200, DEFAULT_CONFIG.floatingButton.sizePercent),
    transparencyPercent: clampNumber(fb.transparencyPercent, 0, 100, DEFAULT_CONFIG.floatingButton.transparencyPercent),
    alwaysOnTop,
    showInTaskbar,
    position: {
      /* 坐标允许为 null（表示使用系统默认位置），数字则必须为有限整数。
         注意：不能用 Number.isFinite(Number(x)) 直接判断，因为 Number(null) === 0，
         会把“自动位置”误判为 0。必须显式排除 null/undefined/空串。 */
      x: (fbPos.x !== null && fbPos.x !== undefined && fbPos.x !== '' && Number.isFinite(Number(fbPos.x))) ? Math.round(Number(fbPos.x)) : null,
      y: (fbPos.y !== null && fbPos.y !== undefined && fbPos.y !== '' && Number.isFinite(Number(fbPos.y))) ? Math.round(Number(fbPos.y)) : null
    },
    /* 自定义图标引用：仅接受 custom_<数字> 形式，否则视为未设置 */
    customIconId: (typeof fb.customIconId === 'string' && /^custom_[0-9]+$/.test(fb.customIconId))
      ? fb.customIconId
      : '',
    /* base64 data URL：必须是以 "data:image/" 开头的合法字符串，否则丢弃（旧版兼容） */
    iconDataUrl: typeof fb.iconDataUrl === 'string' && fb.iconDataUrl.startsWith('data:image/')
      ? fb.iconDataUrl
      : DEFAULT_CONFIG.floatingButton.iconDataUrl,
    iconSize: clampNumber(fb.iconSize, 16, 128, DEFAULT_CONFIG.floatingButton.iconSize),
    /* 边框颜色：非空 hex 字符串，否则使用默认白色 */
    borderColor: typeof fb.borderColor === 'string' && fb.borderColor
      ? fb.borderColor
      : DEFAULT_CONFIG.floatingButton.borderColor
  };

  // --------------------------------------------------------------------------
  //  pickCountDialog —— 人数选择
  // --------------------------------------------------------------------------
  const pick = source.pickCountDialog && typeof source.pickCountDialog === 'object' ? source.pickCountDialog : {};
  const pickCountDialog = {
    defaultCount: Math.round(clampNumber(pick.defaultCount, 1, 10, DEFAULT_CONFIG.pickCountDialog.defaultCount))
  };

  // --------------------------------------------------------------------------
  //  pickResultDialog —— 抽奖结果弹窗
  // --------------------------------------------------------------------------
  const pickResult = source.pickResultDialog && typeof source.pickResultDialog === 'object' ? source.pickResultDialog : {};
  const pickResultDialog = {
    defaultPlayGachaSound:
      typeof pickResult.defaultPlayGachaSound === 'boolean'
        ? pickResult.defaultPlayGachaSound
        : DEFAULT_CONFIG.pickResultDialog.defaultPlayGachaSound,
    soundVolume: clampNumber(pickResult.soundVolume, 0, 100, DEFAULT_CONFIG.pickResultDialog.soundVolume),
    /* 抽取音效资源引用：仅接受 custom_<数字> 形式，否则视为未上传 */
    gachaSoundCustomId: (typeof pickResult.gachaSoundCustomId === 'string' && /^custom_[0-9]+$/.test(pickResult.gachaSoundCustomId))
      ? pickResult.gachaSoundCustomId
      : '',
    playMusic:
      typeof pickResult.playMusic === 'boolean'
        ? pickResult.playMusic
        : DEFAULT_CONFIG.pickResultDialog.playMusic,
    /* BGM 资源引用：仅接受 custom_<数字> 形式，否则视为未上传 */
    bgmCustomId: (typeof pickResult.bgmCustomId === 'string' && /^custom_[0-9]+$/.test(pickResult.bgmCustomId))
      ? pickResult.bgmCustomId
      : '',
    musicVolume: clampNumber(pickResult.musicVolume, 0, 100, DEFAULT_CONFIG.pickResultDialog.musicVolume),
    bgmStartTime: clampNumber(pickResult.bgmStartTime, 0, 120, DEFAULT_CONFIG.pickResultDialog.bgmStartTime),
    bgmFadeDuration: clampNumber(pickResult.bgmFadeDuration, 0.5, 5, DEFAULT_CONFIG.pickResultDialog.bgmFadeDuration),
    panelOpacity: clampNumber(pickResult.panelOpacity, 0.1, 1, DEFAULT_CONFIG.pickResultDialog.panelOpacity),
    panelBgColor:
      typeof pickResult.panelBgColor === 'string' && pickResult.panelBgColor
        ? pickResult.panelBgColor
        : DEFAULT_CONFIG.pickResultDialog.panelBgColor,
    panelBorderColor:
      typeof pickResult.panelBorderColor === 'string' && pickResult.panelBorderColor
        ? pickResult.panelBorderColor
        : DEFAULT_CONFIG.pickResultDialog.panelBorderColor,
    showDeco:
      typeof pickResult.showDeco === 'boolean'
        ? pickResult.showDeco
        : DEFAULT_CONFIG.pickResultDialog.showDeco
  };

  // --------------------------------------------------------------------------
  //  admin —— 高级设置（Windows 权限 + 计划任务 + UIAccess）
  //
  //  向后兼容：如果输入对象中没有 admin 但有旧版 webConfig，则使用 webConfig。
  //  这确保升级前的 config.yml 文件无缝迁移。
  // --------------------------------------------------------------------------
  const adminSource = source.admin && typeof source.admin === 'object'
    ? source.admin
    : (source.webConfig && typeof source.webConfig === 'object' ? source.webConfig : {});
  const adminConfig = {
    adminAutoStartAdmin:
      typeof adminSource.adminAutoStartAdmin === 'boolean'
        ? adminSource.adminAutoStartAdmin
        : DEFAULT_CONFIG.admin.adminAutoStartAdmin,
    /* exe 路径：字符串类型，空串表示自动检测 */
    adminAutoStartPath:
      typeof adminSource.adminAutoStartPath === 'string'
        ? adminSource.adminAutoStartPath
        : DEFAULT_CONFIG.admin.adminAutoStartPath,
    /* 计划任务名：非空字符串，去除首尾空白 */
    adminAutoStartTaskName:
      typeof adminSource.adminAutoStartTaskName === 'string' && adminSource.adminAutoStartTaskName.trim()
        ? adminSource.adminAutoStartTaskName.trim()
        : DEFAULT_CONFIG.admin.adminAutoStartTaskName,
    requireAdminOnLaunch:
      typeof adminSource.requireAdminOnLaunch === 'boolean'
        ? adminSource.requireAdminOnLaunch
        : DEFAULT_CONFIG.admin.requireAdminOnLaunch,
    uiAccessEnabled:
      typeof adminSource.uiAccessEnabled === 'boolean'
        ? adminSource.uiAccessEnabled
        : DEFAULT_CONFIG.admin.uiAccessEnabled,
    renderingBackend:
      typeof adminSource.renderingBackend === 'string'
      && ['d3d9', 'vulkan'].includes(adminSource.renderingBackend)
        ? adminSource.renderingBackend
        : DEFAULT_CONFIG.admin.renderingBackend,
    disableDirectComposition:
      typeof adminSource.disableDirectComposition === 'boolean'
        ? adminSource.disableDirectComposition
        : DEFAULT_CONFIG.admin.disableDirectComposition,
    disableHardwareAcceleration:
      typeof adminSource.disableHardwareAcceleration === 'boolean'
        ? adminSource.disableHardwareAcceleration
        : DEFAULT_CONFIG.admin.disableHardwareAcceleration
  };

  /* 返回干净的配置对象（结构保证与 DEFAULT_CONFIG 完全一致） */
  return {
    studentList: students,
    allowRepeatDraw,
    agreedEula,
    activeClassId,
    floatingButton,
    pickCountDialog,
    pickResultDialog,
    admin: adminConfig
  };
}


// ============================================================================
//  配置文件的磁盘路径
// ============================================================================

/*
 * 获取 config.yml 的完整路径。
 * 位置：%LocalAppData%\BlueRandom\config.yml（由 admin.configureUserDataPath() 设定）
 */
function getConfigPath() {
  return path.join(app.getPath('userData'), 'config.yml');
}

/* 获取配置目录路径（与 config.yml 同目录）。 */
function getConfigDir() {
  return path.dirname(getConfigPath());
}

/*
 * 获取旧版配置文件可能的存储位置（用于首次启动时自动迁移）。
 *
 * 搜索顺序：
 *   1. exe 同目录（便携版场景）
 *   2. 项目根目录（开发模式 npm run dev）
 *   3. 旧版 Local AppData 路径（旧版本遗留）
 *
 * 返回去重后的路径数组（排除当前 configPath 自身）。
 */
function getLegacyConfigPaths() {
  const legacyPaths = [];
  const exeDir = path.dirname(app.getPath('exe'));
  legacyPaths.push(path.join(exeDir, 'config.yml'));

  if (!app.isPackaged) {
    /* 开发模式下，项目根目录也检查 */
    legacyPaths.push(path.join(process.cwd(), 'config.yml'));
  }

  if (admin.IS_WINDOWS) {
    const appData = app.getPath('appData');
    const localRoot = path.resolve(appData, '..', 'Local');
    legacyPaths.push(path.join(localRoot, 'Blue Random', 'config.yml'));
  }

  const currentPath = getConfigPath();
  /* Set 去重 + 排除当前路径 */
  return Array.from(new Set(legacyPaths.filter(p => p && p !== currentPath)));
}


// ============================================================================
//  配置文件的读写
// ============================================================================

/*
 * 将配置对象序列化为带中文注释的 YAML 文本。
 *
 * YAML 是一种人类可读的配置文件格式，类似 JSON 但支持注释。
 * 此函数生成的 YAML 包含详细的中文说明，方便用户手动编辑。
 *
 * yamlSingleQuote 内部函数：
 *   YAML 单引号字符串：'value'，内部单引号需写成两个 ''。
 *   使用单引号而非双引号，因为双引号内 \ 会被转义，Windows 路径
 *   中的反斜杠会导致意外行为。
 */
function toConfigYamlWithComments(config) {
  const fb = config.floatingButton;
  const pick = config.pickCountDialog;
  const pickResult = config.pickResultDialog;
  const adminCfg = config.admin;

  /* 坐标值：数字转整数字符串，null/undefined 写作 'null'（不能靠 Number()，null→0） */
  const posX = (fb.position.x !== null && fb.position.x !== undefined && Number.isFinite(Number(fb.position.x))) ? String(Math.round(Number(fb.position.x))) : 'null';
  const posY = (fb.position.y !== null && fb.position.y !== undefined && Number.isFinite(Number(fb.position.y))) ? String(Math.round(Number(fb.position.y))) : 'null';

  const yamlSingleQuote = (value) => `'${String(value || '').replace(/'/g, "''")}'`;

  /* 学生名单：每行一条 YAML 数组元素 */
  const studentLines = Array.isArray(config.studentList) && config.studentList.length > 0
    ? '\n' + config.studentList.map(s => `  - name: "${s.name}"\n    weight: ${s.weight}\n    letterColor: ${s.letterColor || 'rainbow'}`).join('\n')
    : ' []';

  /* 拼接完整 YAML 文本（数组元素用 \n 连接为单个字符串） */
  return [
    '# ============================================================',
    '#  蔚蓝点名 配置文件',
    '#  通过配置面板（托盘 → 配置）修改后自动保存',
    '#  也可手动编辑此文件，保存后重启应用生效',
    '# ============================================================',
    '',
    '# ---- 抽取名单 ----',
    `studentList:${studentLines}`,
    `allowRepeatDraw: ${config.allowRepeatDraw ? 'true' : 'false'}`,
    `agreedEula: ${config.agreedEula ? 'true' : 'false'}`,
    '# 当前班级 id（对应 classes/ 目录下的班级文件），名单与权重存于班级文件中',
    `activeClassId: ${yamlSingleQuote(config.activeClassId || '')}`,
    '',
    '# ---- 悬浮按钮 ----',
    'floatingButton:',
    '  # 按钮大小百分比（50-200），默认 100',
    `  sizePercent: ${fb.sizePercent}`,
    '  # 透明度百分比（0-100），0=不透明，100=全透明，默认 20',
    `  transparencyPercent: ${fb.transparencyPercent}`,
    '  # 是否始终置顶（true/false），默认 true',
    `  alwaysOnTop: ${fb.alwaysOnTop ? 'true' : 'false'}`,
    '  # 悬浮窗和结果浮窗是否在任务栏可见（true/false），默认 false',
    `  showInTaskbar: ${fb.showInTaskbar ? 'true' : 'false'}`,
    '  # 窗口位置（屏幕坐标），配置面板中手动应用时保存；null 为系统默认',
    '  position:',
    `    x: ${posX}`,
    `    y: ${posY}`,
    '  # 自定义图标引用（对应 customs/ 目录下的资源文件），空字符串为内置默认图标',
    '  # 资源文件：customs/custom_<时间戳>.yml，内容含 fileName/mimeType/purpose/base64',
    `  customIconId: ${yamlSingleQuote(fb.customIconId || '')}`,
    '  # 图标尺寸（px），范围 16-128，默认 48',
    `  iconSize: ${fb.iconSize}`,
    '  # 按钮边框颜色（hex），默认 #ffffff',
    `  borderColor: ${yamlSingleQuote(fb.borderColor || '#ffffff')}`,
    '',
    '# ---- 人数选择 ----',
    'pickCountDialog:',
    '  # 默认抽取人数（1-10），默认 1',
    `  defaultCount: ${pick.defaultCount}`,
    '',
    '# ---- 抽奖结果弹窗 ----',
    'pickResultDialog:',
    '  # 是否播放抽卡音效（true/false），默认 true',
    `  defaultPlayGachaSound: ${pickResult.defaultPlayGachaSound ? 'true' : 'false'}`,
    '  # 抽取音效资源引用（customs/ 目录下的 custom_<时间戳>.yml），空 = 未上传音效',
    `  gachaSoundCustomId: ${yamlSingleQuote(pickResult.gachaSoundCustomId || '')}`,
    '  # 音效音量（0-100），默认 80',
    `  soundVolume: ${pickResult.soundVolume}`,
    '  # 是否播放抽卡背景音乐（true/false），默认 false',
    `  playMusic: ${pickResult.playMusic ? 'true' : 'false'}`,
    '  # BGM 资源引用（customs/ 目录下的 custom_<时间戳>.yml），空 = 未上传背景音乐',
    `  bgmCustomId: ${yamlSingleQuote(pickResult.bgmCustomId || '')}`,
    '  # 音乐音量（0-100），默认 60',
    `  musicVolume: ${pickResult.musicVolume}`,
    '  # BGM 播放起始位置（秒），范围 0-120，默认 0（从头开始）',
    `  bgmStartTime: ${pickResult.bgmStartTime}`,
    '  # BGM 淡入淡出时长（秒），范围 0.5-5，默认 1.5',
    `  bgmFadeDuration: ${pickResult.bgmFadeDuration}`,
    '  # 面板不透明度（0.1-1.0），默认 0.9',
    `  panelOpacity: ${pickResult.panelOpacity}`,
    '  # 面板背景颜色（hex），默认 #ffffff',
    `  panelBgColor: ${yamlSingleQuote(pickResult.panelBgColor || '#ffffff')}`,
    '  # 面板边框颜色（hex），默认 #66ccff',
    `  panelBorderColor: ${yamlSingleQuote(pickResult.panelBorderColor || '#66ccff')}`,
    '  # 结果浮窗顶部是否显示阿罗娜 & 普拉娜装饰图（true/false），默认 true',
    `  showDeco: ${pickResult.showDeco ? 'true' : 'false'}`,
    '',
    '# ---- 高级设置 ----',
    'admin:',
    '  # 开机计划任务是否以管理员身份运行',
    `  adminAutoStartAdmin: ${adminCfg.adminAutoStartAdmin ? 'true' : 'false'}`,
    '  # 计划任务的可执行文件路径（留空则自动检测）',
    `  adminAutoStartPath: ${yamlSingleQuote(adminCfg.adminAutoStartPath)}`,
    '  # 计划任务在任务计划程序中的显示名称',
    `  adminAutoStartTaskName: ${yamlSingleQuote(adminCfg.adminAutoStartTaskName || admin.ADMIN_TASK_DEFAULT_NAME)}`,
    '  # 启动时自动获取管理员权限（开启后显示 UIAccess 选项）',
    `  requireAdminOnLaunch: ${adminCfg.requireAdminOnLaunch ? 'true' : 'false'}`,
    '  # 管理员运行时启用 UIAccess（需要 uiaccess.dll 随包分发）',
    `  uiAccessEnabled: ${adminCfg.uiAccessEnabled ? 'true' : 'false'}`,
    '  # Chromium 渲染后端: d3d9 | vulkan（需重启；OpenGL 已移除）',
    `  renderingBackend: ${yamlSingleQuote(adminCfg.renderingBackend || 'd3d9')}`,
    '  # 禁用 DirectComposition（GDI 呈现，默认开启，需重启）',
    `  disableDirectComposition: ${adminCfg.disableDirectComposition !== false ? 'true' : 'false'}`,
    '  # 禁用 GPU 硬件加速（全部 CPU 软件渲染，默认关闭，需重启）',
    `  disableHardwareAcceleration: ${adminCfg.disableHardwareAcceleration ? 'true' : 'false'}`,
    ''
  ].join('\n');
}

/*
 * 保存配置到磁盘。
 *
 * 步骤：
 *  1. 确保目标目录存在（mkdirSync recursive）
 *  2. 将配置对象转为 YAML 文本
 *  3. 同步写入文件（writeFileSync —— 原子性更好，避免并发写冲突）
 *
 * 注意：写入是同步操作，会阻塞主线程约 1-5ms（配置文件通常 < 50KB）。
 *        对于非高频写入场景（用户在面板点"应用"或启动时），同步写入可接受。
 */
function saveConfig(config) {
  const configPath = getConfigPath();
  const yamlText = toConfigYamlWithComments(config);
  fs.mkdirSync(path.dirname(configPath), { recursive: true });
  fs.writeFileSync(configPath, yamlText, 'utf8');
  console.log('[config] 配置已保存到磁盘, size=' + yamlText.length + ' bytes');
}

/*
 * 首次启动时创建配置文件。
 *
 * 逻辑：
 *   1. 如果目标路径已有文件 → 不动（保留现有配置）
 *   2. 否则，按顺序搜索旧版路径：
 *      a. 找到 → 复制到新位置（迁移）
 *      b. 找不到 → 写入 DEFAULT_CONFIG（全新安装）
 *
 * 这样确保用户升级后不会丢失旧配置。
 */
function writeDefaultConfigIfMissing(configPath) {
  if (fs.existsSync(configPath)) {
    return;  /* 配置已存在，无需操作 */
  }
  /* 尝试从旧版路径迁移 */
  for (const legacyPath of getLegacyConfigPaths()) {
    if (fs.existsSync(legacyPath)) {
      fs.mkdirSync(path.dirname(configPath), { recursive: true });
      fs.copyFileSync(legacyPath, configPath);
      return;
    }
  }
  /* 全新安装，写入默认配置 */
  saveConfig(DEFAULT_CONFIG);
}

/*
 * 从磁盘读取配置并归一化。
 *
 * 流程：
 *   1. 确保配置文件存在（不存在则创建/迁移）
 *   2. 读取 YAML 文本
 *   3. 用 js-yaml 解析为 JS 对象
 *   4. 通过 normalizeConfig 清洗数据（仅内存生效）
 *
 * 简化：读取时不再写回磁盘；config.yml 仅在用户手动触发保存时落盘
 * （配置面板“应用”/“重置”）。
 *
 * 错误处理：
 *   若 YAML 解析失败（语法错误）或文件损坏，返回默认配置（不覆盖用户文件），
 *   保证应用至少能正常运行。
 */
function loadConfig() {
  const configPath = getConfigPath();
  writeDefaultConfigIfMissing(configPath);

  try {
    const raw = fs.readFileSync(configPath, 'utf8');
    const parsed = yaml.load(raw);
    const normalized = normalizeConfig(parsed);
    console.log('[config] 配置加载成功, path=' + configPath + ', size=' + raw.length + ' bytes');
    return normalized;
  } catch (error) {
    console.error('[config] 配置加载失败，使用默认值:', error.message);
    /* 文件损坏时仅返回默认值，不覆盖用户文件 */
    return normalizeConfig(DEFAULT_CONFIG);
  }
}

/*
 * 刷新内存中的配置并返回最新值。
 *
 * 每次 IPC 请求配置时调用（不缓存），确保用户手动编辑 config.yml
 * 后无需重启应用即可生效。
 *
 * 注意：此函数每次调用都会读写磁盘。当前场景下调用频率极低
 * （只在 IPC handle 触发时调用），无性能问题。
 */
function refreshConfig() {
  return loadConfig();
}


// ============================================================================
//  辅助功能：打开配置文件 / 配置目录
// ============================================================================

/*
 * 用系统默认应用打开配置文件。
 *
 * 在 Windows 上通常用记事本打开 .yml 文件。
 * shell.openPath 返回空字符串表示成功，返回错误消息字符串表示失败。
 */
async function openConfigFile() {
  const configPath = getConfigPath();
  writeDefaultConfigIfMissing(configPath);
  const result = await shell.openPath(configPath);
  if (result) {
    return { ok: false, message: `打开配置文件失败: ${result}` };
  }
  return { ok: true, message: '已打开配置文件。' };
}

/*
 * 在文件资源管理器中打开配置文件夹。
 * 先确保目录存在（mkdirSync recursive），再调用 shell.openPath。
 */
async function openConfigDir() {
  const configDir = getConfigDir();
  fs.mkdirSync(configDir, { recursive: true });
  const result = await shell.openPath(configDir);
  if (result) {
    return { ok: false, message: `打开配置目录失败: ${result}` };
  }
  return { ok: true, message: '已打开配置目录。' };
}


// ============================================================================
//  导出 —— 供 ipc.js、windows.js 等模块使用
// ============================================================================
module.exports = {
  DEFAULT_CONFIG,
  getConfigDir,
  getConfigPath,
  getLegacyConfigPaths,
  loadConfig,
  normalizeConfig,
  openConfigDir,
  openConfigFile,
  refreshConfig,
  saveConfig,
  writeDefaultConfigIfMissing
};






