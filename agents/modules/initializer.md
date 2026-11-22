# Initializer 模块说明文档

## 概述
`Initializer` 模块位于 `Main/Modules/Initializer`，是 Blue Random 应用程序的启动编排中枢与 Windows 底层系统特权服务中心。其主要职责包括执行六阶段启动流水线（`StartupPipeline`）、处理管理员权限与 UIAccess 权限提权判断、定位并提取嵌入式原生动态链接库 `uiaccess.dll`、以及基于 Windows 任务计划程序实现开机免 UAC 静默自启。

---

## 功能清单

### 1. 六阶段启动流水线 (`StartupPipeline.RunAsync`)
- **1. 标题**: `StartupPipeline.RunAsync`
- **2. 用途**: 调度应用程序从冷启动到完全进入主运行态的完整六阶段生命周期（Stage 0–5）：日志初始化、权限预检与提权、配置加载迁移与班级/自定义资源整理、全内存资源预热、启动 IPC 总线与各独立 STA UI 线程、最终淡出启动屏。
- **3. 参数**:
  - `splash` (`SplashWindow`): 当前展示的纯原生启动窗体实例，用于更新进度百分比和状态文案。
  - `args` (`string[]`): 进程启动时的原始命令行参数集合。
  - **返回值** (`Task<bool>`): 异步任务返回布尔值。`true` 表示流水线顺利完成，应用主循环进入常驻态；`false` 表示已触发管理员/UIAccess 外部重启或用户取消启动，当前进程应即刻退出。
- **4. 注意事项**:
  - Stage 1 如果需要提权，会启动新进程并调用 `Application.Current.Shutdown()` 返回 `false`，杜绝多进程冲突。
  - Stage 2 执行配置加载时会调用 `YamlConfigService.IsProtocolMigrationNeeded()` 进行配置协议自动升级，并调用 `ClassManagerService.EnsureInitialized` 与 `CustomsStore.ConsolidateAndBindLatestResources`；若 `config.yaml` 损坏则弹窗询问是否重置为默认配置。
  - Stage 3 必须在 UI 线程创建前完成 `ResourceCache.Preload`，确保内存静态字典装配完毕。

---

### 2. 管理员权限检查与提权 (`WindowsSystemService.HandleAdminElevation`)
- **1. 标题**: `WindowsSystemService.HandleAdminElevation`
- **2. 用途**: 检查当前进程是否具备 Windows 管理员权限。若未具备且用户配置要求提权，则通过 `runas` 动词唤起 UAC 授权弹窗重启本程序。
- **3. 参数**:
  - `args` (`string[]`): 当前进程启动参数，提权重启时会自动追加 `--elevatedadmin` 标记。
  - `requireAdmin` (`bool`): 是否需要管理员权限（来自 `config.yaml` 中 `admin.requireAdminOnLaunch` 或由于开启了 `uiAccessEnabled`）。
  - **返回值** (`bool`): `true` 表示已成功发起提权并启动了新进程（当前进程应当即刻退出）；`false` 表示已具备权限、不需要提权或用户拒绝后降级继续运行。
- **4. 注意事项**:
  - 内置了 `--elevatedadmin` 参数防死循环守卫：一旦检测到该标记但权限仍不足，会记录警告日志并降级以普通权限继续运行，绝不会无限循环弹窗。
  - UIAccess 特权依赖于管理员特权前置，因此当开启 UIAccess 时，此项检查会自动被激活。

---

### 3. UIAccess 特权置顶检查与提权 (`WindowsSystemService.HandleUiAccessElevation`)
- **1. 标题**: `WindowsSystemService.HandleUiAccessElevation`
- **2. 用途**: 突破标准 Windows 桌面窗口 Z-Order 限制。利用嵌入的 `uiaccess.dll`（`StartUIAccessProcess`）将本进程令牌赋予 `UIAccess=true`，使悬浮按钮与结果浮窗能够置顶于管理员权限软件、任务管理器及全屏窗口之上。
- **3. 参数**:
  - `args` (`string[]`): 当前进程启动参数，重启时会自动追加 `--elevateduia` 标记。
  - `requireUiAccess` (`bool`): 用户是否在配置中开启了 UIAccess 特权置顶。
  - **返回值** (`bool`): `true` 表示已通过 `uiaccess.dll` 拉起提权进程（当前进程应当即刻退出）；`false` 表示已具备特权、未开启该特性或提权失败跳过。
- **4. 注意事项**:
  - 依赖于第 1 步管理员权限到位，否则跳过提权并记录警告日志。
  - 内置 `--elevateduia` 防死循环守卫，避免重复拉起。

---

### 4. 定位与提取 UIAccess 原生动态库 (`WindowsSystemService.GetUiAccessDllPath` / `DoesUiAccessDllExist`)
- **1. 标题**: `WindowsSystemService.GetUiAccessDllPath` 与 `DoesUiAccessDllExist`
- **2. 用途**: 探测本地磁盘或从程序集清单资源 `BlueRandom.uiaccess.dll` 提取 `uiaccess.dll` 至 `~/.blue random/uiaccess.dll`，并通过静态构造函数注册的 `NativeLibrary.SetDllImportResolver` 供 P/Invoke 动态加载。
- **3. 参数**:
  - 无显式输入参数。
  - **返回值**: `GetUiAccessDllPath()` 返回 `uiaccess.dll` 的绝对路径；`DoesUiAccessDllExist()` 返回是否存在物理文件或内嵌资源。
- **4. 注意事项**:
  - 优先探测程序目录、工作目录及 `third_party/uiaccess.dll`（开发调试模式），其次检查 `~/.blue random/uiaccess.dll`，最后从单文件内嵌资源流式释放。

---

### 5. 创建免 UAC 开机自启任务 (`WindowsSystemService.CreateStartupTask`)
- **1. 标题**: `WindowsSystemService.CreateStartupTask`
- **2. 用途**: 通过 Windows 任务计划程序（`schtasks.exe`）创建或覆盖登录自启计划任务，支持以最高权限（`/rl highest`）静默启动，避免开机触发 UAC 提权弹窗。
- **3. 参数**:
  - `taskName` (`string`): 计划任务名称（为空时默认 `"Blue Random (Admin)"`）。
  - `exePath` (`string`): 待启动的可执行文件绝对路径（为空时自动取 `Environment.ProcessPath`）。
  - `asAdmin` (`bool`): 是否附加 `/rl highest` 以管理员最高权限运行。
  - **返回值** (`bool`): 注册成功返回 `true`，执行异常或权限不足返回 `false`。
- **4. 注意事项**:
  - 执行 `schtasks.exe /create /tn "..." /tr "..." /sc onlogon [/rl highest] /f`（5 秒超时），以最高权限创建时需在管理员上下文中执行方可生效。

---

### 6. Win32 原生置顶状态刷新 (`WindowsSystemService.RefreshTopMost`)
- **1. 标题**: `WindowsSystemService.RefreshTopMost`
- **2. 用途**: 封装 Win32 API `SetWindowPos` 与 `BringWindowToTop`，强制将目标窗口 HWND 设置为 `HWND_TOPMOST`，解除 Windows 系统因全屏切换或焦点争抢导致的窗口下沉。
- **3. 参数**:
  - `hWnd` (`IntPtr`): 目标窗口的 Win32 句柄。
  - **返回值** (`void`)。
- **4. 注意事项**:
  - 传参标志位为 `SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW | SWP_NOACTIVATE`，防止刷新置顶时抢夺用户正在输入或全屏放映 PPT 的焦点。
  - 悬浮球后台定时器（1 秒心跳）会定期触发此方法以实现抗全屏压制。

