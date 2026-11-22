# WebUIBridger 模块说明文档

## 概述
`WebUIBridger` 模块位于 `Main/Modules/WebUIBridger`，是 .NET 宿主进程与 Vue 3 前端配置面板（WebView2）之间的通信桥梁与环境管理器。本模块承载 WebView2 独立运行环境生命周期管理、全内存虚拟域静态资源拦截器（`https://appassets/*`）、COM 互操作桥接对象（`NativeBridge`）的注入与反射调用、以及向前端 JavaScript 上下文安全推送异步事件。

---

## 功能清单

### 1. 配置面板浏览器宿主初始化 (`ConfigPanelBrowserHost.EnsureReadyAsync`)
- **1. 标题**: `ConfigPanelBrowserHost.EnsureReadyAsync`
- **2. 用途**: 异步初始化 WebView2 运行时环境、禁用 Chromium 网络缓存、注入 `NativeBridge` COM 宿主对象、挂载全内存虚拟域资源拦截器，并加载配置面板主页面（支持 `VITE_DEV_SERVER_URL` 开发热调试或 `https://appassets/index.html#/config-panel`）。
- **3. 参数**:
  - 无输入参数（依赖构造函数注入的 `WebView2` 控件与 `NativeBridge` 实例）。
  - **返回值** (`Task`): 异步任务，完成后 `IsReady` 属性变为 `true`。
- **4. 注意事项**:
  - 内部具备 `SemaphoreSlim(1, 1)` 重入锁，保证多次并发调用只会触发一次实际初始化。
  - 调用 `Profile.ClearBrowsingDataAsync()` 与 DevTools Protocol `Network.clearBrowserCache` / `Network.setCacheDisabled`，杜绝前端更新后因浏览器缓存出现逻辑陈旧。
  - 虚拟域过滤拦截规则为 `https://appassets/*`，拦截器直接读取 `ResourceCache.GetWebUiAsset` 内存字节流，全程零磁盘落盘与零 IO 延迟。

---

### 2. 向前端推送配置变更与更新进度 (`ConfigPanelBrowserHost.PushConfigUpdatedAsync` / `PushUpdateProgressAsync`)
- **1. 标题**: `ConfigPanelBrowserHost.PushConfigUpdatedAsync` 与 `PushUpdateProgressAsync`
- **2. 用途**: 
  - `PushConfigUpdatedAsync`: 当外部修改配置后，向 WebView2 注入脚本触发 `window.__onConfigUpdated(yaml)` 刷新草稿数据。
  - `PushUpdateProgressAsync`: 在下载 Release 更新包期间，向 WebView2 实时推送 `window.__onUpdateDownloadProgress(progress)` 进度对象。
- **3. 参数**:
  - `yaml` (`string`): 最新的 UTF-8 YAML 文本字符串。
  - **返回值** (`Task`): 异步执行任务。
- **4. 注意事项**:
  - 内部自动对 YAML 字符串执行 `JsonSerializer.Serialize` 转义，杜绝特殊换行符或引号注入导致的 JavaScript 语法错误。
  - 若调用时前端页面尚未加载完成，内部吞掉异常，不中断业务流程。

---

### 3. 获取配置面板独立运行环境 (`WebViewEnvironmentManager.GetCpEnvironmentAsync`)
- **1. 标题**: `WebViewEnvironmentManager.GetCpEnvironmentAsync`
- **2. 用途**: 采用锁保护惰性创建配置面板专属的 `CoreWebView2Environment` 实例，指定用户数据隔离目录。
- **3. 参数**:
  - 无输入参数。
  - **返回值** (`Task<CoreWebView2Environment>`): 返回用于初始化 WebView2 控件的环境配置对象。
- **4. 注意事项**:
  - 独立使用 `%UserProfile%\.blue random\EBWebView` 目录存储用户 Cookie 与 Storage，杜绝多线程 COM 套间访问同一用户目录引起的 `0x80010106` 冲突。
  - 当前架构中仅配置面板使用 WebView2（结果演播窗已重写为 DirectComposition），无需创建多个环境。

---

### 4. 获取应用系统信息 (`NativeBridge.GetAppInfoJson`)
- **1. 标题**: `NativeBridge.GetAppInfoJson`
- **2. 用途**: 供前端页面在加载时读取当前应用的版本号、版本发布代号、管理员权限状态、UIAccess 特权状态、`uiaccess.dll` 可用性、配置文件路径与可执行文件路径。
- **3. 参数**:
  - 无输入参数。
  - **返回值** (`string`): JSON 字符串，包含 `version`、`codename`、`isAdmin`、`isUiAccess`、`uiAccessDllExists`、`configPath`、`configDir`、`exePath` 字段。
- **4. 注意事项**:
  - 本方法通过 `[ComVisible(true)]` 暴露给 JavaScript `window.chrome.webview.hostObjects.nativeBridge.GetAppInfoJson()` 直接调用。

---

### 5. 获取、保存与重置配置 (`NativeBridge.GetConfigYaml` / `SaveConfigYaml` / `ResetConfig`)
- **1. 标题**: `NativeBridge.GetConfigYaml`、`SaveConfigYaml` 与 `ResetConfig`
- **2. 用途**: 
  - `GetConfigYaml()`: 读取当前正在运行的带中文注释的完整 `config.yaml` 文本内容。
  - `SaveConfigYaml(string yaml)`: 调用 `BackendScheduler.SaveAndApplyConfig(yaml)` 执行反序列化校验、同步当前班级文件、持久化落盘并向 `Fcb`/`Frw`/`Lrw` 广播热更新。
  - `ResetConfig()`: 触发 `ConfigReset` 并调用 `SecurityService.ResetAllConfig()` 重置配置与刷新各窗口。
- **3. 参数**:
  - `SaveConfigYaml(string yaml)` 返回 `bool`；`ResetConfig()` 返回 `bool`。
- **4. 注意事项**:
  - 保存成功后所有视图界面瞬间热更新，无需重启程序。

---

### 6. 多班级管理接口 (`NativeBridge.GetClassesJson` 等)
- **1. 标题**: `GetClassesJson` / `CreateClassJson` / `RenameClass` / `DeleteClass`
- **2. 用途**: 提供多班级列表查询、新建、重命名与删除接口。数据独立持久化于 `~/.blue random/classes/{classId}.yaml`。
- **3. 参数**:
  - `GetClassesJson()`: 返回 `{ ok, activeClassId, classes }` JSON 字符串。
  - `CreateClassJson(string name)`: 创建新班级并设为当前激活班级，返回 `{ ok, class }` JSON。
  - `RenameClass(string classId, string newName)`: 重命名班级，返回 `bool`。
  - `DeleteClass(string classId)`: 删除班级（若删除当前激活班级则自动切换至剩余首个班级），返回 `bool`。
- **4. 注意事项**:
  - 切换激活班级与编辑学生名单/权重由前端更新 `draft.activeClassId` 与 `draft.studentList` 后通过 `SaveConfigYaml` 统一同步落盘。

---

### 7. 自定义资产存储与查询 (`NativeBridge.SaveCustomResourceJson` 等)
- **1. 标题**: `SaveCustomResourceJson` / `GetCustomResourceJson` / `DeleteCustomResource`
- **2. 用途**: 接收前端上传的图片或音频 Base64 编码，安全存储至 `customs/` 目录并返回资源元数据 JSON。
- **3. 参数**:
  - `SaveCustomResourceJson(string fileName, string mimeType, string purpose, string base64, double duration)`: 返回 `CustomResourceItem` JSON（失败返回 `"null"`）。
  - `GetCustomResourceJson(string customId)`: 按 ID 查询资源元数据与 Base64，返回 JSON（不存在返回 `"null"`）。
  - `DeleteCustomResource(string customId)`: 删除指定资源，返回 `bool`。
- **4. 注意事项**:
  - 自动剥离 DataURL 协议头；启动时由 `CustomsStore.ConsolidateAndBindLatestResources` 对同用途资源去重。

---

### 8. 系统操作、更新检查与安全锁 (`NativeBridge`)
- **1. 标题**: 系统文件、更新检查与安全锁接口
- **2. 用途**:
  - 文件与系统：`PickFile(string filter)`、`OpenConfigFolder()`、`OpenUrl(string url)`、`ClearCache()`、`CreateStartupTask(string taskName, string exePath, bool asAdmin)`、`Restart()`、`AdminElevate()`、`CloseWindow()`、`OpenDevTools()`、`GetFloatingPositionJson()`。
  - 更新与日志：`CheckUpdateJson()`（异步调用 `UpdateService.CheckUpdateAsync()`）、`DownloadUpdateJson(downloadUrl, expectedSize)`（流式下载并推送进度）、`CancelUpdateDownload()`、`ApplyDownloadedUpdateJson(stagedPath)`（启动替换脚本并退出重启）、`GetChangelog()`。
  - 安全锁：`IsSecurityEnabled()`、`VerifyPassword(string pwd)`、`SetPassword(string pwd)`、`DisablePassword(string pwd)`、`ResetAllConfig()`。
- **3. 注意事项**:
  - 密码哈希独立存储在 `~/.blue random/.SHA256`，采用 `CryptographicOperations.FixedTimeEquals` 恒定时间比对。

---

### 9. 日志查询与投递 (`NativeBridge.GetLogsJson` / `Log`)
- **1. 标题**: `NativeBridge.GetLogsJson` 与 `NativeBridge.Log`
- **2. 用途**: 
  - `GetLogsJson(int maxLines)`: 读取 `~/.blue random/log.txt` 末尾最多 `maxLines` 行，解析为 `{ id, time, level, text }` JSON 数组供前端“运行日志”面板轮询展示。
  - `Log(string level, string message)`: 接收前端日志并转递至 `AppLogger`（标签 `"WebUI"`）。
- **3. 参数**:
  - `GetLogsJson(int maxLines)`: **返回值** (`string`) 为日志实体列表 JSON。
  - `Log(string level, string message)`: 入参为日志级别（`"info"` / `"warn"` / `"error"`）与消息内容。
- **4. 注意事项**:
  - 日志返回集合按时间**正序**排列（老日志在前，最新日志在最底部），与前端 `TabLogs.vue` 增量追加与自动滚底逻辑一致。

