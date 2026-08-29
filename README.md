<center>
<img src='/public/image/BlueRandom.png'>

# Blue Random | 蔚蓝点名
</center>

------

## 项目简介 ✨

蔚蓝点名 是一款基于 **Electron + Vue 3 + Vite** 的桌面随机点名工具，灵感来源于 **《蔚蓝档案(Blue Archive)》** 的学生招募动画。悬浮按钮常驻桌面，一键触发抽取动画，有趣的招募动画为枯燥的课堂教学添加乐趣。

## 功能特性 🎯
- 🪟 悬浮按钮快速抽取
- 👥 1-10抽自定义配置
- ✉️ ~~不那么~~仿《蔚蓝档案》的抽奖动画
- 📋 快捷名单与权重管理
- 🔁 允许/禁止重复抽取开关
- ⚙️ 现代化UI的配置面板(Powered by [RizUI](https://github.com/Yun-Hydrogen/riz-ui))
- 🔝 UIAccess 置顶增强

## 快速开箱 📦

- 前往 [GitHub Releases](https://github.com/Yun-Hydrogen/blue-random/releases) 下载最新正式版，或在 [GitHub Actions](https://github.com/Yun-Hydrogen/blue-random/actions) 获取开发构建。
- 解压后运行可执行文件，在系统托盘找到 Blue Random 图标。
- 右键托盘图标 → **配置**，进入配置面板。
- 在"名单管理"Tab 导入学生名单（txt/csv 或手动输入），调整权重。
- 在"悬浮按钮"Tab 自定义按钮外观、图标和边框颜色。
- 在"结果浮窗"Tab 设置抽取动画的外观；上传自定义**抽取音效**与**背景音乐**。
- 在"高级设置"Tab 管理开机自启、渲染后端等选项。
- 一切就绪，开始使用~~抽卡~~吧！

## For Dev - 项目结构 📁

```
├── public/                   静态资源（图片、音效、字体）
│   ├── image/                Logo、装饰角色、默认图标
│   ├── sound/                音效
│   └── fonts/                字体文件
├── src/
│   ├── main/                 Electron 主进程
│   │   ├── main.js           应用入口、渲染后端配置
│   │   ├── windows.js        窗口创建、setShape、淡入淡出动画
│   │   ├── config.js         配置读写、校验、YAML 序列化
│   │   ├── ipc.js            IPC 通道注册（28+ 通道）
│   │   ├── admin.js          Windows 权限模块（管理员/UIAccess）
│   │   ├── tray.js           托盘图标与菜单
│   │   ├── update.js         自动更新检查
│   │   ├── config-server.js  Web 配置服务器
│   │   └── logging.js        日志记录
│   ├── preload/
│   │   └── preload.js        contextBridge IPC 桥接 API
│   └── renderer/             前端渲染层
│       ├── main.js           Vue 应用入口
│       ├── style.css         全局样式
│       ├── router/           Hash 路由
│       ├── composables/      组合式逻辑（useConfigPanel）
│       └── views/            页面视图
│           ├── ConfigPanel.vue   配置面板（Tab 导航 + 全局状态）
│           ├── Floating.vue      悬浮按钮窗口
│           ├── PickResult.vue    抽取结果动画窗口
│           ├── TabRoster.vue     名单管理 Tab
│           ├── TabFloating.vue   悬浮按钮配置 Tab
│           ├── TabResult.vue     结果浮窗配置 Tab
│           ├── TabAdvanced.vue   高级设置 Tab
│           ├── TabAbout.vue      关于应用 Tab
│           └── TabLogs.vue       运行日志面板
├── build/                    构建脚本
├── third_party/              第三方依赖
├── package.json
├── vite.config.js
└── README.md
```

## 贡献 🤝

欢迎提交 Issue 和 PR！
- **Bug 或建议**：请先在 Issue 中描述复现步骤与期望行为
- **新功能**：建议先开 Issue 讨论方向与实现
- **代码提交**：保持风格一致，必要时补充截图/录屏


## 说明 🧠

- **本项目大部分由 AI 生成与改写。作者对 Electron 与 Vue 开发几乎不熟悉，如有不完善之处，欢迎指正与贡献改进。**
- **本项目采用了部分第三方美术资源，项目作者并没有获得授权，如造成侵权请联系删除。**

## 许可证 📄

- 除项目 **/public/** 下的图片和音效资源，项目使用 **AGPLv3** 许可证。
- 项目 **/public/** 下的图片和音效资源由各自版权方所有，使用时请注意授权和范围。
- 背景音乐与抽取音效为版权资源，**不随仓库分发**；请用户自行准备，上传后存储于本地 customs/ 目录。
- UiAccess.dll 来自 [RunUIAccess](https://github.com/shc0743/RunUIAccess)，使用 MIT 协议 [License](/THIRD_PARTY_NOTICES/RunUIAccess-MIT.txt)

## 第三方资源版权归属

```
public
  ├─fonts
  │    ├─UI.ttf 南西新圆体 IPA Font License
  │      
  ├─image
  │   ├─app.ico 
  │   ├─Arona_Empty.png   Blue Archive 游戏内UI资源 版权归其所有方
  │   ├─Arona_Plana.png   AI 合成
  │   ├─BlueRandom.png
  │   ├─Letter_Rainbow.png        Blue Archive 游戏内UI资源 版权归其所有方
  │   ├─Letter_Gold.png        Blue Archive 游戏内UI资源 版权归其所有方
  │   ├─Letter_Blue.png        Blue Archive 游戏内UI资源 版权归其所有方
  │   ├─tray.png
  │      
  └─sound
      ├─button_click.wav  Blue Archive 游戏内音效 版权归其所有方

                            
```
非常感谢这些资源的作者们！部分资源可能有些疏漏未在上方体现，欢迎提交Issue改正！

## 感谢 💕

- 《蔚蓝档案(Blue Archive)》游戏提供的灵感：
  [国服 《蔚蓝档案》](https://bluearchive-cn.com/)
  [国际服 Blue Archive](https://bluearchive.nexon.com/home)

- 抽取背景音乐 **KARUT** 的 **《Connected Sky》**

- [RunUIAccess](https://github.com/shc0743/RunUIAccess)
- [RizUI](https://github.com/Yun-Hydrogen/riz-ui)

---
Code with 💗 and AI by HydrogenRua-萌氢P | 惠州一中算法AI社&智能信息社