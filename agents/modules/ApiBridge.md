# ApiBridge 模块说明文档

## 概述
`ApiBridge` 模块位于 `Main/Modules/ApiBridge`，是 Blue Random 的核心跨线程内部通信总线与外部网络更新 API 桥梁。本模块包含基于 `System.Threading.Channels` 构建的进程内解耦通信系统（`IpcBroker`）、强类型通信动作与载荷封包契约（`IpcMessages`）、以及基于 GitHub Releases API 与 302 重定向探针的版本检查服务（`UpdateService`）。

---

## 功能清单

### 1. 进程内发布/订阅总线 (`IpcBroker`)
- **1. 标题**: `IpcBroker` 消息总线
- **2. 用途**: 为处于不同 STA 独立 UI 线程（悬浮球、结果演播窗、结果暂留窗、配置面板）与后台调度器之间的交互提供无锁队列、松耦合的统一消息投递管道，消除各窗口对象之间的直接强引用。
- **3. 参数**:
  - `Initialize()`: 启动单读取后台异步分发循环（`Channel.CreateUnbounded<IpcMessage>`，`SingleReader = true, SingleWriter = false`）。
  - `Subscribe(IpcModule target, Action<IpcMessage> handler)`: 订阅发往指定模块的消息。
  - `Unsubscribe(IpcModule target, Action<IpcMessage> handler)`: 注销监听。
  - `Send(IpcMessage message)`: 非阻塞写入通道队列（若未初始化则自动触发 `Initialize()`）。
  - `Destroy()`: 取消分发令牌并关闭通道写入端。
- **4. 注意事项**:
  - 内部基于 `ConcurrentDictionary<IpcModule, List<Action<IpcMessage>>>` 管理订阅列表，并在后台分发任务中按快照隔离执行且独立捕获异常。
  - 订阅方的 handler 在 `IpcBroker` 后台分发线程上下文中被调用；若涉及 UI 控件属性变更，**必须通过各自窗口的 `Dispatcher.Invoke` / `BeginInvoke`（或 `DCompResultWindow` 的 `_uiQueue` + `PostMessageW`）切回目标窗口归属的 STA 线程**执行。

---

### 2. 通信协议与载荷封包 (`IpcMessages`)
- **1. 标题**: `IpcMessages` 消息契约模型
- **2. 用途**: 规范化通信协议中的动作语义、模块枚举以及各场景下的强类型传输载荷。
- **3. 参数**:
  - `IpcAction`: 动作语义（`Init` 初始化、`Destroy` 销毁、`Get` 下行控制/同步、`Post` 上行事件上报）。
  - `IpcModule`: 模块标识（`Ipc` 核心总线/全局控制、`Fcb` 悬浮按钮、`Frw` 结果演播窗、`Cp` 配置面板、`Lrw` 结果暂留窗）。
  - `IpcMessage`: 消息主体，包含 `Action`、`Target`、`Command`（命令字字符串）、`Payload`（对象载荷），提供 `IpcMessage.Create(...)` 工厂方法。
  - 核心载荷类：
    - `FcbPositionPayload`: 携带悬浮按钮移动后的 `MonitorNumber`、`PositionX` 与 `PositionY`（均为 `int`）。
    - `FrwShowPayload`: 携带演播窗实际抽取人数 `Amount` 与结果名单 `CalledStudents`。
    - `FrwClosedPayload`: 携带演播窗关闭时的学生名单 `Students` 与用户是否在底部横条主动点击暂留的标识 `KeepResult`。
    - `LrwShowPayload`: 携带暂留窗上一轮抽取学生列表 `CalledStudents` 与抽取完成时间 `DrawnAt`。
- **4. 注意事项**:
  - 模块间传输复杂数据优先使用上述强类型载荷，禁止传递包含原生 Win32 句柄或未冻结的 WPF 控件对象。

---

### 3. 自动化与手动检查更新 (`UpdateService.CheckUpdateAsync`)
- **1. 标题**: `UpdateService.CheckUpdateAsync`
- **2. 用途**: 异步检索 GitHub 仓库（`Yun-Hydrogen/blue-random`）最新 Release，拉取远端 Release 的 `changelog.md` 与可执行文件资产直链（`BlueRandom.exe`），并与当前版本号比对。
- **3. 参数**:
  - 无显式输入参数（仓库常量为 `RepoOwner = "Yun-Hydrogen"`、`RepoName = "blue-random"`）。
  - **返回值** (`Task<UpdateCheckResult>`): 异步返回更新结果对象，包含 `Ok`、`Status`（`"update"` / `"ok"` / `"warn"` / `"error"`）、`Title`、`Detail`、`ReleaseUrl`、`DownloadUrl`、`AssetName`、`AssetSize`、`LocalVersion`、`RemoteVersion`、`RemoteTag`。
- **4. 注意事项**:
  - **双通道容灾检测**: 首选请求 GitHub Releases REST API（8 秒超时）解析 `assets` 中的 `changelog.md` 与 `BlueRandom.exe`（兼容旧版带版本号 `.exe`）；若因网络波动或未认证 403 限流失败，自动降级为请求 `releases/latest` 并禁用自动重定向（`AllowAutoRedirect = false`），从 `Location` 响应头提取最新 Tag 后直接拉取对应 Tag 下的 `changelog.md` 与 `BlueRandom.exe` 下载地址。
  - 遇到断网或双通道均失败时，自动捕获异常并返回 `Status = "error"` 的结果对象及默认 Releases 跳转链接，绝不打扰用户正常教学使用。

---

### 4. 下载更新包与自动替换重启 (`UpdateService.DownloadUpdateAsync` / `LaunchSelfUpdate`)
- **1. 标题**: `UpdateService.DownloadUpdateAsync` 与 `UpdateService.LaunchSelfUpdate`
- **2. 用途**: 流式下载远端 Release 的 `BlueRandom.exe` 至 `%TEMP%\BlueRandom_Update\` 并实时回调下载进度（百分比、已下载字节、总字节、下载速度），校验 PE 文件头（`MZ`）与体积后，启动独立 PowerShell 更新脚本等待当前进程退出、覆盖原可执行文件并自动重启新版本。
- **3. 参数**:
  - `DownloadUpdateAsync(string downloadUrl, long expectedSize, Action<UpdateDownloadProgress>? onProgress, CancellationToken ct)`: 支持取消令牌与进度回调。
  - `LaunchSelfUpdate(string stagedPath)`: 校验非 `dotnet.exe` 调试宿主后拉起更新脚本（若目标目录不可写则自动通过 `runas` 请求提权）。
- **4. 注意事项**:
  - 下载过程中写入 `.part` 临时文件，完成后原子重命名并校验 PE 头，防止损坏文件覆盖原程序。

---

### 5. Tag 解析与语义化版本号比对 (`UpdateService.ParseTag` / `CompareVersions`)
- **1. 标题**: `UpdateService.ParseTag` 与 `UpdateService.CompareVersions`
- **2. 用途**: 从 Git Tag（如 `"Shiroko-v26.10.01"`、`"v26.09.01"`）拆解版本号与可选代号，并对多段版本号字符串进行逐级数值比对。
- **3. 参数**:
  - `ParseTag(string tag)`: 返回 `(string Version, string? Codename)`。
  - `CompareVersions(string v1, string v2)`: 若 `v1 > v2` 返回 `1`；`v1 < v2` 返回 `-1`；相等返回 `0`。
- **4. 注意事项**:
  - 自动剥离前缀 `"v"` 并按 `.` 拆分数字段；若遇到缺段情况（如 `"1.0"` vs `"1.0.0"`）自动补齐为零再做比较。

