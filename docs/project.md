# Blue Random（蔚蓝点名）项目知识库

> 本文件是**本地维护**的 AI/开发者上下文文档.
> 最后整理：基于 `NEXT-Dev` 分支当前工作区（含未提交的 DComp 迁移改动）。

---

## 1. 项目速览

| 项 | 内容 |
| :--- | :--- |
| 名称 | 蔚蓝点名 / Blue Random |
| 定位 | 仿《蔚蓝档案》学员招募动画的桌面随机点名工具 |
| 版本 | `26.10.04`，代号 `Midori`（来源：[package.json](../package.json)） |
| 许可 | AGPLv3；`public/` 下的图片与音效版权归各自所有方，BGM/音效不随仓库分发 |
| 技术栈 | .NET 10 (C# / WPF) + WebView2 + Vue 3 + Vite + riz-ui |
| 历史 | 早期为 Electron 项目（目录名仍为 `ba_random_electron`），现已整体重构为单进程 C# 宿主 |

核心形态：

- **原生悬浮球**（FCB）：桌面透明置顶小按钮 + 1~10 抽胶囊选择器；
- **原生结果演播窗**（FRW）：DirectComposition + D3D11 + Direct2D 全屏透明窗口；
- **WebView2 配置面板**（CP）：Vue 3 管理后台；
- **单进程多 STA 线程**：渲染 / 消息循环互不阻塞。

---

## 2. 运行数据目录

当前代码默认写入 `%UserProfile%\.blue random`（见 [YamlConfigService.GetUserDataDir](../src-csharp/Services/YamlConfigService.cs)），可用环境变量 `BLUERANDOM_USER_DATA_DIR` 覆盖。

```
~/.blue random/
├── config.yaml      主配置（带中文注释，可手工编辑）
├── classes/         多班级名单 (*.yaml)
├── customs/         用户上传的音效 / BGM / 图标 (*.yml)
├── .SHA256          管理员密码哈希（启用密码锁后存在）
├── log.txt          统一运行日志（冷启动重置）
├── EBWebView/       配置面板 WebView2 用户数据
├── EBWebView_FRW/   旧版 WebView2 结果窗遗留目录（DComp 化后不再使用）
└── uiaccess.dll     运行期释放的 UIAccess 提权依赖
```

> 注意：旧版本曾使用 `%LOCALAPPDATA%\BlueRandom`，机器上可能同时存在旧目录，排查配置问题时要先确认实际路径。

---

## 3. 目录结构

```
├── docs/                   架构与 IPC 协议文档（ARCHITECTURE.md / ipc.md）
├── public/                 静态素材（字体、图片、音效），编译时内嵌
├── src/
│   └── renderer/           Vue 3 配置面板（Vite 构建到 dist/）
│       ├── bridge/         nativeBridgeAdapter.js：WebView2 宿主对象封装
│       ├── composables/    useConfigPanel.js：配置草稿与全部业务操作
│       ├── views/          ConfigPanel + 5 个 Tab + TabLogs + PickResult(遗留)
│       └── router/         hash 路由：/config-panel、/pick-result
├── src-csharp/
│   ├── Core/
│   │   ├── Config/         ConfigModel / ConfigCache
│   │   ├── Ipc/            IpcBroker / IpcMessages
│   │   ├── Lottery/        LotteryEngine（纯领域算法）
│   │   ├── Resources/      ResourceCache（字体/信封/音频内存缓存）
│   │   ├── Scheduler/      BackendScheduler（状态机 + 防抖落盘 + 广播）
│   │   └── Logging/        AppLogger
│   ├── Services/           ClassManager / CustomsStore / Security / Tray /
│   │                       Update / WebViewEnvironment / WindowManager /
│   │                       WindowsSystem / YamlConfigService
│   └── UI/
│       ├── FCB/            悬浮按钮（WPF）
│       ├── FRW/            结果浮窗（DCompResultWindow 现役；ResultWindow 遗留）
│       ├── LRW/            结果暂留浮窗（LastResultWindow，右上角常驻）
│       ├── CP/             配置面板宿主 + NativeBridge
│       └── Splash/         启动器窗口与五阶段流水线
├── tests/BlueRandom.Tests/ xUnit 单元测试
├── third_party/            uiaccess.dll（RunUIAccess, MIT）
├── publish/                单文件发布输出
└── BlueRandom.sln(x)       .NET 解决方案
```

---

## 4. 线程模型

`WindowManager.InitializeAllThreads()` 拉起三个独立 STA UI 线程 + 托盘：

| 线程 | 承载 | 说明 |
| :--- | :--- | :--- |
| Backend | `IpcBroker` + `BackendScheduler` | 消费 `Channel<IpcMessage>`，驱动抽卡状态机、配置缓存、防抖保存 |
| `FCB_UI_Thread` | `FloatingButtonWindow` | WPF 透明置顶悬浮球，独立 `Dispatcher.Run()` |
| `FRW_UI_Thread` | `DCompResultWindow` | DirectComposition 结果窗，自有 Win32 消息循环 |
| `LRW_UI_Thread` | `LastResultWindow` | 右上角结果暂留窗，按需 `Show()`，`ShowActivated=False` |
| `WebUI_Thread` | `ConfigPanelWindow` + WebView2 | 配置面板，首次打开时才初始化 WebView2 |
| 主线程 | WPF `Application` | 启动流水线 / Splash / 全局异常兜底 |

关键约束：

- 每个 `CoreWebView2Environment` 只能在其所属 STA 线程使用；
- FRW 与 CP 使用**不同**的 userDataFolder，避免进程互相影响；
- `WH_MOUSE_LL` 鼠标钩子必须安装在**专职消息泵线程**，不能放在 60fps 渲染线程上（否则回调会被饿死）。

---

## 5. 启动流水线

[StartupPipeline.cs](../src-csharp/UI/Splash/StartupPipeline.cs) 的 Stage 0~5：

1. **Stage 0**：初始化 `AppLogger`、解析基准路径；
2. **Stage 1**：管理员 / UIAccess 权限仲裁；通过 `--elevatedadmin` / `--elevateduia` 标记防止提权死循环；
3. **Stage 2**：加载并校验 `config.yaml`，协议版本迁移，损坏时提供自愈恢复；
4. **Stage 3**：`ResourceCache.Preload` 预热字体、信封纹理、音频；
5. **Stage 4**：启动 `IpcBroker`、`BackendScheduler`、`WindowManager` 与托盘；
6. **Stage 5**：Splash 淡出，移交控制权。

`App.xaml.cs` 中还有两个独立调试入口：

- `BLUERANDOM_DCOMP_FRW_DEMO=1`：跳过启动流水线，只跑结果窗 Demo（10 名测试学生）；
- `BLUERANDOM_DCOMP_PROBE=1`：运行 DirectComposition 透明窗口技术原型。

---

## 6. 进程内通信（IPC）

细节见 [docs/ipc.md](../docs/ipc.md)，此处只记要点。

### 6.1 模型

```csharp
enum IpcAction { Init, Dest, Get, Post }
enum IpcModule { Ipc, Fcb, Frw, Cp }

sealed class IpcMessage
{
    IpcAction Action; IpcModule Module;
    string Command; object? Payload; DateTime Timestamp;
}
```

- `IpcBroker.Send(msg)`：非阻塞写入 `Channel<IpcMessage>`；
- `IpcBroker.Subscribe(module, handler)`：模块在构造函数中注册；
- `GET` = 后端下发给 UI 的控制指令；`POST` = UI 上报给后端的事件。

### 6.2 常用命令

| 模块 | 命令 | 方向 | 载荷 |
| :--- | :--- | :--- | :--- |
| FCB | `RandomTriggered` | POST | `int`（1~10） |
| FCB | `PositionChanged` | POST | `FcbPositionPayload` |
| FCB | `HideWindow` / `ShowWindow` | GET | `null` |
| FCB | `ApplyNewProp` | GET | `FloatingButtonConfig` |
| FRW | `ShowWindow` | GET | `FrwShowPayload`（人数 + 学生列表） |
| FRW | `WindowClosed` | POST | `null` |
| FRW | `ApplyNewProp` | GET | `PickResultDialogConfig` |
| CP | `ShowConfigPanel` | GET | `null` |
| CP | `ConfigSaved` | POST | YAML 字符串 |
| CP | `OverwriteConfig` | GET | YAML 字符串 |
| CP | `FetchConfig` | GET | YAML 字符串 |

### 6.3 抽卡状态机

`BackendScheduler` 维护 `_isDrawing` 互斥：

1. FCB 上报 `RandomTriggered` → 校验状态 → 调用 `LotteryEngine.Pick`；
2. 向 FCB 下发 `HideWindow`，向 FRW 下发 `ShowWindow`；
3. FRW 关闭后上报 `WindowClosed` → 重置状态 → 再次 `ShowWindow` 唤醒悬浮球；
4. 12 秒看门狗兜底，防止丢帧导致悬浮球永久隐藏。

---

## 7. 配置模型与持久化

### 7.1 模型（[ConfigModel.cs](../src-csharp/Core/Config/ConfigModel.cs)）

`RootConfig`：

- `protocolVersion`、`studentList`、`allowRepeatDraw`、`agreedEula`、`activeClassId`；
- `floatingButton`：尺寸、透明度、置顶、任务栏、坐标、自定义图标、图标尺寸、边框色；
- `pickCountDialog`：默认抽取人数；
- `pickResultDialog`：音效开关/音量、BGM 引用/音量/起点/淡入淡出、面板透明度/底色/边框色、`showLastResult`（结果暂留浮窗开关）、`lastResultBgColor` / `lastResultBorderColor` / `lastResultOpacity` / `lastResultAutoCloseSeconds`（暂留浮窗独立外观与自动关闭，0 = 不自动关闭）；
- `admin`：管理员自启、任务名、启动要求、UIAccess 开关。

`StudentItem`：`name`、`weight`（默认 1.0）、`letterColor`（`rainbow` / `gold` / `blue`）。

### 7.2 内存缓存

`ConfigCache.Current` 是唯一生效配置，读取无锁；写入通过 `ConfigCache.Mutate(...)` 原子替换。

### 7.3 落盘

- 前端点"应用"→ `NativeBridge.SaveConfigYaml` → 校验 + `ClassManagerService` 同步 → 写盘 → 广播 `ApplyNewProp`；
- 悬浮球拖拽结束 → `ConfigCache` 立即更新 → **约 600ms 防抖**后写盘；
- 写盘使用 `YamlConfigService.ToYamlWithComments` 生成带中文注释的 YAML；
- 数值在 `YamlConfigService` 中统一 Clamp（音量 0~100、透明度 0.1~1.0 等）。

### 7.4 班级与自定义资源

- 班级：`ClassManagerService` 管理 `classes/*.yaml`，当前班级由 `activeClassId` 指定；
- 自定义资源：`CustomsStore` 管理 `customs/*.yml`，内含 base64、文件名、MIME、用途（`bgm` / `sfx` / `icon`）、时长；
- 资源被删除或失效时，`CustomsStore` 会自动清理配置里悬空的 id。

---

## 8. 抽卡算法（[LotteryEngine.cs](../src-csharp/Core/Lottery/LotteryEngine.cs)）

### 8.1 允许重复

- 权重增强：`w' = max(0, w) ^ 1.5`；
- 加权轮盘赌：累加总权重 `W`，取 `R ∈ [0, W)`，线性扫描前缀和；
- 全零权重退化为 1/N 均匀随机。

### 8.2 禁止重复

- Efraimidis–Spirakis A-Res：`k_i = -ln(U_i) / w_i`，取最小的 K 个；
- 零权重学生放入候补池，用 Fisher-Yates 洗牌后补齐；
- 候选人数不足时安全返回全量。

### 8.3 品阶

`letterColor` 直接映射信封素材：`rainbow`（彩）、`gold`（金）、`blue`（蓝）。

---

## 9. 结果浮窗（DCompResultWindow，现役）

[文件](../src-csharp/UI/FRW/DCompResultWindow.cs)

### 9.1 窗口与渲染

- 窗口类：`BlueRandomDCompResult`，`WS_POPUP` + `WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP`；
- 图形：D3D11 → D2D → DirectComposition；
- **单张 `IDCompositionSurface` + 单个 Visual**，每帧即时模式重绘全部图层（代码注释说明：多 surface 在部分驱动上会出现只合成少数图层的问题，不要改回多 surface）；
- 图层位图用 WPF 一次性预渲染（面板 / 信封+阴影 / 姓名卡 / 提示条），转 `Pbgra32` 后上传为 D2D 位图；动画只改变换矩阵与不透明度。

### 9.2 动画与交互

- 阶段：`Idle → Opening → Ready → Closing`；
- 16ms `System.Threading.Timer` 投递 `WM_TIMER`，用 `_tickPending` 合并积压帧；
- 就绪后暂停计时器省电；
- 全局 `WH_MOUSE_LL` 钩子（专职线程）在就绪后吞掉任意位置点击并关闭窗口；`Esc` 也可关闭；
- 音频：WPF `MediaPlayer`，自定义音频先落到 `%TEMP%\BlueRandom_Audio` 再播放。

### 9.3 光标（2026-10-18 修复）

**根因**：`SetCursor` 需要 `LoadCursorW` 返回的真实 `HCURSOR`。旧代码把 `IDC_ARROW (32512)` / `IDC_HAND (32649)` 资源 ID 直接传给 `SetCursor`，调用始终返回 `NULL` 并静默失败；同时 `WM_SETCURSOR` 又直接 `return 1` 跳过了 `DefWindowProc`，窗口类光标永远没有机会生效，于是结果窗一直沿用系统启动时继承的 `IDC_WAIT`（转圈）。

**修复**：

- `ArrowCursor` / `HandCursor` 属性缓存 `LoadCursorW` 的真实句柄；
- 新增 `RefreshCursor()`，统一在 `WM_SETCURSOR`、`WM_MOUSEMOVE`、`ProbeBusyCursor()`、`OnTick()` 每帧渲染后调用；
- `FinishClose()` 隐藏窗口前恢复箭头；
- 已用外部脚本采样 `GetCursorInfo` 验证：动画期间 `IDC_ARROW`，就绪后 `IDC_HAND`，不再出现 `IDC_WAIT`。

> 排障提示：结果窗窗口线程是**完全响应**的（`SendMessageTimeout(WM_NULL)` 延迟 0ms），所以这类"忙光标"不是 UI 线程卡死，先查光标句柄是否有效。

### 9.4 结果暂留浮窗（LRW）

- 代码：[LastResultWindow.xaml](../src-csharp/UI/LRW/LastResultWindow.xaml) / [LastResultWindow.xaml.cs](../src-csharp/UI/LRW/LastResultWindow.xaml.cs)
- 开关：`pickResultDialog.showLastResult`（配置面板「结果浮窗 → 结果暂留浮窗」）
- 数据流：
  1. `DCompResultWindow.FinishClose()` 把 `_students` 作为 `WindowClosed` 的载荷上报；
  2. `BackendScheduler.HandleFrwWindowClosed` 缓存为 `LastResults`，调用 `RefreshLastResultWindow()`；
  3. 开关开启时通过 `@LRW ShowWindow`（`LrwShowPayload`）显示；新抽取开始时先发 `HideWindow`；
  4. 配置保存/重置后 `RefreshLastResultWindow()` 会按新开关值立即显示或隐藏。
- 窗口固定在主屏工作区右上角（16 DIP 边距），`ShowActivated=False` 不抢焦点；✕ 只隐藏窗口，不改变配置。
- 姓名左侧只显示品阶圆点（彩 = 紫、金 = 金、蓝 = 蓝），圆点内无文字；底部固定显示操作提示，不被名单覆盖。
- 外观独立于主结果窗：`lastResultBgColor` / `lastResultBorderColor` / `lastResultOpacity`；`lastResultAutoCloseSeconds > 0` 时底部显示逐秒倒计时，归零自动淡出隐藏（0 = 不自动关闭）。
- `NativeBridge.GetLastResultsJson()` 现在读取 `BackendScheduler.LastResults`（不再依赖遗留的 `ResultWindow`）。

---

## 10. 配置面板（Vue 3）

- 入口：[main.js](../src/renderer/main.js) → `setupNativeBridgeAdapter()` → 挂载路由；
- 路由：`/config-panel`（[ConfigPanel.vue](../src/renderer/views/ConfigPanel.vue)）、`/pick-result`（遗留 Web 版结果页，原生 FRW 已不依赖）；
- 桥接：[nativeBridgeAdapter.js](../src/renderer/bridge/nativeBridgeAdapter.js) 把 `chrome.webview.hostObjects.nativeBridge` 封装成 `window.configPanelApi`；
- 状态：[useConfigPanel.js](../src/renderer/composables/useConfigPanel.js) 持有 `draft`（配置草稿）、Tab 导航、tooltip 和全部操作；
- 页面：TabRoster（名单/权重）、TabFloating（悬浮球）、TabResult（结果窗/音效/BGM）、TabAdvanced（权限/自启/密码/更新）、TabAbout、TabLogs（右侧常驻日志）；
- 保存链路：`draft` → `saveConfig` → `NativeBridge.SaveConfigYaml` → 后端校验并广播。

### NativeBridge API 分组

| 分组 | 方法 |
| :--- | :--- |
| 配置 | `GetConfigYaml` / `SaveConfigYaml` / `ResetConfig` / `ResetAllConfig` |
| 班级 | `ListClassesJson` / `CreateClass` / `RenameClass` / `DeleteClass` / `LoadClassJson` / `SaveClassJson` |
| 自定义资源 | `SaveCustomResource` / `GetCustomResourceJson` / `DeleteCustomResource` / `GetCustomDataUrl` / `PickFile` / `OpenConfigFolder` / `ClearCache` |
| 应用信息 | `GetAppInfoJson` / `CheckUpdateJson` / `GetChangelog` |
| 权限与系统 | `CreateStartupTask` / `AdminElevate` / `Restart` / `CloseWindow` / `OpenDevTools` / `OpenUrl` |
| 安全 | `IsSecurityEnabled` / `VerifyPassword` / `SetPassword` / `DisablePassword` |
| 悬浮球 | `GetFloatingPositionJson` |
| 日志 | `GetLogsJson` / `Log` |
| 结果窗 | `GetPickResultConfigJson` / `GetLastResultsJson` / `ClosePickResult` |

---

## 11. 构建、运行与测试

```powershell
# 前端依赖（riz-ui 从 GitHub tarball 安装）
npm install

# 构建 Vue 配置面板 -> dist/（Release 发布前必须执行）
npm run build

# 编译 C#（Debug 开发）
dotnet build BlueRandom.slnx -c Debug

# 单元测试
dotnet test tests\BlueRandom.Tests\BlueRandom.Tests.csproj

# 一键发布单文件 exe -> publish/
npm run build:app
```

重要：`BlueRandom.csproj` 通过 `EmbeddedResource` 内嵌 `public/**`、`dist/**`、`package.json`、`changelog.md`、`third_party/uiaccess.dll`。若未先构建 `dist`，发布产物会缺少配置面板页面。

### 调试用环境变量

| 变量 | 作用 |
| :--- | :--- |
| `BLUERANDOM_USER_DATA_DIR` | 覆盖用户数据目录 |
| `BLUERANDOM_DCOMP_FRW_DEMO=1` | 只运行结果窗 Demo（10 名测试学生） |
| `BLUERANDOM_DCOMP_PROBE=1` | 运行 DComp 技术原型窗口 |
| `BLUERANDOM_DCOMP_DUMP=1` | 导出当前结果窗帧到 `%TEMP%\br_frw_dump.png` |

### 测试覆盖（[tests/BlueRandom.Tests](../tests/BlueRandom.Tests)）

`LotteryEngineTests`、`YamlConfigServiceTests`、`ClassManagerTests`、`CustomsStoreTests`、`IpcBrokerTests`、`UpdateServiceTests`、`WindowsSystemTests`、`ArcGeometryTests`。当前基线：**49 例全部通过**。

---

## 12. 常见改动路径

### 新增一个配置项

1. [ConfigModel.cs](../src-csharp/Core/Config/ConfigModel.cs)：加属性 + `YamlMember`；
2. [YamlConfigService.cs](../src-csharp/Services/YamlConfigService.cs)：默认模板、`ToYamlWithComments`、必要的 Clamp/迁移；
3. 需要前端读写时：`nativeBridgeAdapter.js` 或 `GetConfigYaml` 已自动覆盖；
4. [useConfigPanel.js](../src/renderer/composables/useConfigPanel.js)：`draft` 映射与默认值；
5. 对应 Tab 组件中加 UI。

### 新增一条 IPC 命令

1. `IpcMessages.cs` 定义载荷类型；
2. `BackendScheduler` 的对应模块处理器中 `switch` 增加分支；
3. 目标 UI 在 `OnIpcMessageReceived`/`HandleIpc` 中处理；
4. 若涉及状态机，务必同步更新 `_isDrawing` 与看门狗逻辑。

### 新增一个配置 Tab

1. `views/TabXxx.vue`；
2. `ConfigPanel.vue` 的 Tab 列表与主题色映射；
3. `useConfigPanel.js` 的 slider/tooltip 状态；
4. 需要独立全局样式时，在 `App.vue` 的 `watch(route.path)` 中处理。

### 新增自定义资源类型

1. `CustomsStore` 的 `purpose` 约定与校验；
2. `NativeBridge.SaveCustomResource` 参数；
3. 前端上传/试听/移除流程（参考 `TabResult.vue` 的 staged 暂存机制：应用时才落盘）。

---

## 13. 已知坑与注意事项

1. **`docs/ARCHITECTURE.md` 已部分过时**：它仍把 FRW 描述为 WPF `ResultWindow`，实际现役实现是 `DCompResultWindow`。
2. **未纳入版本控制的文件**：`src-csharp/UI/FRW/DCompResultWindow.cs`、`DCompProbe.cs`、`public/image/Letter_*_Shadow.png` 在 `NEXT-Dev` 工作区中仍是未跟踪状态，提交时勿遗漏（否则他处拉取后编译失败）。
3. **旧实现保留**：`ResultWindow.xaml(.cs)`、`src/renderer/views/PickResult.vue` 已不再由 `WindowManager` 使用，改动前先确认调用点。
4. **资源路径**：WebView2 以 `file://` 加载页面，Vue 中引用 `public/` 资源必须走 `resolveAssetUrl`，不要用 `/image/...` 绝对路径。
5. **`SetCursor` 只能收真实 HCURSOR**，`IDC_*` 只是 `LoadCursor` 的入参；这是本次忙光标问题的根因。
6. **`WH_MOUSE_LL` 钩子线程必须保持专职泵消息**，不要合并到渲染线程。
7. **YAML 注释是产品特性**：手工编辑 `config.yaml` 是预期用法，保存时必须用 `ToYamlWithComments`，不要用裸序列化覆盖。
8. **配置迁移**：`protocolVersion` 与 `package.json` 的 `codename` 绑定，改协议版本要同时考虑 `YamlConfigService` 的迁移分支与测试。
9. **版权**：`public/` 下 Blue Archive 素材与字体有独立授权，BGM/抽取音效由用户自行上传，禁止把版权音频提交进仓库。
10. **日志**：`AppLogger.Initialize` 每次冷启动会重置 `log.txt`，不要把需要长期保留的信息只写进日志。

---

## 14. 维护记录

| 日期 | 内容 |
| :--- | :--- |
| 2026-10-18 | 修复结果浮窗动画期间 `IDC_WAIT` 忙光标：`SetCursor` 改用真实 `HCURSOR` 句柄，并在每帧渲染后刷新；新增本知识库与 `.gitignore` 规则。 |
| 2026-10-18 | 新增「结果暂留浮窗」：`showLastResult` 开关 + LRW 独立线程，抽取结束后在屏幕右上角显示上一轮结果，可关闭，新抽取开始时自动隐藏；支持独立底色/边框/不透明度、自动关闭倒计时（0 = 不自动关闭），姓名左侧仅显示品阶圆点（彩 = 紫）。 |
