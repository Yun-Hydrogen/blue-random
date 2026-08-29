/*
================================================================================
技术文档：src/main/classes.js
职责：多班级名单存储 —— 每个班级一份配置文件。

目录结构：
  <配置根目录>/
    config.yml          —— 全局设置（含 activeClassId 当前班级 id）
    classes/
      classes_<id>.yml  —— 每个班级一份（名单 + 权重 + 名称）
        文件名：classes_<以当前时间为种子的随机编号>.yml（如 classes_482913457.yml）

班级文件格式（YAML）：
  classId: 'classes_482913457'
  name: '高一(3)班'
  studentList:
    - name: "张三"
      weight: 1.0

保存策略：
  - 班级文件的「名单/权重」内容仅在用户手动触发保存时写入（配置面板“应用”）；
  - 班级的 添加 / 重命名 / 删除 / 刷新 属于结构操作，立即落盘；
  - 当前班级 id（activeClassId）保存在根 config.yml，同样随手动保存落盘。

维护建议：
  - 修改班级文件格式时同步更新 normalizeClass / toClassYamlWithComments
  - classId 生成使用以 Date.now() 为种子的 xorshift，碰撞概率极低（仍做存在性检查）
  - 学生新增 letterColor（信封颜色：rainbow/gold/blue，缺省 rainbow，旧配置读取时自动升级）
================================================================================
*/
const fs = require('fs');
const path = require('path');
const yaml = require('js-yaml');
const config = require('./config');

/* 班级文件前缀与文件名规则 */
const CLASS_FILE_PREFIX = 'classes_';
const CLASS_FILE_RE = /^classes_[0-9A-Za-z_\-]+\.yml$/i;

/* 信封颜色取值：rainbow（彩，默认）/ gold（黄）/ blue（蓝） */
const LETTER_COLORS = ['rainbow', 'gold', 'blue'];
const normalizeLetterColor = (v) => (LETTER_COLORS.includes(v) ? v : 'rainbow');

/* YAML 单引号转义（与 config.js 一致） */
const yamlSingleQuote = (value) => `'${String(value || '').replace(/'/g, "''")}'`;

/* 班级文件所在目录：<配置根目录>/classes */
function getClassesDir() {
  return path.join(config.getConfigDir(), 'classes');
}

/*
 * 生成班级 id：classes_<以当前时间为种子的随机编号>
 * xorshift32 打散 Date.now()，取 9 位数字。
 */
function generateClassId() {
  let s = Date.now();
  s ^= s << 13; s >>>= 0;
  s ^= s >>> 17; s >>>= 0;
  s ^= s << 5; s >>>= 0;
  const n = s % 1000000000;
  return `${CLASS_FILE_PREFIX}${String(n).padStart(9, '0')}`;
}

/* 校验 classId 是否合法（仅允许 classes_ + 字母数字下划线） */
function isValidClassId(classId) {
  return typeof classId === 'string' && CLASS_FILE_RE.test(`${classId}.yml`);
}

/* 班级 id → 文件绝对路径（非法 id 返回 null） */
function getClassFilePath(classId) {
  if (!isValidClassId(classId)) return null;
  return path.join(getClassesDir(), `${classId}.yml`);
}

/* 序列化班级配置为带注释的 YAML 文本 */
function toClassYamlWithComments(c) {
  const studentLines = Array.isArray(c.studentList) && c.studentList.length > 0
    ? '\n' + c.studentList.map(s => `  - name: "${s.name}"\n    weight: ${s.weight}\n    letterColor: ${s.letterColor || 'rainbow'}`).join('\n')
    : ' []';
  return [
    '# ============================================',
    '#  班级配置文件（多班级名单与权重）',
    '#  由配置面板「班级管理」维护，请勿手动重命名/删除',
    '# ============================================',
    `classId: ${yamlSingleQuote(c.classId)}`,
    `name: ${yamlSingleQuote(c.name)}`,
    `studentList:${studentLines}`,
    ''
  ].join('\n');
}

/* 清洗班级配置（类型校验 + 默认值填充） */
function normalizeClass(input, fallbackId) {
  const source = (input && typeof input === 'object') ? input : {};
  const studentList = Array.isArray(source.studentList)
    ? source.studentList.map(s => {
        if (typeof s === 'string') return { name: s.trim(), weight: 1.0, letterColor: 'rainbow' };
        if (s && typeof s === 'object') {
          return {
            name: String(s.name || '').trim(),
            weight: Number.isFinite(Number(s.weight)) ? Number(s.weight) : 1.0,
            letterColor: normalizeLetterColor(s.letterColor)
          };
        }
        return null;
      }).filter(s => s && s.name)
    : [];
  return {
    classId: (typeof source.classId === 'string' && source.classId) ? source.classId : (fallbackId || ''),
    name: (typeof source.name === 'string' && source.name.trim()) ? source.name.trim() : '未命名班级',
    studentList
  };
}

/* 保存班级配置到磁盘（自动创建 classes 目录） */
function saveClass(cls) {
  const normalized = normalizeClass(cls);
  const filePath = getClassFilePath(normalized.classId);
  if (!filePath) return false;
  fs.mkdirSync(getClassesDir(), { recursive: true });
  fs.writeFileSync(filePath, toClassYamlWithComments(normalized), 'utf8');
  return true;
}

/* 读取班级配置（不存在/解析失败返回 null） */
function loadClass(classId) {
  const filePath = getClassFilePath(classId);
  if (!filePath || !fs.existsSync(filePath)) return null;
  try {
    const parsed = yaml.load(fs.readFileSync(filePath, 'utf8'));
    return normalizeClass(parsed, classId);
  } catch (error) {
    console.error('[classes] 班级配置加载失败:', classId, error.message);
    return null;
  }
}

/* 列出全部班级（按名称排序） */
function listClasses() {
  const dir = getClassesDir();
  if (!fs.existsSync(dir)) return [];
  const result = [];
  for (const file of fs.readdirSync(dir)) {
    if (!CLASS_FILE_RE.test(file)) continue;
    const classId = path.basename(file, '.yml');
    const cls = loadClass(classId);
    if (cls) result.push(cls);
  }
  result.sort((a, b) => a.name.localeCompare(b.name, 'zh'));
  return result;
}

/* 创建班级（空名单），返回班级对象 */
function createClass(name) {
  let classId = generateClassId();
  /* 碰撞保护：理论上不会发生，仍循环重试 */
  while (fs.existsSync(getClassFilePath(classId))) {
    classId = generateClassId();
  }
  const cls = { classId, name: (name || '').trim() || '未命名班级', studentList: [] };
  saveClass(cls);
  return cls;
}

/* 重命名班级，返回是否成功 */
function renameClass(classId, name) {
  const cls = loadClass(classId);
  if (!cls) return false;
  cls.name = (name || '').trim() || '未命名班级';
  return saveClass(cls);
}

/* 删除班级文件，返回是否成功 */
function deleteClass(classId) {
  const filePath = getClassFilePath(classId);
  if (!filePath || !fs.existsSync(filePath)) return false;
  fs.unlinkSync(filePath);
  return true;
}

/* 读取根配置中的当前班级 id */
function getActiveClassId() {
  return config.loadConfig().activeClassId || '';
}

/* 读取当前班级的完整配置（无则 null） */
function getActiveClass() {
  const id = getActiveClassId();
  return id ? loadClass(id) : null;
}

/*
 * 初始化（应用启动时调用）：
 *   1. 确保 classes 目录存在；
 *   2. 迁移：根 config.yml 里旧版 studentList 非空且无任何班级文件时，
 *      生成“默认班级”承载旧名单；
 *   3. 校验 activeClassId：指向不存在的班级时回退到第一个班级（或清空）。
 */
function ensureInitialized() {
  try {
    fs.mkdirSync(getClassesDir(), { recursive: true });
    const classes = listClasses();
    const cfg = config.loadConfig();

    const hasLegacyRoster = Array.isArray(cfg.studentList) && cfg.studentList.length > 0;
    if (classes.length === 0 && hasLegacyRoster) {
      const cls = { classId: generateClassId(), name: '默认班级', studentList: cfg.studentList };
      saveClass(cls);
      config.saveConfig(config.normalizeConfig({ ...cfg, activeClassId: cls.classId }));
      console.log('[classes] 已从旧配置迁移名单到默认班级: ' + cls.classId);
      return;
    }

    const activeId = cfg.activeClassId || '';
    if (activeId && !classes.some(c => c.classId === activeId)) {
      const next = classes.length > 0 ? classes[0].classId : '';
      config.saveConfig(config.normalizeConfig({ ...cfg, activeClassId: next }));
      console.log('[classes] activeClassId 无效，回退到: ' + (next || '(无)'));
    }
  } catch (error) {
    console.error('[classes] 初始化失败:', error.message);
  }
}

module.exports = {
  createClass,
  deleteClass,
  ensureInitialized,
  generateClassId,
  getActiveClass,
  getActiveClassId,
  getClassesDir,
  listClasses,
  loadClass,
  normalizeClass,
  renameClass,
  saveClass
};
