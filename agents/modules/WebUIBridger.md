# WebUIBridger 模块说明文档

## 概述
`WebUIBridger` 模块位于 `Main/Modules/WebUIBridger`，是 .NET 宿主进程与 Vue 3 前端配置面板（WebView2）之间的通信桥梁与环境管理器。本模块承载 WebView2 独立运行环境生命周期管理、全内存虚拟域静态资源拦截器（`https://appassets/*`）、COM 互操作桥接对象（`NativeBridge`）的注入与反射调用、以及向前端 JavaScript 上下文安全推送异步事件。

---

## 功能清单

### 1. 配置面板浏览器宿主初始化 (`ConfigPanelBrowserHost.EnsureReadyAsync`)
- **1. 标题**: `ConfigPanelBrowserHost.EnsureReadyAsync`
- **2. 用途**: 异步初始化 WebView2 运行时环境、配置安全沙箱、禁用 Chromium 网络缓存、注入 NativeBridge COM 宿主对象、挂载全内存虚拟域资源拦截器，并加载配置面板主页面。
- **3. 参数**:
  - 无输入参数（依赖构造函数注入的 `WebView2` 控件与 `NativeBridge` 实例）。
  - **返回值** (`Task`): 异步任务，完成后 `IsReady` 属性变为 `true`。
- **4. 注意事项**:
  - 内部具备 `SemaphoreSlim(1, 1)` 重入锁，保证多次并发调用只会触发一次实际初始化。
  - 调用 Chromium DevTools Protocol 强制执行 `Network.clearBrowserCache` 与 `Network.setCacheDisabled`，杜绝前端热更新后因浏览器缓存出现白屏或逻辑陈旧。
  - 虚拟域过滤拦截规则为 `https://appassets/*`，拦截器直接读取 `ResourceCache.GetWebUiAsset` 内存字节流，全程零磁盘落盘与零 IO 延迟。

---

### 2. 向前端推送配置变更 (`ConfigPanelBrowserHost.PushConfigUpdatedAsync`)
- **1. 标题**: `ConfigPanelBrowserHost.PushConfigUpdatedAsync`
- **2. 用途**: 当外部（如快捷键、托盘菜单或重置操作）修改了配置后，向 WebView2 注入 JavaScript 脚本触发 `window.__onConfigUpdated(yaml)`，使前端页面实时刷新草稿数据。
- **3. 参数**:
  - `yaml` (`string`): 最新的 UTF-8 YAML 文本字符串。
  - **返回值** (`Task`): 异步执行任务。
- **4. 注意事项**:
  - 内部自动对 YAML 字符串执行 JSON 转义序列化，杜绝特殊换行符或引号注入导致的 JavaScript 执行语法错误。
  - 若调用时前端页面尚未加载完成，内部吞掉异常，不中断业务流程。

---

### 3. 获取配置面板独立运行环境 (`WebViewEnvironmentManager.GetCpEnvironmentAsync`)
- **1. 标题**: `WebViewEnvironmentManager.GetCpEnvironmentAsync`
- **2. 用途**: 采用单例双重检查锁惰性创建配置面板专属的 `CoreWebView2Environment` 实例，指定用户数据隔离目录。
- **3. 参数**:
  - 无输入参数。
  - **返回值** (`Task<CoreWebView2Environment>`): 返回用于初始化 WebView2 控件的环境配置对象。
- **4. 注意事项**:
  - 独立使用 `%UserProfile%\.blue random\EBWebView` 目录存储用户 Cookie 与 IndexedDB，杜绝多线程 COM 套间访问同一用户目录引起的 `0x80010106` 冲突。
  - 当前架构中仅配置面板使用 WebView2（结果演播窗已重写为 DirectComposition），无需创建多个环境。

---

### 4. 获取应用系统信息 (`NativeBridge.GetAppInfoJson`)
- **1. 标题**: `NativeBridge.GetAppInfoJson`
- **2. 用途**: 供前端页面在加载时读取当前应用的版本号、版本发布代号、是否具备管理员权限、是否具备 UIAccess 特权、开机自启状态以及管理员密码锁定状态。
- **3. 参数**:
  - 无输入参数。
  - **返回值** (`string`): JSON 字符串，包含 `version`, `codename`, `isAdmin`, `isUiAccess`, `autoStart`, `hasPassword` 等字段。
- **4. 注意事项**:
  - 本方法被 `[ComVisible(true)]` 标记，供 JavaScript `window.chrome.webview.hostObjects.nativeBridge.GetAppInfoJson()` 直接调用。

---

### 5. 获取与保存配置 YAML (`NativeBridge.GetConfigYaml` / `SaveConfigYaml`)
- **1. 标题**: `NativeBridge.GetConfigYaml` 与 `NativeBridge.SaveConfigYaml`
- **2. 用途**: 
  - `GetConfigYaml`: 读取当前正在运行的带丰富中文注释的完整 `config.yaml` 文本内容。
  - `SaveConfigYaml`: 接收前端修改后的 YAML 内容，执行反序列化合法性校验、数值钳位、同步注入内存缓存 `ConfigCache` 并持久化落盘。
- **3. 参数**:
  - `GetConfigYaml()`: 无输入，**返回值** (`string`) 为当前 YAML 文本。
  - `SaveConfigYaml(string yaml)`: 输入前端序列化生成的 YAML 文本，**返回值** (`bool`) 表示校验与保存是否成功。
- **4. 注意事项**:
  - 保存成功后会自动通过 `IpcBroker` 广播 `ApplyProps` 指令至 `FCB` 悬浮按钮、`FRW` 演播窗以及 `LRW` 暂留窗，所有视图界面瞬间热更新，无需重启程序。
  - 若密码保护开启，前端调用 `SaveConfigYaml` 前必须已完成权限验证。

---

### 6. 多班级管理接口 (`NativeBridge.GetClassesJson` 等)
- **1. 标题**: `NativeBridge.GetClassesJson` / `CreateClassJson` / `UpdateClassJson` / `DeleteClassJson` / `SetActiveClassJson`
- **2. 用途**: 提供全套多班级增删改查与当前活跃班级切换接口。数据独立持久化于 `~/.blue random/classes/{classId}.json`。
- **3. 参数**:
  - `GetClassesJson()`: **返回值** (`string`) 包含当前所有可用班级摘要列表与当前激活的 `activeClassId`。
  - `CreateClassJson(string name)` / `UpdateClassJson(string json)` / `DeleteClassJson(string classId)`: 对应班级的增删改入参。
  - `SetActiveClassJson(string classId)`: 切换当前激活班级。
- **4. 注意事项**:
  - 切换活跃班级后，会同步将该班级学生名单与权重注入 `ConfigCache`，同时向核心抽卡引擎重置本轮抽取历史与候选名单池。

---

### 7. 批量导入学生名单 (`NativeBridge.ImportStudentsJson`)
- **1. 标题**: `NativeBridge.ImportStudentsJson`
- **2. 用途**: 解析用户从本地 TXT、CSV 等文本中粘贴或导入的名单文本，自动提取学生姓名并赋初始权重 1。
- **3. 参数**:
  - `rawText` (`string`): 原始多行学生名单文本（支持逗号、制表符、换行分隔）。
  - **返回值** (`string`): 序列化后的 `List<StudentItem>` JSON 字符串。
- **4. 注意事项**:
  - 内部自动执行重名过滤（忽略大小写与首尾空格）及非法空行剔除。

---

### 8. 自定义资产转码与存储 (`NativeBridge.SaveCustomResourceJson`)
- **1. 标题**: `NativeBridge.SaveCustomResourceJson`
- **2. 用途**: 接收前端上传的图片（PNG/ICO/JPG）或音频（WAV/MP3）的 Base64 编码，安全存储至 `customs/` 目录并返回资源唯一 ID。
- **3. 参数**:
  - `purpose` (`string`): 资源用途标记，如 `icon`（悬浮球图标）、`bgm`（结果演播背景音）、`sound`（抽卡音效）。
  - `fileName` (`string`): 原始文件名（用于识别扩展名）。
  - `base64` (`string`): 文件内容的 Base64 数据字符串。
  - **返回值** (`string`): 包含生成的唯一 `id` 及元数据的 JSON 文本。
- **4. 注意事项**:
  - 自动剥离类似 `data:image/png;base64,` 的 DataURL 协议头。
  - 音频与图片均受单一用途仅保留最新版本的去重策略管理，避免临时目录无限制膨胀。

---

### 9. 管理员安全锁控制 (`NativeBridge.VerifyAdminPassword` 等)
- **1. 标题**: `NativeBridge.VerifyAdminPassword` / `SetAdminPassword` / `DisableAdminPassword`
- **2. 用途**: 为公共教室多媒体场景设计防误触安全锁。通过 PBKDF2/SHA-256 加盐哈希校验密码，通过后方可解锁高级配置。
- **3. 参数**:
  - 各方法接收明文密码 `string password`，**返回值** (`bool`) 表示校验是否通过或设置是否成功。
- **4. 注意事项**:
  - 禁止明文存储密码，盐值与哈希结果存放在 `config.yaml` 的 `admin` 节点下。

---

### 10. 日志查询与投递 (`NativeBridge.GetLogsJson` / `Log`)
- **1. 标题**: `NativeBridge.GetLogsJson` 与 `NativeBridge.Log`
- **2. 用途**: 
  - `GetLogsJson`: 供前端 "运行日志" Tab 分页或全量获取当前内存环形日志缓冲区条目。
  - `Log`: 拦截前端 `console.warn` / `console.error` 并转递至宿主统一日志系统。
- **3. 参数**:
  - `GetLogsJson()`: **返回值** (`string`) 为日志实体列表 JSON。
  - `Log(string level, string message)`: 入参为日志级别（"warn"/"error"）与消息内容。
- **4. 注意事项**:
  - 日志返回集合按时间倒序排列（最新日志排在最前）。
