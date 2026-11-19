# ConfigProvider 模块说明文档

## 概述
`ConfigProvider` 模块位于 `Main/Modules/ConfigProvider`，是 Blue Random 的数据持久化、配置模型契约、内存配置缓存与安全密码保护中枢。本模块定义了完整的树状领域配置对象结构（`ConfigModel`）、提供高性能无锁只读的内存配置单例（`ConfigCache`）、实现配置文件的版本迁移与带丰富结构化中文注释的 YAML 编解码器（`YamlConfigService`）、以及防误改的 SHA-256 加盐密码保护系统（`SecurityService`）。

---

## 功能清单

### 1. 全局配置领域模型 (`ConfigModel`)
- **1. 标题**: `ConfigModel` 配置模型体系
- **2. 用途**: 以 C# 类型契约定义系统的全部配置项。包括悬浮按钮、抽取人数弹窗、演播结果浮窗（含暂留窗）、系统特权与学生名单等全部数据节点，与前端 YAML 字段保持双向映射。
- **3. 参数**:
  - `RootConfig`: 根配置实体，包含 `ProtocolVersion`、`StudentList`、`FloatingButton`、`PickCountDialog`、`PickResultDialog`、`Admin`。
  - `FloatingButtonConfig`: 悬浮球尺寸百分比（`SizePercent`）、透明度（`TransparencyPercent`）、置顶（`AlwaysOnTop`）、图标大小与边框色、自定义图标 ID 及屏幕绝对位置 `Position`。
  - `PickResultDialogConfig`: 演播窗展示时长、动画缩放、抽卡音效与背景音配置、以及结果暂留浮窗全部配置（`ShowLastResult`、`LastResultBgColor`、`LastResultOpacity`、`LastResultAutoCloseSeconds`）。
  - `AdminConfig`: 管理员启动强制提权标记、UIAccess 置顶增强开关、密码哈希与加密盐。
- **4. 注意事项**:
  - 各属性均标注 `[YamlMember(Alias = "...")]`，且严格统一使用 lowerCamelCase，保证与前端 Vue 响应式字段一致。
  - 属性均设有防御性默认值，反序列化时缺失的键会自动回退为安全默认值。

---

### 2. 全内存线程安全配置缓存 (`ConfigCache`)
- **1. 标题**: `ConfigCache`
- **2. 用途**: 全局无锁内存配置单例。为后端高频调用（如抽卡算法、窗口渲染属性刷新、IPC 广播）提供亚微秒级的只读访问，完全隔绝磁盘 IO 延迟。
- **3. 参数**:
  - `ConfigCache.Current`: 获取或原子替换当前生效的 `RootConfig` 对象。
  - `ConfigCache.GetFcbProps()`: 提取悬浮按钮配置快照。
  - `ConfigCache.GetFrwProps()`: 提取结果浮窗与暂留窗配置快照。
- **4. 注意事项**:
  - 读取操作完全免锁（基于引用原子读取）；写操作使用不可变原子置换模式，杜绝脏读或死锁。

---

### 3. 加载与自动迁移配置 (`YamlConfigService.LoadConfig`)
- **1. 标题**: `YamlConfigService.LoadConfig`
- **2. 用途**: 从物理磁盘 `~/.blue random/config.yaml` 读取文本，执行反序列化、合法性数值钳位校验，并自动检测协议版本差异完成平滑无损迁移。
- **3. 参数**:
  - `autoMigrate` (`bool`): 是否在检测到配置代号不匹配时自动升级并重新落盘，默认为 `true`。
  - **返回值** (`RootConfig`): 归一化校验后的有效配置对象。
- **4. 注意事项**:
  - 若磁盘上不存在配置文件，会自动创建并写入包含详细中文注释的标准模板配置。
  - 无论用户在外部如何篡改数值，`Normalize` 方法均会强制执行边界钳位（如抽取人数限制在 1-10 人，不透明度限制在 0.1-1.0），防止窗口因异常数据崩溃。

---

### 4. 生成带注释的 YAML 并持久化 (`YamlConfigService.SaveConfig` / `ToYamlWithComments`)
- **1. 标题**: `YamlConfigService.SaveConfig` 与 `YamlConfigService.ToYamlWithComments`
- **2. 用途**: 
  - `ToYamlWithComments`: 自研结构化 YAML 生成器，为导出的配置文件注入详尽的模块中文说明与字段级注释，极大提升用户手动编辑 `config.yaml` 时的可读性。
  - `SaveConfig`: 将格式化好的带注释 YAML 写入用户主目录下的配置文件。
- **3. 参数**:
  - `ToYamlWithComments(RootConfig cfg)`: 传入配置对象，**返回值** (`string`) 为结构化带注释 YAML 字符串。
  - `SaveConfig(RootConfig config)`: 传入有效配置对象，**返回值** (`void`)。
- **4. 注意事项**:
  - 采用写入临时文件再原子重命名（Replace）的安全落盘机制，防止在系统断电或崩溃时将原配置文件损坏为空文件。

---

### 5. 用户数据目录解析 (`YamlConfigService.GetUserDataDir`)
- **1. 标题**: `YamlConfigService.GetUserDataDir`
- **2. 用途**: 解析并返回 Blue Random 在当前操作系统的运行时根数据目录。
- **3. 参数**:
  - 无输入。
  - **返回值** (`string`): 运行数据绝对路径，默认指向 `%UserProfile%\.blue random`。
- **4. 注意事项**:
  - 支持通过系统环境变量 `BLUERANDOM_USER_DATA_DIR` 覆盖目标路径，便于便携版（Portable）打包或多实例隔离测试。

---

### 6. 安全锁凭据管理 (`SecurityService`)
- **1. 标题**: `SecurityService`
- **2. 用途**: 防止学生在教室多媒体一体机上私自修改抽卡权重或删除配置。提供密码设置、密码验证、密码注销以及一键恢复出厂设置保护。
- **3. 参数**:
  - `IsEnabled()`: 判断当前是否已配置并开启了管理员密码保护。
  - `VerifyPassword(string password)`: 校验输入的明文密码是否与加密哈希匹配。
  - `SetPassword(string password)`: 生成 16 字节高强度密码学随机盐（Salt），执行 SHA-256 哈希计算并持久化。
  - `DisablePassword(string password)`: 校验正确后注销当前安全密码锁。
  - `ResetAllConfig()`: 恢复出厂默认配置并清除全部自定义数据。
- **4. 注意事项**:
  - 密码验证失败返回 `false`，安全敏感操作在后端均受 `SecurityService` 前置拦截，即使前端直接发起非法 IPC 消息也无法越权修改。
