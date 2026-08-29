/*
================================================================================
技术文档：src/main/update.js
职责：从 GitHub Releases 检查应用更新。

================================================================================
更新检查流程
================================================================================
  1. 读取本地版本号（app.getVersion()，来自 package.json）
  2. 请求 GitHub Releases API 获取最新 Release 信息
  3. 从 Release 的附件中找到 version.yml（远程版本描述文件）
  4. 解析 version.yml 获取远程版本号
  5. 如果有 commit 信息，通过 GitHub Commit API 获取提交消息
  6. 比较本地版本与远程版本 —— 返回 'update' / 'ok' / 'easter' / 'error'

================================================================================
API 端点说明
================================================================================
  GitHub Releases API：
    GET https://api.github.com/repos/Yun-Hydrogen/blue-random/releases/latest
    返回最新 Release 的 JSON（含 tag、assets 列表、html_url 等）

  GitHub Commit API：
    GET https://api.github.com/repos/Yun-Hydrogen/blue-random/commits/<sha>
    返回指定提交的详细信息（含 commit.message）

  注意：GitHub API 对未认证请求有频率限制（60次/小时/IP）。
        本应用每次启动检查一次，远低于限制。

================================================================================
版本比较规则（Semantic Versioning）
================================================================================
  格式：主版本.次版本.修订号（如 26.6.14）
  比较方式：逐段比较数字大小，忽略前导 'v' 字符。
  示例：
    compareVersion('1.2.3', '1.2.4') → -1（本地旧）
    compareVersion('2.0.0', '1.9.9') →  1（本地新）
    compareVersion('1.0.0', '1.0.0') →  0（相同）

================================================================================
返回值结构
================================================================================
  所有返回值均包含以下公共字段：
    ok            — 操作是否成功（网络请求失败时为 false）
    status        — 'update'（有新版本）/ 'ok'（已是最新）/ 'easter'（彩蛋）/ 'error'
    title         — 简短标题（用于 UI 显示）
    detail        — 详细说明（用于 UI 展示）
    releaseUrl    — GitHub Releases 页面链接
    localVersion  — 本地版本号
    remoteVersion — 远程版本号（仅 status != 'error'）
    debug         — 调试信息数组（API URL、状态码等）

================================================================================
维护建议
================================================================================
  - 更换仓库地址时修改 repoOwner/repoName 常量
  - version.yml 由 electron-builder 打包时自动生成，不要手动创建
  - GitHub API 可能因网络问题超时（约 5-10 秒），由 .catch() 兜底
  - 返回的 debug 数组可帮助排查 API 调用失败的具体环节
================================================================================
*/
const { app, net } = require('electron');

/*
 * parseVersionYaml(text) —— 解析简易 YAML 格式的 version.yml 文件。
 *
 * version.yml 示例内容：
 *   version: 26.6.14
 *   commit: abc123def456
 *
 * 解析方式：逐行匹配 "键: 值" 模式。
 * 这是一个极简 YAML 解析器，仅支持单层键值对（不支持嵌套/数组/多行）。
 *
 * 为什么不使用 js-yaml：
 *   version.yml 结构非常简单（2-3 个键值对），引入完整 YAML 解析器
 *   会显著增加打包体积。手动解析 5 行代码即可完成。
 */
function parseVersionYaml(text) {
  const lines = String(text || '').split(/\r?\n/);
  const data = {};
  lines.forEach((line) => {
    const match = line.match(/^\s*([a-zA-Z0-9_-]+)\s*:\s*"?([^\"]*)"?\s*$/);
    if (match) {
      /* match[1] = 键名（如 "version"），match[2] = 键值（如 "26.6.14"） */
      data[match[1]] = match[2];
    }
  });
  return data;
}

/*
 * normalizeVersion(value) —— 清洗版本号字符串。
 *
 * 去掉前后的空格，去掉开头的 'v' 或 'V' 字符。
 * 例如：'v26.6.14' → '26.6.14'，'  1.0.0 ' → '1.0.0'。
 */
function normalizeVersion(value) {
  return String(value || '').trim().replace(/^v/i, '');
}

/*
 * compareVersion(a, b) —— 语义化版本比较。
 *
 * 返回值：
 *    1  → a > b（a 更新）
 *   -1  → a < b（b 更新）
 *    0  → a == b（相同）
 *
 * 算法：
 *   1. 清洗版本号字符串
 *   2. 按 '.' 分割为数字数组（如 '26.6.14' → [26, 6, 14]）
 *   3. 从左到右逐段比较，第一个不同的段决定结果
 *   4. 如果所有段都相同，返回 0
 *   5. 较短版本缺失的段视为 0（如 '1.0' vs '1.0.0' → 相同）
 *
 * 注意：这不是严格的 SemVer 实现（不解析 prerelease/build 后缀），
 *        但对于本项目的 yy.mm.dd 格式版本号完全够用。
 */
function compareVersion(a, b) {
  const pa = normalizeVersion(a).split('.').map(n => parseInt(n, 10)).filter(n => Number.isFinite(n));
  const pb = normalizeVersion(b).split('.').map(n => parseInt(n, 10)).filter(n => Number.isFinite(n));
  const len = Math.max(pa.length, pb.length);
  for (let i = 0; i < len; i += 1) {
    const av = pa[i] || 0;
    const bv = pb[i] || 0;
    if (av > bv) return 1;
    if (av < bv) return -1;
  }
  return 0;
}

/*
 * fetchUrl(url, options?) —— 使用 Electron 内置网络模块发送 HTTP GET 请求。
 *
 * 为什么使用 electron.net 而非 axios/fetch：
 *   Electron 的 net 模块自动使用系统代理设置（Windows 代理配置、环境变量），
 *   在企业网络或 VPN 环境下更可靠。同时避免引入额外依赖。
 *
 * 参数：
 *   url     — 请求地址
 *   options — 可选配置对象，headers 字段会合并到默认请求头
 *
 * 返回：Promise，resolve 得到 { statusCode, headers, body }。
 *
 * 注意：
 *   - User-Agent 设置为 'Blue-Random'（GitHub API 要求有 UA）
 *   - Accept 默认使用 GitHub API v3 格式
 *   - body 是 Buffer 类型，调用方需 toString('utf8') 转换
 */
function fetchUrl(url, options = {}) {
  return new Promise((resolve, reject) => {
    const request = net.request({
      method: 'GET',
      url,
      headers: {
        'User-Agent': 'Blue-Random',
        'Accept': 'application/vnd.github+json',
        ...(options.headers || {})
      }
    });

    /* 收集响应数据块（Buffer 数组） */
    const chunks = [];
    request.on('response', (response) => {
      response.on('data', (chunk) => chunks.push(Buffer.from(chunk)));
      response.on('end', () => {
        const body = Buffer.concat(chunks);
        resolve({
          statusCode: response.statusCode || 0,
          headers: response.headers || {},
          body
        });
      });
    });

    request.on('error', reject);
    request.end();
  });
}

/*
 * checkUpdateFromMain() —— 主更新检查函数（异步）。
 *
 * 这是本模块的唯一对外接口，main.js 在启动时调用。
 *
 * 流程详解：
 *   1. GET Releases API → 获取最新 Release
 *   2. 在 Release 的 assets 中找到 version.yml
 *   3. GET version.yml → 解析远程版本号 + commit SHA
 *   4. GET Commit API → 获取提交消息（用于更新日志展示）
 *   5. compareVersion(local, remote) → 比较结果
 *
 * 特殊状态 'easter'：
 *   当本地版本 > 远程版本时触发（理论上不应出现，可能是开发中的自测版本）。
 *   这是一个彩蛋——UI 会显示"这是为什么呢？"和"来自未来的版本"。
 *
 * debug 数组：
 *   记录每个 API 请求的 URL 和中间状态，可用于排查网络问题。
 *   在返回对象的 debug 字段中携带。
 */
async function checkUpdateFromMain() {
  const repoOwner = 'Yun-Hydrogen';
  const repoName = 'blue-random';
  const debug = [];
  const localVersion = app.getVersion();
  console.log('[update] 开始检查更新, 本地版本=' + localVersion);
  const releaseApi = `https://api.github.com/repos/${repoOwner}/${repoName}/releases/latest`;
  debug.push(`GET ${releaseApi}`);

  /* ---- 第 1 步：获取最新 Release ---- */
  const releaseResp = await fetchUrl(releaseApi);
  if (releaseResp.statusCode < 200 || releaseResp.statusCode >= 300) {
    console.error('[update] Release API 请求失败, statusCode=' + releaseResp.statusCode);
    return {
      ok: false,
      status: 'error',
      title: '检查更新失败',
      detail: `Release 请求失败 (${releaseResp.statusCode})`,
      localVersion,
      debug
    };
  }

  /* ---- 第 2 步：解析 Release 数据，找到 version.yml ---- */
  const release = JSON.parse(releaseResp.body.toString('utf8'));
  const assets = Array.isArray(release.assets) ? release.assets : [];
  debug.push(`assets=${assets.length}`);
  const versionAsset = assets.find(asset => asset.name === 'version.yml')
    || assets.find(asset => String(asset.name || '').toLowerCase().endsWith('version.yml'));

  if (!versionAsset || !versionAsset.browser_download_url) {
    return {
      ok: false,
      status: 'error',
      title: '未找到版本描述文件',
      detail: '发布中缺少 version.yml，请稍后再试。',
      releaseUrl: release.html_url || `https://github.com/${repoOwner}/${repoName}/releases/latest`,
      localVersion,
      debug
    };
  }

  /* ---- 第 3 步：下载 version.yml，解析远程版本号 ---- */
  debug.push(`GET ${versionAsset.browser_download_url}`);
  const versionResp = await fetchUrl(versionAsset.browser_download_url, {
    headers: { Accept: 'text/plain' }
  });
  if (versionResp.statusCode < 200 || versionResp.statusCode >= 300) {
    return {
      ok: false,
      status: 'error',
      title: '检查更新失败',
      detail: `version.yml 下载失败 (${versionResp.statusCode})`,
      releaseUrl: release.html_url || `https://github.com/${repoOwner}/${repoName}/releases/latest`,
      localVersion,
      debug
    };
  }

  const remoteMeta = parseVersionYaml(versionResp.body.toString('utf8'));
  const remoteVersion = remoteMeta.version || '0.0.0';
  const remoteCommit = remoteMeta.commit || '';
  debug.push(`remoteVersion=${remoteVersion}`);

  /* ---- 第 4 步：获取提交信息（可选，用于更新日志） ---- */
  let commitMessage = '';
  let commitUrl = '';
  if (remoteCommit) {
    commitUrl = `https://github.com/${repoOwner}/${repoName}/commit/${remoteCommit}`;
    const commitApi = `https://api.github.com/repos/${repoOwner}/${repoName}/commits/${remoteCommit}`;
    debug.push(`GET ${commitApi}`);
    const commitResp = await fetchUrl(commitApi);
    if (commitResp.statusCode >= 200 && commitResp.statusCode < 300) {
      const commitJson = JSON.parse(commitResp.body.toString('utf8'));
      if (commitJson && commitJson.commit && commitJson.commit.message) {
        commitMessage = String(commitJson.commit.message).trim();
      }
    }
  }

  /* ---- 第 5 步：版本比较，返回结果 ---- */
  const compare = compareVersion(localVersion, remoteVersion);

  if (compare < 0) {
    /* 有更新 */
    console.log('[update] 发现新版本:', remoteVersion, '(当前:', localVersion + ')');
    return {
      ok: true,
      status: 'update',
      title: `发现新版本：${remoteVersion}`,
      detail: commitMessage ? `更新内容：\n${commitMessage}` : '有新版本可用。',
      commitUrl,
      releaseUrl: release.html_url || `https://github.com/${repoOwner}/${repoName}/releases/latest`,
      localVersion,
      remoteVersion,
      debug
    };
  }

  if (compare === 0) {
    /* 已是最新 */
    console.log('[update] 已是最新版本:', localVersion);
    return {
      ok: true,
      status: 'ok',
      title: `已是最新版本：${localVersion}`,
      detail: commitMessage ? `当前提交：\n${commitMessage}` : '无需更新。',
      commitUrl,
      releaseUrl: release.html_url || `https://github.com/${repoOwner}/${repoName}/releases/latest`,
      localVersion,
      remoteVersion,
      debug
    };
  }

  /* 本地版本 > 远程版本（彩蛋） */
  return {
    ok: true,
    status: 'easter',
    title: `这是为什么呢？本地版本为：${localVersion}`,
    detail: `远端版本为：${remoteVersion}，看起来你正在使用来自未来的版本...`,
    commitUrl,
    releaseUrl: release.html_url || `https://github.com/${repoOwner}/${repoName}/releases/latest`,
    localVersion,
    remoteVersion,
    debug
  };
}

module.exports = {
  checkUpdateFromMain
};
