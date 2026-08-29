/*
================================================================================
技术文档：src/main/security.js
职责：配置面板密码保护 —— SHA256 哈希存储于 <配置根目录>/.SHA256。

存储方案：
  - 密码不存明文，使用 Node crypto SHA256 哈希写入 <配置根目录>/.SHA256
  - .SHA256 文件存在且非空 → 密码保护已开启
  - 验证使用恒定时间比较（timingSafeEqual）防时序攻击

功能：
  isEnabled()            当前是否已开启密码保护
  setPassword(pwd)       设置/更改密码（写入 SHA256）
  verifyPassword(pwd)    验证密码
  disablePassword(pwd)   关闭密码保护（需验证当前密码）
  resetAllConfig()       忘记密码：清除所有配置（密码 + config.yml + classes/ + customs/）

维护建议：
  - 不要在前端记录明文密码；渲染层仅中转用户输入
  - 修改哈希算法时需兼容旧哈希（当前仅 SHA256 hex）
================================================================================
*/
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const config = require('./config');

/* 密码哈希文件名（位于配置根目录下） */
const SECURITY_FILE_NAME = '.SHA256';

/* .SHA256 文件绝对路径：<配置根目录>/.SHA256 */
function getSecurityFile() {
  return path.join(config.getConfigDir(), SECURITY_FILE_NAME);
}

/* 当前是否已开启密码保护（哈希文件存在且非空） */
function isEnabled() {
  try {
    const file = getSecurityFile();
    return fs.existsSync(file) && fs.readFileSync(file, 'utf8').trim().length > 0;
  } catch (_) {
    return false;
  }
}

/* 计算 SHA256 十六进制摘要 */
function sha256(text) {
  return crypto.createHash('sha256').update(String(text || ''), 'utf8').digest('hex');
}

/* 设置/更改密码（写入 SHA256）；返回 { ok, message? } */
function setPassword(password) {
  const pwd = String(password || '');
  if (pwd.length < 4) {
    return { ok: false, message: '密码至少 4 位' };
  }
  try {
    fs.writeFileSync(getSecurityFile(), sha256(pwd), 'utf8');
    return { ok: true, message: '密码已设置' };
  } catch (error) {
    return { ok: false, message: '写入密码失败: ' + error.message };
  }
}

/* 验证密码（恒定时间比较）；未开启保护时视为通过 */
function verifyPassword(password) {
  if (!isEnabled()) return { ok: true };
  try {
    const stored = fs.readFileSync(getSecurityFile(), 'utf8').trim();
    const a = Buffer.from(stored, 'hex');
    const b = Buffer.from(sha256(String(password || '')), 'hex');
    const ok = a.length === b.length && crypto.timingSafeEqual(a, b);
    return ok ? { ok: true } : { ok: false, message: '密码错误' };
  } catch (_) {
    return { ok: false, message: '密码校验失败' };
  }
}

/* 关闭密码保护（需验证当前密码）；返回 { ok, message? } */
function disablePassword(password) {
  const v = verifyPassword(password);
  if (!v.ok) return v;
  try {
    fs.unlinkSync(getSecurityFile());
  } catch (_) { /* 文件不存在也视为已关闭 */ }
  return { ok: true, message: '密码保护已关闭' };
}

/*
 * 忘记密码：清除所有配置
 *   1. 删除 .SHA256（解除锁定）
 *   2. 重置 config.yml 为默认
 *   3. 清空 classes/（班级）与 customs/（自定义资源）目录
 * 返回 { ok, message }
 */
function resetAllConfig() {
  try { fs.unlinkSync(getSecurityFile()); } catch (_) { /* 忽略 */ }
  config.saveConfig(config.normalizeConfig({}));
  for (const dirName of ['classes', 'customs']) {
    const dir = path.join(config.getConfigDir(), dirName);
    try {
      if (fs.existsSync(dir)) fs.rmSync(dir, { recursive: true, force: true });
    } catch (error) {
      console.error('[security] 清理目录失败:', dirName, error.message);
    }
  }
  return { ok: true, message: '已清除所有配置（密码、名单、班级、自定义资源）' };
}

module.exports = {
  getSecurityFile,
  isEnabled,
  setPassword,
  verifyPassword,
  disablePassword,
  resetAllConfig
};
