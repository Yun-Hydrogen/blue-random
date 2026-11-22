# 项目开发与构建流程

本文档说明 **Blue Random（蔚蓝点名）** 的本地开发、构建、测试、调试与发布流程。

---

## 一、 环境要求

- **操作系统**: Windows 10 / Windows 11（x64）
- **.NET SDK**: .NET 10 SDK（`net10.0-windows`）
- **Node.js**: Node.js 22+（用于构建 `WebUI/` 前端）
- **WebView2 Runtime**: Windows 11 默认内置；Windows 10 需安装 Microsoft Edge WebView2 Runtime

---

## 二、 常用脚本与命令

根目录 [package.json](../package.json) 与 [Scripts/](../Scripts) 提供了统一入口：

| 命令 / 脚本 | 作用 | 说明 |
|---|---|---|
| `npm run dev` | 启动前端 Vite 开发服务器 | 监听 `http://localhost:5173`，配合 `VITE_DEV_SERVER_URL` 热调试配置面板 |
| `npm run build` / `npm run build:vite` | 构建 WebUI 前端 | 输出静态产物至 `WebUI/dist/` |
| `npm run build:backend` | 仅编译 .NET 后端 | 调用 `Scripts/build.ps1 -SkipWebUI` |
| `powershell -File Scripts/build.ps1` | 全量构建（前端 + 后端） | 先构建 `WebUI/dist/`，再编译 `Main/BlueRandom.csproj` |
| `npm test` / `powershell -File Scripts/test.ps1` | 构建前端并运行单元测试 | 执行 `Tests/BlueRandom.Tests/BlueRandom.Tests.csproj`（xUnit） |
| `npm run build:app` / `powershell -File Scripts/publish.ps1` | 发布单文件独立版 | 输出 `win-x64` 自包含单文件 `publish/BlueRandom.exe` |
| `npm run clean` / `powershell -File Scripts/clean.ps1` | 清理构建产物 | 删除 `Main/bin`、`Main/obj`、`Tests/.../bin|obj`、`WebUI/dist`、`publish` |

> **注意**: `Main/BlueRandom.csproj` 在编译期会将 `WebUI/dist/**/*`、`Resources/**/*`、`third_party/uiaccess.dll`、根 `package.json` 与 `changelog.md` 作为 `EmbeddedResource` 内嵌进程序集，因此修改前端代码后需重新构建 `WebUI/dist/` 再编译或测试后端。

---

## 三、 VS Code 调试与环境变量

- **VS Code 任务与启动配置**:
  - `.vscode/tasks.json` 定义了默认构建任务（`Scripts/publish.ps1`）与调试预编译任务（`Scripts/build.ps1`）。
  - `.vscode/launch.json` 提供 `调试模式`（`coreclr` 启动 `Main/bin/Debug/net10.0-windows/BlueRandom.dll`）。
- **常用调试环境变量**:
  - `VITE_DEV_SERVER_URL`: 指向 Vite 开发服务器（如 `http://localhost:5173/`），使配置面板加载实时热更新页面。
  - `BLUERANDOM_USER_DATA_DIR`: 覆盖默认用户数据目录（默认 `%UserProfile%\.blue random`），便于多实例或沙箱隔离调试。
  - `BLUERANDOM_DCOMP_FRW_DEMO=1`: 跳过完整启动流水线，直接独立运行 `DCompResultWindow` 演播窗进行视觉验证。
  - `BLUERANDOM_DCOMP_DUMP=1`: 在演播窗就绪与关闭时将帧导出至 `%TEMP%\br_frw_dump.png` 供离线排查。

---

## 四、 自动化测试

单元测试工程位于 `Tests/BlueRandom.Tests/`，通过 `InternalsVisibleTo` 访问 `Main` 内部类型，并在 `TestSettings.cs` 中禁用跨测试类并行执行以隔离静态状态与临时目录：

- **算法与调度**: `LotteryEngineTests.cs`、`IpcBrokerTests.cs`、`LastResultTriggerTests.cs`
- **配置与存储**: `YamlConfigServiceTests.cs`、`ClassManagerTests.cs`、`CustomsStoreTests.cs`
- **资源与系统**: `EmbeddedResourceTests.cs`、`WindowsSystemTests.cs`、`UpdateServiceTests.cs`
- **视图与几何**: `ArcGeometryTests.cs`、`FloatingButtonWindowTests.cs`、`LastResultPresenterTests.cs`、`LastResultWindowTests.cs`、`LrwVisualThemeTests.cs`

---

## 五、 版本管理与 CI/CD 发布

1. **单一版本源**: 版本号（`version`，格式 `YY.MM.PATCH`）与版本代号（`codename`）统一维护在仓库根目录 `package.json`，由后端 `AppVersion` 与前端 `TabLogs.vue` 共同读取；更新日志维护在根目录 `changelog.md`。
2. **CI 构建 (`.github/workflows/ci.yml`)**: 任意分支 `push` 或 `pull_request` 自动执行 `npm ci --prefix WebUI` → `npm --prefix WebUI run build` → `dotnet test` → `dotnet publish`（`win-x64` 单文件，统一输出 `BlueRandom.exe`），并上传 `BlueRandom-CI-Windows` 产物。
3. **正式发布 (`.github/workflows/release.yml`)**: 手动触发 `workflow_dispatch`，读取根 `package.json` 生成 Tag（`<codename>-v<version>`）与 Release 标题，附带 `changelog.md` 与统一命名的单文件可执行程序 `BlueRandom.exe` 创建 GitHub Release。
