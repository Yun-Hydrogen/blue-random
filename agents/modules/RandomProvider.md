# RandomProvider 模块说明文档

## 概述
`RandomProvider` 模块位于 `Main/Modules/RandomProvider`，是 Blue Random 应用程序的随机抽取核心领域引擎与抽卡业务协调中心。本模块包含纯领域加权随机抽样算法（`LotteryEngine`）、跨窗口调度与防抖落盘状态机（`BackendScheduler`）、多班级名单 YAML 持久化管理（`ClassManagerService`）、以及抽卡演出专属的音频与 BGM 渐变播放器（`ResultAudioPlayer`）。

---

## 功能清单

### 1. 核心加权抽卡算法 (`LotteryEngine.Pick`)
- **1. 标题**: `LotteryEngine.Pick`
- **2. 用途**: 根据候选名单、预设抽取人数以及是否允许重复抽取的开关，执行加权随机抽样，清洗空白姓名条目，并为每位被抽取的学生映射信封品阶（`rainbow` / `gold` / `blue`）。
- **3. 参数**:
  - `pool` (`IReadOnlyList<StudentItem>?`): 候选学生名单列表，每项包含姓名、非负浮点权重与信封颜色 `LetterColor`。
  - `targetCount` (`int`): 本轮请求抽取的学生人数（通常为 1-10 人）。
  - `allowDuplicate` (`bool`): 是否允许同一个人在同一轮抽卡中被重复抽取。
  - **返回值** (`List<CalledStudent>`): 抽取命中的学生实体结果列表，包含 `Name` 与规范化后的品阶颜色 `LetterColor`（`"rainbow"` / `"gold"` / `"blue"`）。
- **4. 注意事项**:
  - **允许重复模式**: 采用带非线性 Gamma 增益（$\gamma = 1.5$，$w_i' = \max(0, w_i)^{1.5}$）的轮盘赌加权抽样（Roulette-Wheel Selection）；若总权重 $\le 0$，退化为离散均匀随机抽样。
  - **禁止重复模式**: 对正权重池（$w_i > 0$）采用 **Efraimidis-Spirakis (A-Res) 加权无放回抽样算法**，计算特征键 $k_i = -\ln(U_i) / w_i$ 并按升序取前 $K$ 项；若正权重人数不足 `targetCount`，剩余名额从零权重池（$w_i \le 0$）通过 Fisher-Yates 洗牌补齐。
  - **信封品阶映射**: 内部 `MapToCalled` 直接读取学生条目配置的 `LetterColor`（仅允许 `"gold"`、`"blue"`，其余回退为 `"rainbow"`）。
  - **边界兜底**: 若候选名单为空、清洗后无有效姓名或 `targetCount <= 0`，直接返回空列表；禁止重复模式下请求人数超过名单总数时自动截断为名单总数。

---

### 2. 后端抽卡调度中枢初始化 (`BackendScheduler.Initialize`)
- **1. 标题**: `BackendScheduler.Initialize`
- **2. 用途**: 初始化后台事件总线监听，订阅 `Fcb`、`Frw`、`Cp` 模块上的 `IpcAction.Post` 上报事件，并维护最近一次抽取结果快照（`LastResults`、`LastDrawAt`）。
- **3. 参数**:
  - 无输入参数。
  - **返回值** (`void`)。
- **4. 注意事项**:
  - 内部具备 `_stateLock` 互斥锁与 12 秒自愈看门狗（Watchdog），杜绝演播窗口未正常关闭时并发触发二次抽取或永久卡死。

---

### 3. 抽卡触发、悬浮球坐标防抖与全窗口协同 (`BackendScheduler`)
- **1. 标题**: 抽卡状态机与关闭状态收敛
- **2. 用途**: 响应 `Fcb` 上报的 `RandomTriggered` 与 `PositionChanged`、以及 `Frw` 上报的 `WindowClosed` 与 `ErrorHappened`。
- **3. 核心流程**:
  - **触发抽卡 (`HandleFcbRandomTriggered`)**: 校验并发锁后，向 `Fcb` 与 `Lrw` 发送 `HideWindow` 隐藏悬浮球与旧暂留窗，调用 `LotteryEngine.Pick` 生成结果，再向 `Frw` 发送 `ShowWindow`（`FrwShowPayload`）。
  - **演播关闭收敛 (`HandleFrwWindowClosed`)**: 解除 `_isDrawing` 锁、缓存 `LastResults` 与 `LastDrawAt`，向 `Fcb` 发送 `ShowWindow` 恢复悬浮球；仅在 `props.ShowLastResult && keepResult` 时调用 `ShowLastResultWindow()` 唤起暂留窗，否则向 `Lrw` 发送 `HideWindow`。
  - **坐标防抖落盘 (`HandleFcbPositionChanged`)**: 立即更新 `ConfigCache` 中的悬浮球坐标，并在 **600ms** 防抖后写入 `config.yaml` 且向 `Cp` 发送 `FetchConfig`。
- **4. 注意事项**:
  - `ShowLastResultWindow()` 仅在 `!_isDrawing && _lastResults.Count > 0` 时向 `Lrw` 发送 `ApplyProps` 与 `ShowWindow`。

---

### 4. 配置校验落盘与全模块热更新 (`BackendScheduler.SaveAndApplyConfig`)
- **1. 标题**: `BackendScheduler.SaveAndApplyConfig`
- **2. 用途**: 集中处理前端配置保存请求。反序列化校验 YAML、同步更新当前激活班级文件、写入 `config.yaml`、更新 `ConfigCache.Current`，并向 `Fcb`、`Frw`、`Lrw` 广播热更新。
- **3. 参数**:
  - `yaml` (`string?`): 前端提交的最新配置 YAML 文本。
  - **返回值** (`bool`): 解析与同步是否成功；失败时向 `Cp` 发送 `OverwriteConfig` 回滚前端配置。
- **4. 注意事项**:
  - **样式同步防误弹出**: 保存配置时仅调用 `ApplyLastResultProps()` 向 `Lrw` 发送 `ApplyProps`（并在关闭暂留开关时发送 `HideWindow`），**绝不调用** `ShowWindow`，彻底避免保存配置时误弹暂留窗。

---

### 5. 多班级服务 (`ClassManagerService`)
- **1. 标题**: `ClassManagerService`
- **2. 用途**: 负责班级配置的本地读写与多班级管理。每个班级对应独立的 YAML 文件 `~/.blue random/classes/{classId}.yaml`。
- **3. 参数**:
  - `GetClassesDir()`: 获取并确保 `~/.blue random/classes` 目录存在。
  - `EnsureInitialized(RootConfig config)`: 初始化默认班级（`classes_default`）并与主配置 `ActiveClassId` / `StudentList` 执行双向校准。
  - `ListClasses()`: 扫描 `classes_*.yaml` 并返回所有班级列表（默认班级排在最前）。
  - `CreateClass(string name)`: 生成 `classes_xxxxxxxxx` 唯一 ID 并创建空班级文件。
  - `SaveClass(ClassConfig cls)` / `LoadClass(string classId)` / `RenameClass(string classId, string newName)` / `DeleteClass(string classId)`: 班级持久化 CRUD。
- **4. 注意事项**:
  - 切换当前激活班级由前端与 `NativeBridge.SaveConfigYaml` 协同更新 `ActiveClassId` 与 `StudentList`。

---

### 6. 演播音频与渐变播放器 (`ResultAudioPlayer`)
- **1. 标题**: `ResultAudioPlayer`
- **2. 用途**: 演播结果浮窗专属的音频驱动管理器。负责开箱抽取音效（SFX）与背景音乐（BGM）的解码缓存、循环回放及基于渲染帧的平滑音量淡入淡出。
- **3. 参数**:
  - `PlaySfx(PickResultDialogConfig props)`: 优先从 `CustomsStore` 读取自定义音效，缺失时回退至内置 `button_click.wav`，写入 `%TEMP%\BlueRandom_Audio\` 后通过 `MediaPlayer` 播放。
  - `PlayBgm(PickResultDialogConfig props)`: 从 `CustomsStore` 读取自定义 BGM，设置起始位置 `BgmStartTime` 与淡入时长 `BgmFadeDuration` 并循环播放。
  - `Update()`: 每帧（约 16ms）推进 BGM 淡入/淡出音量插值计算。
  - `FadeOut()`: 标记启动 BGM 淡出动画。
  - `StopBgmImmediate()` / `Dispose()`: 立即切断播放并释放 `MediaPlayer` 资源。
- **4. 注意事项**:
  - 使用 `DCompResultWindow` 的渲染 tick 推进音量插值，无需额外多媒体定时器。
  - 在退出窗口或关闭演播时，务必调用 `Dispose()` 释放底层 `MediaPlayer`。

