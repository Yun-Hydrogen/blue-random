/*
================================================================================
技术文档：src/main/customs.js
职责：用户自定义资源（customs）存储 —— 每个资源一份 YAML 配置文件。

目录结构：
  <配置根目录>/
    config.yml          —— 全局设置（含 customIconId 等引用）
    customs/
      custom_<时间戳>.yml —— 每个自定义资源一份（文件名 + 类型 + 用途 + base64）
        文件名：custom_<完整毫秒时间戳>.yml（如 custom_1724912345678.yml）
        —— 用完整日期时间戳而非随机数，命名可预期、便于排查

资源文件格式（YAML）：
  id: 'custom_1724912345678'
  fileName: 'icon.png'       # 原始文件名
  mimeType: 'image/png'      # 实际文件类型
  purpose: 'floating-icon'   # 用途（如悬浮按钮图标 / bgm 背景音乐）
  duration: 0                # 可选：音频时长（秒，仅 bgm 等音频资源填写）
  base64: 'iVBORw0KGgo...'   # base64 编码的数据

保存策略：
  - 上传的资源先“暂存到内存”（staged）：点击配置面板“应用”时才写入 customs/；
    未点击应用则只存在于内存，关闭/重新打开配置面板即舍弃，绝不落盘
  - 应用时 staged 转正为 custom_<时间戳>.yml；被替换/移除的旧资源同步删除
  - config.yml 只保存引用 id（如 floatingButton.customIconId、bgmCustomId），
    不再内嵌大段 base64，保持配置文件可读
  - 旧版内嵌 iconDataUrl（config.yml 内 base64 data URL）在启动时自动迁移到
    customs/ 目录，并把引用写入 customIconId。

维护建议：
  - 修改资源文件格式时同步更新 normalizeCustom / toCustomYamlWithComments
  - customId 使用完整毫秒时间戳（Date.now()），碰撞时 +1ms 重试
================================================================================
*/
const fs = require('fs');
const path = require('path');
const yaml = require('js-yaml');
const config = require('./config');

/* 自定义资源文件前缀与文件名规则 */
const CUSTOM_FILE_PREFIX = 'custom_';
const CUSTOM_FILE_RE = /^custom_[0-9]+\.yml$/;

/* YAML 单引号转义（与 config.js 一致） */
const yamlSingleQuote = (value) => `'${String(value || '').replace(/'/g, "''")}'`;

/* 自定义资源目录：<配置根目录>/customs */
function getCustomsDir() {
  return path.join(config.getConfigDir(), 'customs');
}

/*
 * 生成资源 id：custom_<完整毫秒时间戳>。
 * 使用完整日期时间戳而非随机数（可预期、便于排查）；
 * 极端情况下同毫秒重复则 +1ms 重试（由调用方做存在性检查）。
 */
function generateCustomId() {
  return `${CUSTOM_FILE_PREFIX}${Date.now()}`;
}

/* 校验 customId 是否合法（仅允许 custom_ + 数字） */
function isValidCustomId(customId) {
  return typeof customId === 'string' && CUSTOM_FILE_RE.test(`${customId}.yml`);
}

/* 资源 id → 文件绝对路径（非法 id 返回 null） */
function getCustomFilePath(customId) {
  if (!isValidCustomId(customId)) return null;
  return path.join(getCustomsDir(), `${customId}.yml`);
}

/* 序列化资源为带注释的 YAML 文本 */
function toCustomYamlWithComments(c) {
  return [
    '# ============================================',
    '#  自定义资源文件（customs）',
    '#  由配置面板维护，请勿手动重命名/删除',
    '# ============================================',
    `id: ${yamlSingleQuote(c.id)}`,
    `fileName: ${yamlSingleQuote(c.fileName || '')}`,
    `mimeType: ${yamlSingleQuote(c.mimeType || '')}`,
    `purpose: ${yamlSingleQuote(c.purpose || '')}`,
    `duration: ${Number.isFinite(Number(c.duration)) ? Number(c.duration) : 0}`,
    `base64: ${yamlSingleQuote(c.base64 || '')}`,
    ''
  ].join('\n');
}

/* 清洗资源配置（类型校验 + 默认值填充） */
function normalizeCustom(input, fallbackId) {
  const source = (input && typeof input === 'object') ? input : {};
  return {
    id: (typeof source.id === 'string' && source.id) ? source.id : (fallbackId || ''),
    fileName: (typeof source.fileName === 'string' && source.fileName) ? source.fileName : 'custom',
    mimeType: (typeof source.mimeType === 'string' && source.mimeType) ? source.mimeType : 'application/octet-stream',
    purpose: (typeof source.purpose === 'string' && source.purpose) ? source.purpose : '',
    /* 音频时长（秒），非数字记 0；供 BGM 等音频资源使用 */
    duration: Number.isFinite(Number(source.duration)) ? Number(source.duration) : 0,
    base64: (typeof source.base64 === 'string') ? source.base64 : ''
  };
}

/*
 * 保存资源到磁盘（自动创建 customs 目录）。
 * 未提供 id 时自动生成；返回 { id, fileName, mimeType, purpose, base64 } 或 null。
 */
function saveCustom(input) {
  const normalized = normalizeCustom(input);
  if (!normalized.base64) return null;
  let id = normalized.id || generateCustomId();
  /* 碰撞保护：同一毫秒内重复创建则 +1ms 重试 */
  while (!isValidCustomId(id) || fs.existsSync(getCustomFilePath(id))) {
    id = generateCustomId();
  }
  normalized.id = id;
  fs.mkdirSync(getCustomsDir(), { recursive: true });
  fs.writeFileSync(getCustomFilePath(id), toCustomYamlWithComments(normalized), 'utf8');
  return normalized;
}

/* 读取资源（不存在/解析失败返回 null） */
function loadCustom(customId) {
  const filePath = getCustomFilePath(customId);
  if (!filePath || !fs.existsSync(filePath)) return null;
  try {
    const parsed = yaml.load(fs.readFileSync(filePath, 'utf8'));
    return normalizeCustom(parsed, customId);
  } catch (error) {
    console.error('[customs] 资源加载失败:', customId, error.message);
    return null;
  }
}

/* 列出全部自定义资源（按文件名倒序，最新的在前） */
function listCustoms() {
  const dir = getCustomsDir();
  if (!fs.existsSync(dir)) return [];
  const result = [];
  for (const file of fs.readdirSync(dir)) {
    if (!CUSTOM_FILE_RE.test(file)) continue;
    const id = path.basename(file, '.yml');
    const custom = loadCustom(id);
    if (custom) result.push(custom);
  }
  result.sort((a, b) => String(b.id).localeCompare(String(a.id)));
  return result;
}

/* 删除资源文件，返回是否成功 */
function deleteCustom(customId) {
  const filePath = getCustomFilePath(customId);
  if (!filePath || !fs.existsSync(filePath)) return false;
  fs.unlinkSync(filePath);
  return true;
}

/* 将资源拼为 data URL（data:<mimeType>;base64,<data>），失败返回 null */
function getCustomDataUrl(customId) {
  const custom = loadCustom(customId);
  if (!custom || !custom.mimeType || !custom.base64) return null;
  return `data:${custom.mimeType};base64,${custom.base64}`;
}

// ============================================================================
//  内存暂存（staged）：上传的资源先存内存，点击“应用”时才落盘。
//
//  生命周期：
//    上传 → stagedSaveCustom 存内存（返回 staged_custom_<时间戳> id）
//    应用 → save-config 中对引用的 staged 调 stagedPromote 落盘
//    关闭面板 / 应用完成 → stagedClear 清空（未应用的上传即被舍弃）
// ============================================================================

/* staged 前缀 */
const STAGED_PREFIX = 'staged_custom_';
/* 内存暂存表：stagedId → 资源数据 */
const stagedCustoms = new Map();

/* 将上传数据暂存到内存，返回 staged id（staged_custom_<时间戳>）；失败返回 null */
function stagedSaveCustom(input) {
  const normalized = normalizeCustom(input);
  if (!normalized.base64) return null;
  const stagedId = `${STAGED_PREFIX}${Date.now()}`;
  stagedCustoms.set(stagedId, normalized);
  return stagedId;
}

/* 读取暂存数据（staged id → 数据对象，id 替换为 staged id；不存在返回 null） */
function stagedGetCustom(stagedId) {
  const data = stagedCustoms.get(stagedId);
  if (!data) return null;
  return { ...data, id: stagedId };
}

/* 将暂存资源写入磁盘（落盘），返回真实资源对象；staged 项删除。失败返回 null */
function stagedPromote(stagedId) {
  const data = stagedCustoms.get(stagedId);
  if (!data) return null;
  const custom = saveCustom(data);
  if (custom) stagedCustoms.delete(stagedId);
  return custom;
}

/* 清空全部暂存（关闭配置面板 / 应用完成后调用） */
function stagedClear() {
  stagedCustoms.clear();
}

/*
 * 初始化（应用启动时调用）：
 *   1. 确保 customs 目录存在；
 *   2. 迁移：根 config.yml 里旧版浮动按钮内嵌图标（iconDataUrl data URL）
 *      迁移到 customs/ 目录，并写入 floatingButton.customIconId 引用；
 *   3. 校验 customIconId：指向不存在的资源时清空引用。
 */
function ensureInitialized() {
  try {
    fs.mkdirSync(getCustomsDir(), { recursive: true });
    const cfg = config.loadConfig();
    const fb = (cfg.floatingButton && typeof cfg.floatingButton === 'object') ? cfg.floatingButton : {};

    /* 迁移旧版内嵌图标：iconDataUrl 非空 且 尚无 customIconId 引用 */
    if (!fb.customIconId && typeof fb.iconDataUrl === 'string'
      && fb.iconDataUrl.startsWith('data:image/')) {
      const semicolon = fb.iconDataUrl.indexOf(';');
      const comma = fb.iconDataUrl.indexOf(',');
      const mime = semicolon > 5 ? fb.iconDataUrl.slice(5, semicolon) : '';
      const base64 = comma >= 0 ? fb.iconDataUrl.slice(comma + 1) : '';
      if (mime && base64) {
        const custom = saveCustom({
          fileName: 'legacy-icon',
          mimeType: mime,
          purpose: 'floating-icon',
          base64
        });
        if (custom) {
          config.saveConfig(config.normalizeConfig({
            ...cfg,
            floatingButton: { ...fb, customIconId: custom.id, iconDataUrl: '' }
          }));
          console.log('[customs] 旧版内嵌图标已迁移到 customs:', custom.id);
          return;
        }
      }
    }

    /* 校验 customIconId：指向不存在的资源 → 清空引用 */
    if (fb.customIconId && !loadCustom(fb.customIconId)) {
      config.saveConfig(config.normalizeConfig({
        ...cfg,
        floatingButton: { ...fb, customIconId: '' }
      }));
      console.log('[customs] customIconId 指向的资源不存在，已清空:', fb.customIconId);
    }
  } catch (error) {
    console.error('[customs] 初始化失败:', error.message);
  }
}

module.exports = {
  getCustomsDir,
  generateCustomId,
  isValidCustomId,
  saveCustom,
  loadCustom,
  listCustoms,
  deleteCustom,
  getCustomDataUrl,
  stagedSaveCustom,
  stagedGetCustom,
  stagedPromote,
  stagedClear,
  ensureInitialized
};
