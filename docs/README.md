# 蔚蓝点名 (Blue Random) 官方架构与开发文档库

欢迎查阅 **蔚蓝点名 (Blue Random)** 技术文档。本项目已全面重构为以 **单一 C# (.NET 10 / WPF) 宿主进程 + 原生多线程 UI + WebView2 混合前端** 为核心的现代化客户端架构。

---

## 一、 核心文档导航

本目录包含系统的深层架构设计规范与通信协议定义，请根据需要查阅：

| 文档名称 | 核心内容 | 适用受众 |
| :--- | :--- | :--- |
| 📘 **[程序整体架构设计说明书](./ARCHITECTURE.md)** | 重构背景与设计哲学、单进程 4 线程拓扑模型、双重内存缓存（Config Cache 与 Resource Cache）、纯领域抽卡算法 (LotteryEngine)、5 阶段原子启动与自愈流水线、原生 UI 实现原理及系统安全性。 | 架构师、核心开发人员、二次开发者 |
| 📡 **[跨线程通信拓扑与 IPC 协议规范](./COMMUNICATION_TOPOLOGY.md)** | 进程内 `@IPC` 异步无锁通信通道模型、四种动作语义（`INIT`, `DEST`, `GET`, `POST`）、各模块（`@IPC`, `@FCB`, `@FRW`, `@CP`）报文参数定义、`NativeBridge` 直连架构与全景业务交互时序图。 | UI 开发者、通信集成人员、调试维护人员 |

---

## 二、 核心技术栈总览

- **宿主环境**：.NET 10 (C# 13, Windows Desktop SDK)
- **原生 UI 表现层**：Windows Presentation Foundation (WPF)，支持 Per-Monitor DPI V2、原生半透明窗口与硬件加速合成
- **前端配置面板**：Vue 3 + Vite + `riz-ui` + FontAwesome，嵌入运行于 Microsoft WebView2 (Chromium Evergreen)
- **底层通信**：`System.Threading.Channels` 高性能异步无锁队列
- **系统托盘**：`H.NotifyIcon.Wpf` (纯托管 WPF 原生托盘解决方案)
- **数据序列化**：`YamlDotNet` (兼容旧版 YAML 格式并支持生成语义化中文注释)
- **单元测试**：`xUnit` + `Microsoft.NET.Test.Sdk`

---

## 三、 本地开发与构建指引

### 3.1 环境要求
- Windows 10 (1809+) 或 Windows 11
- .NET 10 SDK (或更高版本)
- Node.js 20+ 及 npm (推荐 Node.js 22 LTS)
- WebView2 运行时 (Win11 默认自带，Win10 自动静默拉取)

### 3.2 步骤一：构建前端产物
配置面板基于 WebUI 构建，修改前端代码后需编译为静态资源：
```powershell
# 安装前端依赖
npm install

# 编译 Vue 3 前端静态产物至 dist/ 目录
npm run build:vite
```

### 3.3 步骤二：构建与启动 C# 宿主应用
```powershell
# 编译整个解决方案
dotnet build src-csharp/BlueRandom.csproj

# 直接启动运行
dotnet run --project src-csharp/BlueRandom.csproj
```

### 3.4 步骤三：运行自动化单元测试
```powershell
# 执行加权抽卡算法与 YAML 归一化等单元测试
dotnet test tests/BlueRandom.Tests/BlueRandom.Tests.csproj
```

### 3.5 步骤四：生产发布打包
```powershell
# 单文件独立打包 (Single-File Standalone): 内置 .NET 运行时、UIAccess 原生库、WebUI 与字体资源，开箱即用单一可执行文件
dotnet publish src-csharp/BlueRandom.csproj -c Release -r win-x64 --self-contained true -o publish
```

### 3.6 VS Code 一键调试与任务指令
项目内置 `.vscode/launch.json` 与 `.vscode/tasks.json`：
- **F5 / 运行与调试面板**：
  - `调试: Blue Random (全量编译 + 启动调试)`：自动先编译前端，再编译后端，进入断点调试。
  - `调试: 仅编译后端并调试 (快速 F5)`：跳过前端直接调试后端。
  - `全量编译并输出可执行文件`：一键编译前端并发布单文件独立版至 `publish/BlueRandom.exe`。
  - `编译后端`：直接执行 `dotnet build`。
  - `编译前端`：直接执行 `npm run build:vite`。
- **快捷键**：
  - `Ctrl + Shift + B`：触发默认构建任务 `全量编译 (前端 + 后端)`。
  - `Ctrl + Shift + P` $\to$ `Tasks: Run Task`：选择执行前端构建、后端构建、发布打包或自动化单元测试。


---

## 四、 持续集成与自动化构建 (CI/CD)

项目通过 GitHub Actions 实现完整的持续集成管线：
- **开发版工作流**：`.github/workflows/build-windows-dev.yml`
  - 监听 `NEXT-Dev`、`neo-dev` 分支 push 与 PR。
  - 自动执行 Node.js 前端编译、.NET 10 单元测试，输出单一可执行文件 `BlueRandom-Dev-v*-win-x64.exe` 构件供内部测试。
- **正式发布工作流**：`.github/workflows/build-windows.yml`
  - 监听 `neo`、`NEXT`、`main` 分支及 `v*` 语义化版本 Tag。
  - 自动运行测试并发布单一可执行文件 `BlueRandom-v*-win-x64.exe` 的 GitHub Release。

