# Common 模块说明文档

## 概述
`Common` 模块位于 `Main/Modules/Common`，是 Blue Random 系统的跨模块底层基础设施中心。本模块承载了线程安全的统一诊断日志记录器（`AppLogger`）以及统一语义化版本与发布代号探测器（`AppVersion`），为所有业务模块与视图层提供日志诊断与版本元数据支撑。

---

## 功能清单

### 1. 全局统一日志系统 (`AppLogger`)
- **1. 标题**: `AppLogger`
- **2. 用途**: 为整个应用程序提供线程安全的分级日志记录。支持控制台彩色高亮输出，并将运行日志同步追加写入用户数据目录下的 `log.txt`，供前端配置面板实时轮询读取。
- **3. 参数**:
  - `Initialize(string userDataDir)`: 创建用户数据目录并重置 `~/.blue random/log.txt`，写入启动时间戳头。
  - `Info(string module, string message)`: 记录常规信息日志（`INFO`）。
  - `Warn(string module, string message)`: 记录潜在异常或降级运行警告（`WARN`）。
  - `Error(string module, string message, Exception? ex = null)`: 记录严重错误及异常类型、消息与堆栈明细（`ERROR`）。
- **4. 注意事项**:
  - 内部通过 `lock (_lock)` 保证跨 STA 线程并发写入控制台与 `log.txt` 的线程安全，文件写入异常会被静默吞掉以免影响主业务。
  - 每次冷启动会清空并重建 `log.txt`，前端通过 `NativeBridge.GetLogsJson(maxLines)` 读取该文件展示。

---

### 2. 应用语义化版本与代号管理 (`AppVersion`)
- **1. 标题**: `AppVersion`
- **2. 用途**: 动态定位并解析应用唯一的元数据源（根目录 `package.json`），向 C# 各层（启动屏、标题栏、更新服务）统一提供当前程序的版本号（`Version`）与发布代号（`Codename`），并支持读取 `changelog.md`。
- **3. 参数**:
  - `Version` (`string`): 静态属性，返回当前语义化版本字符串（如 `"26.11.04"`，未找到时回退 `"26.10.02"`）。
  - `Codename` (`string`): 静态属性，返回当前版本发布代号（如 `"Momoi"`，未找到时回退 `"Shiroko"`）。
  - `GetChangelog()`: 读取当前版本更新日志（优先内嵌清单 `changelog.md`，其次回溯磁盘）。
- **4. 注意事项**:
  - **双通道智能探测机制**:
    1. 优先尝试从 Assembly Manifest 读取内嵌资源 `package.json` / `changelog.md`（单文件独立打包或 CI 发布产物）；
    2. 若未内嵌（本地开发调试态），则从当前程序集基目录向上逐级回溯探测（最多探测 4 级父目录），确保源码修改版本号后本地 F5 即时生效，无需重复手动打包。
  - 作为全程序版本号的单一权威数据源，前端 `TabLogs.vue` 亦直接引用根 `package.json`，确保前后端版本 100% 同步。

