/*
================================================================================
技术文档：src/main/tray-menu.js
职责：定义系统托盘图标的右键菜单结构。

================================================================================
概念说明
================================================================================
  Electron Menu：
    系统托盘菜单是操作系统的原生右键菜单（Windows 任务栏右下角、
    macOS 菜单栏右侧）。通过 Electron 的 Menu.buildFromTemplate() API
    用 JSON 模板构建，无需写 HTML/CSS。

  Menu.buildFromTemplate(template)：
    接收一个菜单项数组，返回可绑定到 Tray 实例的 Menu 对象。
    每个菜单项是一个对象，包含：
      label — 显示的文本
      click — 点击时执行的回调函数

================================================================================
核心函数
================================================================================
  buildTrayContextMenu({ onOpenConfig, onQuit })
    参数：
      onOpenConfig — 点击"配置"时的回调（通常打开配置面板窗口）
      onQuit       — 点击"退出"时的回调（通常调用 app.quit()）
    返回：Electron Menu 实例，可直接传给 tray.setContextMenu()

================================================================================
维护建议
================================================================================
  - 新增菜单项只需在模板数组中添加 { label, click } 对象
  - 菜单项顺序即数组中的顺序（配置 → 重启 → 退出）
  - 回调通过参数注入而非硬编码，保持本模块与 main.js 解耦
================================================================================
*/
const { Menu } = require('electron');

/*
 * 构建托盘右键菜单。
 *
 * 当前菜单结构：
 *   +------------+
 *   |  配置       | → 点击 → onOpenConfig() → 打开配置面板
 *   |  重启       | → 点击 → onRestart()    → 以当前状态重启应用
 *   |  退出       | → 点击 → onQuit()       → 关闭应用
 *   +------------+
 *
 * 防御性编程：
 *   每个 click 回调中先检查对应的函数参数是否为 function 类型，
 *   防止调用方传入非法值导致应用崩溃。
 */
function buildTrayContextMenu({ onOpenConfig, onRestart, onQuit }) {
  return Menu.buildFromTemplate([
    {
      label: '配置',
      click: () => {
        if (typeof onOpenConfig === 'function') {
          onOpenConfig();
        }
      }
    },
    {
      label: '重启',
      click: () => {
        if (typeof onRestart === 'function') {
          onRestart();
        }
      }
    },
    {
      label: '退出',
      click: () => {
        if (typeof onQuit === 'function') {
          onQuit();
        }
      }
    }
  ]);
}

module.exports = {
  buildTrayContextMenu
};
