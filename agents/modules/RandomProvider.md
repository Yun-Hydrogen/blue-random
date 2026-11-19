# RandomProvider 模块说明文档

## 概述
`RandomProvider` 模块位于 `Main/Modules/RandomProvider`，是 Blue Random 应用程序的随机抽取核心领域引擎与抽卡业务协调中心。本模块包含纯领域加权无偏随机抽样算法（`LotteryEngine`）、跨窗口调度与防抖落盘状态机（`BackendScheduler`）、多班级名单配置持久化与切换管理（`ClassManagerService`）、以及抽卡演出专属的高性能音频与 BGM 渐变播放器（`ResultAudioPlayer`）。

---

## 功能清单

### 1. 核心加权抽卡算法 (`LotteryEngine.Pick`)
- **1. 标题**: `LotteryEngine.Pick`
- **2. 用途**: 根据候选名单、预设抽取人数以及是否允许重复抽取的开关，执行高随机度的数学无偏加权抽样，为每位被抽取的学生计算并绑定信封品阶（三阶彩色、二阶金色、一阶蓝色）。
- **3. 参数**:
  - `pool` (`IReadOnlyList<StudentItem>`): 候选学生名单列表，每项包含姓名与非负浮点权重。
  - `count` (`int`): 本轮请求抽取的学生人数（通常为 1-10 人）。
  - `allowDuplicate` (`bool`): 是否允许同一个人在同一轮抽卡中被重复抽取。
  - **返回值** (`List<CalledStudent>`): 抽取命中的学生实体结果列表，包含 `Name`、`Weight` 以及计算后的品阶颜色 `LetterColor`（"rainbow" / "gold" / "blue"）。
- **4. 注意事项**:
  - **允许重复模式**: 采用经典的 Roulette Wheel Selection（轮盘赌加权抽样），每次独立从全名单按权重概率随机抽取 1 人，连抽 `count` 次。
  - **禁止重复模式**: 采用学术级标准 **A-Res (Efraimidis-Spirakis) 加权无放回抽样算法**。为名单中每个学生生成特征键 $k_i = u_i^{1 / w_i}$（其中 $u_i \sim \text{Uniform}(0, 1)$，$w_i$ 为权重），随后取键值最大的前 $k$ 位学生作为结果。该算法在数学上严格证明了加权无放回选取的公平性与独立性。
  - **边界兜底**: 若候选名单为空，直接返回空列表；若禁止重复模式下请求人数超过了名单池总人数，自动降级为返回全量学生名单打乱结果，绝不发生下标越界或死循环。

---

### 2. 信封品阶映射 (`LotteryEngine.MapLetterColor`)
- **1. 标题**: `LotteryEngine.MapLetterColor`
- **2. 用途**: 仿《蔚蓝档案》招募系统将抽取结果按概率与权重映射为三档信封视觉品阶：三星彩信封（Rainbow）、二星金信封（Gold）与一星蓝信封（Blue）。
- **3. 参数**:
  - `weight` (`double`): 学生当前配置的权重值。
  - `avgWeight` (`double`): 名单池中全体学生的平均权重。
  - **返回值** (`string`): 信封品阶标识字符串（`"rainbow"`、`"gold"` 或 `"blue"`）。
- **4. 注意事项**:
  - 算法结合了基础稀有度概率分布（3% 彩信封、18.5% 金信封、78.5% 蓝信封）与学生个体权重偏移，实现既有保底欧气刺激感，又符合统计学权重期望。

---

### 3. 后端抽卡调度中枢初始化 (`BackendScheduler.Initialize`)
- **1. 标题**: `BackendScheduler.Initialize`
- **2. 用途**: 初始化后台事件总线监听，订阅悬浮按钮发出的 `RandomTriggered` 抽取请求，并管理抽取历史记录。
- **3. 参数**:
  - 无输入参数。
  - **返回值** (`void`)。
- **4. 注意事项**:
  - 内部具备原子状态锁，杜绝演播窗口未完全关闭前并发触发二次抽取导致画面撕裂或音频冲突。

---

### 4. 触发抽卡与全窗口协同 (`BackendScheduler.TriggerDraw`)
- **1. 标题**: `BackendScheduler.TriggerDraw`
- **2. 用途**: 响应悬浮按钮的点击抽取指令。从当前活跃班级或全局名单拉取池子，调用 `LotteryEngine.Pick` 生成抽取结果，并向 DirectComposition 演播窗（FRW）与结果暂留窗（LRW）分发展示。
- **3. 参数**:
  - `count` (`int`): 请求抽取人数。
  - **返回值** (`void`)。
- **4. 注意事项**:
  - 执行前会自动检查并向 `LRW` 发送 `HideWindow` 指令，隐藏上一轮的暂留结果。
  - 抽取完成后向 `FRW` 发送包含结果名单与演出配置的 `ShowWindow` IPC 载荷。

---

### 5. 配置防抖落盘与全模块同步 (`BackendScheduler.SaveAndApplyConfig`)
- **1. 标题**: `BackendScheduler.SaveAndApplyConfig`
- **2. 用途**: 集中处理前端配置保存请求。先同步更新内存单例 `ConfigCache`，然后向悬浮按钮、结果窗广播最新样式，并在 500ms 延迟防抖后写入物理磁盘 `config.yaml`。
- **3. 参数**:
  - `yaml` (`string?`): 前端提交的最新配置 YAML 文本。
  - **返回值** (`bool`): 解析与同步是否成功。
- **4. 注意事项**:
  - 采用防抖（Debounce）设计，用户在配置面板连续拖动滑块时不会触发频繁的磁盘 IO 写入。

---

### 6. 多班级服务 (`ClassManagerService`)
- **1. 标题**: `ClassManagerService`
- **2. 用途**: 负责班级配置的本地读写与多班级切换管理。每个班级对应独立的数据文件 `~/.blue random/classes/{classId}.json`。
- **3. 参数**:
  - `EnsureInitialized()`: 初始化班级存储目录与默认班级。
  - `GetAllClasses()`: 获取所有班级概要列表。
  - `SaveClass(ClassConfig config)`: 保存或更新指定班级信息。
  - `DeleteClass(string classId)`: 删除指定班级（至少保留一个活跃班级）。
  - `SetActiveClass(string classId)`: 切换当前激活班级。
- **4. 注意事项**:
  - 切换活跃班级后，自动同步刷新内存中的全局名单池。

---

### 7. 演播音频与渐变播放器 (`ResultAudioPlayer`)
- **1. 标题**: `ResultAudioPlayer`
- **2. 用途**: 演播结果浮窗专属的音频驱动管理器。负责开箱抽取音效（SFX）与背景音乐（BGM）的并发播放、循环回放及基于时间帧的平滑音量淡入淡出。
- **3. 参数**:
  - `PlaySfx(PickResultDialogConfig props)`: 根据配置从 `customs/` 或内置资源解码播放抽取音效。
  - `PlayBgm(PickResultDialogConfig props)`: 根据配置播放循环背景音乐，设置初始偏移时间与音量淡入参数。
  - `Update()`: 每帧（约 16ms）推进音量渐变计算，平滑调整 `MediaPlayer.Volume`。
  - `FadeOut()`: 标记启动 BGM 淡出动画。
  - `StopBgmImmediate()` / `Dispose()`: 立即切断播放并释放系统多媒体 COM 句柄。
- **4. 注意事项**:
  - 使用 DirectComposition 的渲染 tick 进行音量插值计算，无需依赖额外的多媒体定时器，避免跨线程竞态。
  - 在退出窗口或关闭演播时，务必调用 `Dispose()` 释放底层 `MediaPlayer`，避免内存泄露与音频残留。
