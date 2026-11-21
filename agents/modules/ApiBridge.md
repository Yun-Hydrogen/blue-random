# ApiBridge 模块说明文档

## 概述
`ApiBridge` 模块位于 `Main/Modules/ApiBridge`，是 Blue Random 的核心跨线程内部通信总线与外部网络更新 API 桥梁。本模块包含基于高性能发布/订阅模式构建的进程内解耦通信系统（`IpcBroker`）、强类型通信动作与载荷封包契约（`IpcMessages`）、以及基于 GitHub API 的版本检查与语义化代号比对服务（`UpdateService`）。

---

## 功能清单

### 1. 进程内发布/订阅总线 (`IpcBroker`)
- **1. 标题**: `IpcBroker` 消息总线
- **2. 用途**: 为处于不同 STA 独立 UI 线程（悬浮球、结果演播窗、结果暂留窗、配置面板）与后台调度线程之间的交互提供无锁、松耦合的统一消息投递管道，消除各窗口对象之间的直接强引用。
- **3. 参数**:
  - `Subscribe(IpcModule target, Action<IpcMessage> handler)`: 订阅发往指定模块的消息。
  - `Unsubscribe(IpcModule target, Action<IpcMessage> handler)`: 注销监听。
  - `Send(IpcMessage message)`: 异步广播投递消息包（非阻塞）。
- **4. 注意事项**:
  - 内部基于线程安全的并发字典管理订阅链表。
  - 消息投递在发布者的执行上下文中触发派发，订阅方的 handler 接收到消息后，若涉及 UI 控件属性变更，**必须通过各自窗口的 `Dispatcher.Invoke` 或 `BeginInvoke` 切回当前窗口归属的 UI 线程**执行。

---

### 2. 通信协议与载荷封包 (`IpcMessages`)
- **1. 标题**: `IpcMessages` 消息契约模型
- **2. 用途**: 规范化通信协议中的动作语义、模块枚举以及各场景下的强类型传输载荷。
- **3. 参数**:
  - `IpcAction`: 动作语义（`Post` 单向投递、`Request` 请求、`Response` 响应、`Destroy` 销毁）。
  - `IpcModule`: 模块标识（`Core` 核心调度、`Fcb` 悬浮按钮、`Frw` 结果演播窗、`Lrw` 结果暂留窗、`Cp` 配置面板、`Tray` 系统托盘）。
  - `IpcMessage`: 消息主体，包含 `Action`、`Sender`、`Target`、`Command`（命令字字符串）、`Payload`（对象载荷）。
  - 核心载荷类：
    - `FcbPositionPayload`: 携带悬浮按钮移动后的 `PositionX` 与 `PositionY`。
    - `FrwShowPayload`: 携带演播窗抽卡结果名单与演出配置。
    - `FrwClosedPayload`: 携带演播窗关闭时的学生名单 `Students` 与用户是否在底部横条主动点击暂留的标识 `KeepResult`。
    - `LrwShowPayload`: 携带暂留窗上一轮抽取学生列表与抽取完成时间 `DrawnAt`。
- **4. 注意事项**:
  - 模块间传输复杂数据优先使用上述强类型载荷，禁止传递包含原生 Win32 句柄或未冻结的 WPF 控件对象。

---

### 3. 自动化与手动检查更新 (`UpdateService.CheckForUpdatesAsync`)
- **1. 标题**: `UpdateService.CheckForUpdatesAsync`
- **2. 用途**: 异步访问 GitHub API 检索最新 Release，与当前运行的版本号及版本代号比对，判断是否存在可用更新或先行预览版。
- **3. 参数**:
  - `owner` (`string`): GitHub 仓库所有者（默认 `"Yun-Hydrogen"`）。
  - `repo` (`string`): 仓库名（默认 `"blue-random"`）。
  - **返回值** (`Task<UpdateCheckResult>`): 异步返回更新结果对象，包含 `UpdateAvailable`、`IsPrerelease`、`LatestVersion`、`LatestCodename`、`ReleaseUrl`、`ReleaseNotes`。
- **4. 注意事项**:
  - 请求时附带符合 GitHub REST 规范的 `User-Agent` 与防超时限制（10 秒超时）。
  - 遇到断网、被墙或 GitHub API 速率超限（HTTP 403）时，自动捕获异常并返回无更新结果，并在日志中记录 Warning，绝不打扰用户正常教学使用。

---

### 4. 语义化版本号与代号比对 (`UpdateService.CompareVersions`)
- **1. 标题**: `UpdateService.CompareVersions`
- **2. 用途**: 对格式为 `YY.MM.PATCH`（例如 `"26.10.04"`）的版本号字符串进行逐级数值加权比对。
- **3. 参数**:
  - `v1` (`string`): 第一个版本号。
  - `v2` (`string`): 第二个版本号。
  - **返回值** (`int`): 若 `v1 > v2` 返回 `1`；`v1 < v2` 返回 `-1`；相等返回 `0`。
- **4. 注意事项**:
  - 自动剥离前缀 `"v"` 并按 `.` 拆分三段整型数字；若遇到缺段情况（如 `"26.10"`）自动补齐为零再做比较。
