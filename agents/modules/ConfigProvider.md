# ConfigProvider 模块说明文档

## 概述
`ConfigProvider` 模块位于 `Main/Modules/ConfigProvider`，是 Blue Random 的数据持久化、配置模型契约、内存配置缓存与安全密码保护中枢。本模块定义了完整的树状领域配置对象结构（`ConfigModel`）、提供线程安全的内存配置单例（`ConfigCache`）、实现配置文件的协议版本迁移与带结构化中文注释的 YAML 编解码器（`YamlConfigService`）、以及防误改的 SHA-256 密码保护系统（`SecurityService`）。

---

## 功能清单

### 1. 全局配置领域模型 (`ConfigModel`)
- **1. 标题**: `ConfigModel` 配置模型体系
- **2. 用途**: 以 C# 类型契约定义系统的全部配置项。包括悬浮按钮、抽取人数弹窗、演播结果浮窗（含暂留窗）、系统特权与学生名单等全部数据节点，与前端 YAML 字段保持双向映射。
- **3. 参数**:
  - `RootConfig`: 根配置实体，包含 `ProtocolVersion`、`StudentList`、`AllowRepeatDraw`、`AgreedEula`、`ActiveClassId`、`FloatingButton`、`PickCountDialog`、`PickResultDialog`、`Admin`。
  - `FloatingButtonConfig`: 悬浮球尺寸百分比（`SizePercent`）、透明度（`TransparencyPercent`）、置顶（`AlwaysOnTop`）、任务栏可见（`ShowInTaskbar`）、屏幕坐标（`Position`）、自定义图标 ID（`CustomIconId`）、图标大小（`IconSize`）与边框色（`BorderColor`）。
  - `PickResultDialogConfig`: 抽卡音效开关/自定义 ID/音量、BGM 开关/自定义 ID/音量/起始秒数/淡入淡出时长、演播窗底色/边框色/不透明度，以及结果暂留浮窗全部配置（`ShowLastResult`、`LastResultBgColor`、`LastResultBorderColor`、`LastResultOpacity`、`LastResultAutoCloseSeconds`）。
  - `AdminConfig`: 开机自启管理员开关（`AdminAutoStartAdmin`）、自启路径（`AdminAutoStartPath`）、计划任务名（`AdminAutoStartTaskName`）、启动时管理员提权开关（`RequireAdminOnLaunch`）与 UIAccess 置顶增强开关（`UiAccessEnabled`）。
- **4. 注意事项**:
  - 各属性均标注 `[YamlMember(Alias = "...")]`，且严格统一使用 lowerCamelCase，保证与前端 Vue 响应式字段一致。
  - 属性均设有防御性默认值，反序列化时缺失的键会自动回退为安全默认值。

---

### 2. 全内存线程安全配置缓存 (`ConfigCache`)
- **1. 标题**: `ConfigCache`
- **2. 用途**: 全局线程安全内存配置单例。为后端高频调用（如抽卡算法、窗口渲染属性刷新、IPC 广播）提供毫秒级内存访问，避免高频磁盘 IO 延迟。
- **3. 参数**:
  - `ConfigCache.Current`: 通过排他锁原子读写当前生效的 `RootConfig` 对象，赋值后触发 `ConfigUpdated` 事件。
  - `ConfigCache.Mutate(Action<RootConfig> updater)`: 在锁内安全修改指定字段并在锁外触发 `ConfigUpdated`。
  - `ConfigCache.GetStudentPool()`: 返回当前学生名单列表的独立副本。
  - `ConfigCache.GetFcbProps()`: 提取悬浮按钮配置对象。
  - `ConfigCache.GetFrwProps()`: 提取结果浮窗与暂留窗配置对象。
- **4. 注意事项**:
  - 读写操作均受内部排他锁保护，并在释放锁后触发 `ConfigUpdated` 事件，避免死锁。

---

### 3. 加载与自动迁移配置 (`YamlConfigService.LoadConfig`)
- **1. 标题**: `YamlConfigService.LoadConfig`
- **2. 用途**: 从物理磁盘 `~/.blue random/config.yaml` 读取文本，执行反序列化、合法性数值钳位校验，并自动检测协议版本差异完成平滑无损迁移。
- **3. 参数**:
  - `autoMigrate` (`bool`): 是否在检测到协议代号不匹配时自动升级并重新落盘，默认为 `true`。
  - **返回值** (`RootConfig`): 归一化校验后的有效配置对象。
- **4. 注意事项**:
  - 若磁盘上不存在配置文件，可通过 `EnsureConfigFileExists()` / `WriteDefaultConfig()` 写入包含详细中文注释的标准模板配置。
  - `Normalize` 方法强制执行边界钳位：`SizePercent` [50, 200]、`TransparencyPercent` [0, 100]、`IconSize` [16, 128]、`DefaultCount` [1, 10]、`SoundVolume`/`MusicVolume` [0, 100]、`PanelOpacity`/`LastResultOpacity` [0.1, 1.0]、`LastResultAutoCloseSeconds` [0, 3600]。

---

### 4. 生成带注释的 YAML 并持久化 (`YamlConfigService.SaveConfig` / `ToYamlWithComments`)
- **1. 标题**: `YamlConfigService.SaveConfig` 与 `YamlConfigService.ToYamlWithComments`
- **2. 用途**: 
  - `ToYamlWithComments`: 自研结构化 YAML 生成器，为导出的配置文件注入详尽的模块中文说明与字段级注释，提升用户手动编辑 `config.yaml` 时的可读性。
  - `SaveConfig`: 先调用 `Normalize` 清洗边界，再将格式化好的带注释 YAML 以 UTF-8 写入主配置文件。
- **3. 参数**:
  - `ToYamlWithComments(RootConfig cfg)`: 传入配置对象，**返回值** (`string`) 为结构化带注释 YAML 字符串。
  - `SaveConfig(RootConfig config)`: 传入有效配置对象，**返回值** (`void`)。
- **4. 注意事项**:
  - 另提供 `PeekProtocolVersion` 与 `IsProtocolMigrationNeeded` 用于快速正则窥探 `protocolVersion`，避免仅判定版本时全量反序列化。

---

### 5. 用户数据目录解析 (`YamlConfigService.GetUserDataDir`)
- **1. 标题**: `YamlConfigService.GetUserDataDir`
- **2. 用途**: 解析并返回 Blue Random 在当前操作系统的运行时根数据目录。
- **3. 参数**:
  - 无输入。
  - **返回值** (`string`): 运行数据绝对路径，默认指向 `%UserProfile%\.blue random`。
- **4. 注意事项**:
  - 支持通过 `OverrideUserDataDir`（单元测试沙箱隔离）或系统环境变量 `BLUERANDOM_USER_DATA_DIR` 覆盖目标路径。

---

### 6. 安全锁凭据管理 (`SecurityService`)
- **1. 标题**: `SecurityService`
- **2. 用途**: 防止学生在教室多媒体一体机上私自修改配置或名单。提供密码设置、密码验证、密码注销以及一键恢复出厂设置保护。
- **3. 参数**:
  - `IsEnabled()`: 判断 `~/.blue random/.SHA256` 是否存在且非空。
  - `VerifyPassword(string password)`: 计算输入明文的 SHA-256 摘要，并使用 `CryptographicOperations.FixedTimeEquals` 恒定时间比对。
  - `SetPassword(string password)`: 校验密码长度至少 4 位后计算 SHA-256 哈希并写入 `.SHA256` 文件。
  - `DisablePassword(string password)`: 校验原密码通过后物理删除 `.SHA256` 文件。
  - `ResetAllConfig()`: 删除 `.SHA256`、重置 `config.yaml` 为默认模板，并递归清空 `classes/` 与 `customs/` 目录。
- **4. 注意事项**:
  - 密码哈希独立存储在 `~/.blue random/.SHA256` 文件中，不写入 `config.yaml`。

