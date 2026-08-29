/*
技术文档：build/prepare-uiaccess.js
职责：打包前将 RunUIAccess 的 uiaccess.dll 内联进主进程源码。

核心功能：
- 在项目根目录/vendor/third_party 中查找 uiaccess.dll；
- 若 third_party 缺失则自动复制到 third_party（内联源）；
- 将 dll 以 base64 写入 src/main/uiaccess-inline.js，
  该模块会被 vite-plugin-electron 编译进 dist-electron/main.js，
  打包后位于 app.asar 内 —— 即“编译到程序里面”，不再随 exe 附带独立 dll 文件。
*/
const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..');
const thirdPartyDir = path.join(root, 'third_party');
const target = path.join(thirdPartyDir, 'uiaccess.dll');
const candidates = [
  path.join(root, 'uiaccess.dll'),
  path.join(root, 'vendor', 'uiaccess.dll')
];

/* ---- 第 1 步：确保 third_party/uiaccess.dll 存在（内联源） ---- */
if (!fs.existsSync(target)) {
  const source = candidates.find((candidate) => fs.existsSync(candidate));
  if (!source) {
    console.warn('[uiaccess] uiaccess.dll not found. Place it in third_party/, vendor/, or project root.');
    process.exit(0);
  }
  fs.mkdirSync(thirdPartyDir, { recursive: true });
  fs.copyFileSync(source, target);
  console.log(`[uiaccess] Copied uiaccess.dll to third_party from ${source}`);
} else {
  console.log('[uiaccess] uiaccess.dll already present in third_party.');
}

/* ---- 第 2 步：内联为 base64 模块（编译进主进程包） ---- */
const base64 = fs.readFileSync(target).toString('base64');
const outPath = path.join(root, 'src', 'main', 'uiaccess-inline.js');
const header = '/* 自动生成：由 build/prepare-uiaccess.js 从 third_party/uiaccess.dll 生成，请勿手动编辑 */\n';
fs.writeFileSync(outPath, `${header}module.exports = '${base64}';\n`);
console.log(`[uiaccess] Inlined uiaccess.dll -> src/main/uiaccess-inline.js (${(base64.length / 1024).toFixed(1)} KB base64)`);
