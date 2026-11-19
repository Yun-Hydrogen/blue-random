# Initializer 模块说明文档

## 概述
`Initializer` 模块位于 `Main/Modules/Initializer`，是 Blue Random 应用程序的启动编排中枢与 Windows 底层系统特权服务中心。其主要职责包括执行六阶段原子启动流水线（`StartupPipeline`）、处理管理员权限与 UIAccess 权限提权判断、提取并调用嵌入式原生动态链接库 `uiaccess.dll`、以及基于 Windows 任务计划程序实现开机免 UAC 静默自启。

---

## 功能清单

### 1. 六阶段启动流水线 (`StartupPipeline.RunAsync`)
- **1. 标题**: `StartupPipeline.RunAsync`
- **2. 用途**: 调度应用程序从冷启动到完全进入主运行态的完整六阶段生命周期。包括前置权限预检、配置加载迁移、内存资源预热、拉起各独立 STA UI 线程、最终淡出启动屏。
- **3. 参数**:
  - `splash` (`SplashWindow`): 当前展示的纯原生启动窗体实例，用于更新进度百分比和状态文案。
  - `args` (`string[]`): 进程启动时的原始命令行参数集合。
  - **返回值** (`Task<bool>`): 异步任务返回布尔值。`true` 表示流水线顺利完成，应用主循环进入常驻态；`false` 表示已触发管理员/UIAccess 外部重启或启动异常，主进程应即刻退出。
- **4. 注意事项**:
  - 启动阶段 0 和 1 如果需要提权，会通过启动新进程并返回 `false`，此时当前启动进程必须立即退出，杜绝多进程冲突。
  - 阶段 2 执行配置加载时会调用 `YamlConfigService.IsProtocolMigrationNeeded()` 进行配置协议自动升级。
  - 阶段 3 必须在 UI 线程创建前完成 `ResourceCache.Preload`，确保内存静态字典装配完毕。

---

### 2. 管理员权限检查与提权 (`WindowsSystemService.HandleAdminElevation`)
- **1. 标题**: `WindowsSystemService.HandleAdminElevation`
- **2. 用途**: 检查当前进程是否具备 Windows 管理员权限。若未具备且用户配置要求提权，则通过 `runas` 动词唤起 UAC 授权弹窗重启本程序。
- **3. 参数**:
  - `args` (`string[]`): 当前进程启动参数，提权重启时会自动追加 `--elevated-admin` 标记。
  - `requireAdmin` (`bool`): 是否需要管理员权限（来自 `config.yaml` 中 `admin.requireAdminOnLaunch` 或由于开启了 UIAccess）。
  - **返回值** (`bool`): `true` 表示已成功发起提权并启动了新进程（当前进程应当即刻退出）；`false` 表示已具备权限或不需要提权。
- **4. 注意事项**:
  - 内置了 `--elevated-admin` 参数防死循环守卫，一旦检测到该标记但权限仍不足，会弹出错误提示并终止，绝不会无限循环弹窗。
  - UIAccess 特权依赖于管理员特权前置，因此当开启 UIAccess 时，此项检查会自动被激活。

---

### 3. UIAccess 特权置顶检查与提权 (`WindowsSystemService.HandleUiAccessElevation`)
- **1. 标题**: `WindowsSystemService.HandleUiAccessElevation`
- **2. 用途**: 突破标准 Windows 桌面窗口 Z-Order 限制。利用嵌入的 `uiaccess.dll` 将本进程令牌赋予 `UIAccess=true`，使悬浮按钮与结果浮窗能够置顶于管理员权限软件、任务管理器及全屏窗口之上。
- **3. 参数**:
  - `args` (`string[]`): 当前进程启动参数，重启时会自动追加 `--elevated-uiaccess` 标记。
  - `requireUiAccess` (`bool`): 用户是否在配置中开启了 UIAccess 特权置顶。
  - **返回值** (`bool`): `true` 表示已通过 `uiaccess.dll` 拉起提权进程（当前进程应当即刻退出）；`false` 表示已具备特权或未开启该特性。
- **4. 注意事项**:
  - 要求操作系统目录 `%SystemRoot%` 下具备写入权限（`uiaccess.dll` 会临时安装至安全系统目录执行，随重启生效后自动清理）。
  - 依赖于第 1 步管理员权限到位，否则调用底层 C++ 注入接口将返回拒绝访问。

---

### 4. 提取 UIAccess 原生动态库 (`WindowsSystemService.ExtractUiAccessDll`)
- **1. 标题**: `WindowsSystemService.ExtractUiAccessDll`
- **2. 用途**: 在单文件发布或独立运行模式下，自程序集清单资源 `BlueRandom.uiaccess.dll` 读取二进制流，释放至当前运行目录，供 P/Invoke 调用。
- **3. 参数**:
  - 无显式输入参数。
  - **返回值** (`string`): 提取落盘后的 `uiaccess.dll` 完整绝对文件路径。
- **4. 注意事项**:
  - 优先探测本地物理目录是否存在 `third_party/uiaccess.dll`（开发调试模式）。
  - 释放文件发生占用或只读冲突时，会尝试安全重命名并记录警告日志，避免崩溃。

---

### 5. 创建免 UAC 开机自启任务 (`WindowsSystemService.SetAutoStartTask`)
- **1. 标题**: `WindowsSystemService.SetAutoStartTask`
- **2. 用途**: 通过 Windows 任务计划程序（`schtasks.exe`）注册名为 `BlueRandom_AutoStart` 的计划任务，实现开机静默以最高权限启动，避免开机触发恼人的 UAC 提权黄框。
- **3. 参数**:
  - 无显式输入参数，自动获取当前进程主模块可执行文件路径 (`Process.GetCurrentProcess().MainModule.FileName`)。
  - **返回值** (`bool`): 注册成功返回 `true`，执行异常或权限不足返回 `false`。
- **4. 注意事项**:
  - 执行 `schtasks.exe /create /f /sc onlogon /rl highest` 指令，必须在管理员上下文中执行方可生效。
  - 若用户修改了应用程序物理路径，必须重新调用此方法更新计划任务指向。

---

### 6. 删除开机自启任务 (`WindowsSystemService.RemoveAutoStartTask`)
- **1. 标题**: `WindowsSystemService.RemoveAutoStartTask`
- **2. 用途**: 在用户于高级设置面板中关闭开机自启选项时，安全清理已注册的 `BlueRandom_AutoStart` 计划任务。
- **3. 参数**:
  - 无。
  - **返回值** (`bool`): 成功删除或任务原本不存在返回 `true`，执行报错返回 `false`。
- **4. 注意事项**:
  - 使用 `schtasks.exe /delete /tn "BlueRandom_AutoStart" /f` 静默清理，避免弹窗打扰用户。

---

### 7. Win32 原生置顶状态刷新 (`WindowsSystemService.RefreshTopMost`)
- **1. 标题**: `WindowsSystemService.RefreshTopMost`
- **2. 用途**: 封装 Win32 API `SetWindowPos`，强制将目标窗口 HWND 设置为 `HWND_TOPMOST`，解除 Windows 系统因全屏切换或焦点争抢导致的窗口下沉。
- **3. 参数**:
  - `hwnd` (`IntPtr`): 目标窗口的 Win32 句柄。
  - **返回值** (`void`)。
- **4. 注意事项**:
  - 传参标志位必须为 `SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW`，防止刷新置顶时抢夺用户正在输入的游戏或工作软件焦点。
  - 悬浮球后台定时器（1 秒心跳）会定期触发此方法以实现抗全屏压制。
