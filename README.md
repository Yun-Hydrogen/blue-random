<center>
<img src='/public/image/BlueRandom.png'>

# Blue Random | 蔚蓝点名
</center>

------

## 项目简介 ✨

蔚蓝点名 是一款基于 **.NET 10 (C# / WPF) + WebView2 (Vue 3 + Vite)** 的现代化桌面随机点名工具，灵感来源于 **《蔚蓝档案(Blue Archive)》** 的学生招募动画。悬浮按钮常驻桌面，一键触发抽取动画，有趣的招募动画为枯燥的课堂教学添加乐趣。

## 功能特性 🎯
- 🪟 原生 WPF 悬浮按钮快速抽取（支持 1~10 抽动态胶囊、无感点击穿透与边缘物理吸附）
- ✉️ 原汁原味仿《蔚蓝档案》的招募演播（15°倾斜入场、彩/金/蓝信封撕开、姓名卡揭晓）
- 👥 纯领域加权无偏抽卡引擎（支持轮盘赌与 Efraimidis-Spirakis 加权无放回抽样）
- 📋 快捷多班级名单与权重管理
- 🔁 允许/禁止重复抽取开关
- ⚙️ 现代化 UI 配置面板 (Powered by [RizUI](https://github.com/Yun-Hydrogen/riz-ui) & WebView2)
- 🔝 UIAccess 置顶增强与任务计划静默自启
- 🔒 SHA-256 安全密码锁，防止学生误改配置

## 快速开箱 📦

- 前往 [GitHub Releases](https://github.com/Yun-Hydrogen/blue-random/releases) 下载最新正式版，或在 [GitHub Actions](https://github.com/Yun-Hydrogen/blue-random/actions) 获取开发构建。
- 解压后运行可执行文件，在系统托盘找到 Blue Random 图标。
- 右键托盘图标 → **配置**，进入配置面板。
- 在"名单管理"Tab 导入学生名单（txt/csv 或手动输入），调整权重与班级。
- 在"悬浮按钮"Tab 自定义按钮外观、图标和边框颜色。
- 在"结果浮窗"Tab 设置抽取动画的外观；上传自定义**抽取音效**与**背景音乐**。
- 在"高级设置"Tab 管理开机自启、置顶权限、管理员密码保护等选项。
- 一切就绪，开始使用~~抽卡~~吧！

## For Dev - 项目结构 📁

```
├── docs/                     系统架构与跨线程 IPC 通信协议设计文档
├── public/                   静态资源（图片、音效、字体）
│   ├── image/                Logo、立绘装饰、默认图标
│   ├── sound/                音效
│   └── fonts/                UI.ttf 字体文件
├── src-csharp/               .NET 10 C# 宿主与原生 WPF UI
│   ├── Core/                 核心领域逻辑 (配置缓存、无锁IPC通道、抽卡算法、调度器)
│   ├── Services/             班级管理、安全凭据、原生托盘、系统特权与窗口调度
│   └── UI/                   各独立 STA 线程窗口 (FCB 悬浮球、FRW 演播窗、CP 配置面板宿主、Splash)
├── src/renderer/             WebUI 前端管理后台 (Vue 3 + Vite + riz-ui)
│   ├── bridge/               nativeBridgeAdapter (WebView2 宿主直连适配)
│   ├── composables/          配置面板状态机与业务逻辑
│   ├── views/                各配置选项卡页面
│   └── main.js               Vue 应用入口
├── tests/                    xUnit 自动化单元测试套件
├── third_party/              第三方依赖 (RunUIAccess 等)
├── package.json              前端依赖配置
├── vite.config.js            Vite 构建配置
└── BlueRandom.sln            .NET 解决方案
```

## 贡献 🤝

欢迎提交 Issue 和 PR！
- **Bug 或建议**：请先在 Issue 中描述复现步骤与期望行为
- **新功能**：建议先开 Issue 讨论方向与实现
- **代码提交**：保持风格一致，必要时补充截图/录屏


## 说明 🧠

- **本项目已全面重构为单一 C# (.NET 10 / WPF) 宿主 + 原生多线程 UI + WebView2 架构，深度解耦，如有不完善之处，欢迎指正与贡献改进。**
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