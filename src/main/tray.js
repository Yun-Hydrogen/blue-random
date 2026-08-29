/*
================================================================================
技术文档：src/main/tray.js
职责：创建系统托盘图标，绑定右键菜单。

================================================================================
概念说明
================================================================================
  Electron Tray：
    系统托盘（System Tray）是操作系统任务栏/菜单栏上的小图标区域。
    Windows 在右下角，macOS 在右上角菜单栏。
    本应用通过托盘实现"后台驻留"——关闭所有窗口后不退出，用户可通过
    托盘右键菜单重新打开配置面板或彻底退出应用。

  nativeImage.createFromPath(path)：
    从文件路径加载图标图片，自动适配不同 DPI（Windows 需要 .png 或 .ico）。
    本应用使用 tray.png（位于 public/image/）。

  tray.setToolTip(text)：
    鼠标悬停在托盘图标上时显示的文字提示。

  tray.setContextMenu(menu)：
    绑定右键菜单（由 tray-menu.js 的 buildTrayContextMenu 构建）。

================================================================================
路径解析（dev vs prod）
================================================================================
  开发模式（npm run dev）：
    图标位置：<项目根>/public/image/tray.png
    __dirname 指向 src/main/，往上两级到项目根，再进入 public/image/

  生产模式（打包后）：
    图标位置：<应用>/resources/app.asar 内的 dist/image/tray.png
    Vite 构建时将 public/ 内容复制到 dist/ 目录
    __dirname 指向 dist-electron/ 的打包后位置

================================================================================
维护建议
================================================================================
  - 更换托盘图标时替换 public/image/tray.png 即可
  - 托盘创建失败不会阻断应用启动（Electron 会静默降级为无托盘运行）
  - createTray 返回 Tray 实例，调用方可以持有引用以便后续操作
================================================================================
*/
const { Tray, nativeImage } = require('electron');
const path = require('path');
const { buildTrayContextMenu } = require('./tray-menu');

/*
 * createTray({ onOpenConfig, onRestart, onQuit }) —— 创建系统托盘。
 *
 * 参数：
 *   onOpenConfig — 点击"配置"菜单项的回调
 *   onRestart   — 点击"重启"菜单项的回调
 *   onQuit       — 点击"退出"菜单项的回调
 *
 * 返回：Electron Tray 实例
 *
 * 流程：
 *   1. 判断开发/生产模式，解析图标路径
 *   2. 加载图标为 nativeImage
 *   3. 创建 Tray 实例
 *   4. 设置悬停提示文字
 *   5. 构建并绑定右键菜单
 *
 * 调用位置：main.js 的 app.whenReady() 回调中，在所有权限检查通过后。
 */
function createTray({ onOpenConfig, onRestart, onQuit }) {
  /* 判断是否为开发模式（Vite 开发服务器在运行） */
  const isDev = !!process.env.VITE_DEV_SERVER_URL;

  /*
   * 图标路径解析。
   *   dev 模式：src/main/ → ../../public/image/tray.png（项目根目录）
   *   prod 模式：dist-electron/ → ../dist/image/tray.png（打包输出目录）
   */
  const trayIconPath = isDev
    ? path.join(__dirname, '../public/image/tray.png')
    : path.join(__dirname, '../dist/image/tray.png');

  /* 从文件加载图标（Electron 会自动处理 DPI 缩放） */
  const trayIcon = nativeImage.createFromPath(trayIconPath);

  /* 创建托盘实例 */
  const tray = new Tray(trayIcon);

  /* 鼠标悬停提示 */
  tray.setToolTip('Blue Random');

  /* 构建菜单（"配置" / "重启" / "退出"）并绑定 */
  const trayMenu = buildTrayContextMenu({
    onOpenConfig,
    onRestart,
    onQuit
  });
  tray.setContextMenu(trayMenu);

  console.log('[tray] 系统托盘已创建');
  return tray;
}

module.exports = {
  createTray
};
