# Blue Random 系统架构与进程内 IPC 通信规范

本文档系统性说明 **Blue Random（蔚蓝点名）** 的进程与线程拓扑架构，以及基于 `System.Threading.Channels` 发布/订阅模型的进程内通信总线（`IpcBroker`）的通信协议规范、命令字字典与可视化交互时序流程。

---

## 一、 进程与线程拓扑全景

Blue Random 运行于单个 Windows 原生进程内，内部划分为 1 个后台控制调度与 IPC 分发中枢，以及 5 个职责独立的 STA（Single Thread Apartment）线程（4 个 UI 窗口线程 + 1 个系统托盘线程；演播窗就绪期间另按需拉起 1 个低级鼠标钩子线程 `Frw_MouseHook`）。

```mermaid
flowchart TD
    subgraph HostProcess ["Blue Random 宿主进程 (Host Process)"]
        Init["启动器流水线 (StartupPipeline)"]

        subgraph CoreThread ["核心调度与 IPC 分发 (BackendScheduler / IpcBroker)"]
            IpcBus["IpcBroker 通信总线 (Channel 后台分发任务)"]
            Scheduler["BackendScheduler (抽卡状态机)"]
            Engine["LotteryEngine (抽卡算法)"]
            CCache["ConfigCache (内存配置单例)"]
            RCache["ResourceCache (全内存静态资源)"]
            Debounce["防抖落盘器 (600ms)"]
        end

        subgraph FcbThread ["Fcb_UI_Thread (STA 线程)"]
            FCB["FloatingButtonWindow (悬浮按钮)"]
            FcbCtrl["FloatingButtonController"]
        end

        subgraph FrwThread ["Frw_UI_Thread (STA 线程)"]
            FRW["DCompResultWindow (DirectComposition 结果窗)"]
            Audio["ResultAudioPlayer (SFX / BGM)"]
            MouseHook["Frw_MouseHook (WH_MOUSE_LL 专职线程)"]
        end

        subgraph LrwThread ["Lrw_UI_Thread (STA 线程)"]
            LRW["LastResultWindow (结果暂留窗)"]
            LrwCtrl["LastResultAutoCloseController"]
            LrwTheme["LrwVisualTheme (混色引擎)"]
        end

        subgraph CpThread ["Cp_UI_Thread (STA 线程)"]
            CP["ConfigPanelWindow (配置面板宿主)"]
            WV2["ConfigPanelBrowserHost (WebView2)"]
            Bridge["NativeBridge (COM 桥接)"]
        end

        subgraph TrayThread ["Tray_UI_Thread (STA 线程)"]
            Tray["TrayService (NOTIFYICONDATA + Fluent 托盘菜单)"]
        end
    end

    Init --> CoreThread
    Init --> FcbThread
    Init --> FrwThread
    Init --> LrwThread
    Init --> CpThread
    Init --> TrayThread

    FcbThread <-->|IpcMessage| IpcBus
    FrwThread <-->|IpcMessage| IpcBus
    LrwThread <-->|IpcMessage| IpcBus
    CpThread <-->|IpcMessage| IpcBus
    TrayThread -->|IpcMessage| IpcBus
```

### 线程分工与职责说明
1. **核心调度与 IPC 分发 (`BackendScheduler` / `IpcBroker`)**: 承载程序生命周期、`IpcBroker` 后台异步分发循环（`Channel<IpcMessage>`）、抽卡状态机（含 12 秒防卡死看门狗）、加权抽卡算法（`LotteryEngine`）、线程安全内存配置缓存（`ConfigCache`）、全内存静态资源缓存（`ResourceCache`）与 600ms 磁盘防抖落盘器。
2. **悬浮按钮独立线程 (`Fcb_UI_Thread` - STA)**: 专职承载桌面常驻透明悬浮按钮（`FloatingButtonWindow`），运行极坐标圆弧排版、原生 `DragMove` 拖拽与边缘吸附、触控事件捕获与 `CubicEase` 贝塞尔弹性展开动效；注入 Win32 `WS_EX_NOACTIVATE` / `WS_EX_TOOLWINDOW` 扩展样式并拦截 `WM_MOUSEACTIVATE` 与 `WM_GETMINMAXINFO`，确保移动与点击悬浮球时不抢占前台全屏放映 PPT 的焦点且不触发左右分屏贴靠。
3. **结果演播窗独立线程 (`Frw_UI_Thread` - STA)**: 专职承载 DirectComposition + D3D11 + D2D 透明硬件加速演播窗（`DCompResultWindow`），自有 Win32 消息循环泵，并在就绪期间按需拉起专职 `Frw_MouseHook`（STA）线程运行 `WH_MOUSE_LL` 全屏点击钩子；在弹出前记录前台活动窗口并在关闭后自动精准归还焦点，保障翻页笔操作无缝连贯。
4. **结果暂留窗独立线程 (`Lrw_UI_Thread` - STA)**: 专职承载屏幕右上角常驻卡片（`LastResultWindow`），负责平滑滑入滑出动效与线性收缩自动关闭进度条。
5. **配置面板独立线程 (`Cp_UI_Thread` - STA)**: 唯一承载设置面板（`ConfigPanelWindow`）与 Microsoft WebView2 浏览器的 UI 线程，提供 `NativeBridge` COM 宿主直连对象，支持窗口隐藏式常驻秒开，杜绝 COM 跨套间异常。
6. **系统托盘独立线程 (`Tray_UI_Thread` - STA)**: 承载原生 Win32 `Shell_NotifyIcon` 隐藏消息接收窗口（`BlueRandomTraySink`）与 WPF Fluent 右键上下文菜单。

---

## 二、 进程内通信总线 (`IpcBroker`) 协议模型

### 1. 通信主体与动作语义
- **目标模块枚举 (`IpcModule`)**:
  - `Ipc`: 核心总线 / 全局控制目标（如全局 `Destroy` 指令）
  - `Fcb`: 悬浮按钮模块（`FloatingButtonWindow` 与 `BackendScheduler` 共同订阅）
  - `Frw`: 结果演播窗模块（`DCompResultWindow` 与 `BackendScheduler` 共同订阅）
  - `Lrw`: 结果暂留窗模块（`LastResultWindow` 订阅）
  - `Cp`: 配置面板宿主模块（`ConfigPanelWindow` 与 `BackendScheduler` 共同订阅）
  - 注：`BackendScheduler` 订阅 `Fcb`、`Frw`、`Cp` 上的 `IpcAction.Post` 上报事件；各视图窗口处理发往自身模块的 `IpcAction.Get` / `IpcAction.Destroy` 指令；`TrayService` 作为消息发送方投递指令，不作为 `IpcModule` 枚举成员。

- **动作语义 (`IpcAction`)**:
  - `Init`: 初始化动作（预留）
  - `Destroy`: 全局终止指令，要求目标模块安全释放资源并关闭窗口/消息循环
  - `Get`: 下行控制/同步指令（由调度器、托盘或桥接层发往目标 UI 模块，如 `ShowWindow`、`ApplyProps`、`FetchConfig`）
  - `Post`: 上行事件上报（由 UI 模块或桥接层上报给 `BackendScheduler`，如 `RandomTriggered`、`PositionChanged`、`WindowClosed`、`ConfigSaved`）

- **标准消息封包 (`IpcMessage`)**:
  - `Action` (`IpcAction`): 动作谓词。
  - `Target` (`IpcModule`): 目标接收模块。
  - `Command` (`string`): 命令字标识符。
  - `Payload` (`object?`): 强类型数据载荷。

---

## 三、 命令字与消息载荷映射字典

| 发送方 | 目标 (`Target`) | Action | Command | Payload 类型 | 业务语义 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `Fcb` | `Fcb` (`BackendScheduler`) | `Post` | `RandomTriggered` | `int` | 用户在悬浮球点击确认，请求抽取指定人数 (1-10) |
| `Fcb` | `Fcb` (`BackendScheduler`) | `Post` | `PositionChanged` | `FcbPositionPayload` | 悬浮球被拖拽后上报最新屏幕坐标 (X, Y) |
| `Fcb` / `Frw` / `Cp` | 对应模块 (`BackendScheduler`) | `Post` | `ErrorHappened` | `string` | 模块运行期异常上报（`Frw` 异常时触发状态自愈） |
| `BackendScheduler` | `Fcb` | `Get` | `HideWindow` | `null` | 抽卡开始时暂时隐藏悬浮按钮 |
| `BackendScheduler` / `Tray` | `Fcb` | `Get` | `ShowWindow` | `null` | 抽卡结束或托盘菜单点击“显示悬浮球”时恢复显示并刷新置顶 |
| `BackendScheduler` / `NativeBridge` | `Fcb` | `Get` | `ApplyProps` | `FloatingButtonConfig` | 热更新悬浮球尺寸、透明度、边框、图标与位置 |
| `BackendScheduler` / `NativeBridge` | `Fcb` | `Get` | `ApplyDefaultPickCount` | `int` | 热更新悬浮球默认抽取人数 |
| `Fcb` (预留) | `Fcb` | `Get` | `MoveNewPosition` | `FcbPositionPayload` | 指示悬浮球移动至指定坐标并执行边缘吸附 |
| `BackendScheduler` | `Frw` | `Get` | `ShowWindow` | `FrwShowPayload` | 指示演播窗全屏弹出并播放抽卡动画 |
| `BackendScheduler` / `NativeBridge` | `Frw` | `Get` | `ApplyProps` | `PickResultDialogConfig` | 广播结果窗配置更新（`DCompResultWindow` 展示时亦直接读取 `ConfigCache.GetFrwProps()`） |
| `Frw` | `Frw` (`BackendScheduler`) | `Post` | `WindowClosed` | `FrwClosedPayload` | 演播窗关闭，上报本轮抽取名单与用户是否主动点击暂留 |
| `BackendScheduler` | `Lrw` | `Get` | `ShowWindow` | `LrwShowPayload` | 指示暂留窗在屏幕右上角滑入展示抽取结果 |
| `BackendScheduler` | `Lrw` | `Get` | `HideWindow` | `null` | 启动新抽卡、未选择暂留或关闭暂留开关时指示暂留窗滑出隐藏 |
| `BackendScheduler` | `Lrw` | `Get` | `ApplyProps` | `PickResultDialogConfig` | 热更新暂留窗底色、边框色、不透明度与自动关闭秒数 |
| `Tray` | `Cp` | `Get` | `ShowConfigPanel` | `null` | 托盘左键点击或右键菜单“配置面板”唤起配置面板 |
| `BackendScheduler` | `Cp` | `Get` | `FetchConfig` | `string` (YAML) | 悬浮球坐标防抖落盘后向前端推送最新配置 YAML |
| `BackendScheduler` | `Cp` | `Get` | `OverwriteConfig` | `string` (YAML) | 配置校验失败时将缓存中的有效配置回滚覆写给前端 |
| `NativeBridge` | `Cp` (`BackendScheduler`) | `Post` | `ConfigSaved` | `string` (YAML) | 预留：前端保存配置事件上报（当前主要由 `NativeBridge.SaveConfigYaml` 直接调用 `BackendScheduler.SaveAndApplyConfig`） |
| `NativeBridge` | `Cp` (`BackendScheduler`) | `Post` | `ConfigReset` | `null` | 通知调度器执行恢复出厂默认配置并重启 |
| `Tray` / `BackendScheduler` | `Ipc` (及各模块) | `Destroy` | `Destroy` | `null` | 退出程序指令，通知目标模块释放资源并关闭窗口 |

---

## 四、 核心业务通信时序流程图

### 1. 抽卡全生命周期交互时序图
```mermaid
sequenceDiagram
    autonumber
    actor User as 用户 (教师)
    participant FCB as 悬浮按钮 (FCB)
    participant Core as 核心调度 (BackendScheduler)
    participant LRW as 结果暂留窗 (LRW)
    participant FRW as 演播结果窗 (FRW)

    User->>FCB: 点击展开选择器，选定人数后点击 ✓
    FCB->>FCB: 校验人数范围并在本地收缩胶囊
    FCB->>Core: [Post] @Fcb.RandomTriggered (count)
    activate Core
    Core->>Core: 检查防并发锁 (12s 看门狗) 与名单池
    Core->>Core: LotteryEngine.Pick 计算抽取结果与品阶
    Core->>FCB: [Get] @Fcb.HideWindow (暂时隐藏悬浮球)
    Core->>LRW: [Get] @Lrw.HideWindow (通知旧暂留窗退场)
    LRW-->>LRW: 向右滑出隐藏
    Core->>FRW: [Get] @Frw.ShowWindow (FrwShowPayload)
    deactivate Core

    activate FRW
    FRW->>FRW: 暂存当前前台窗口句柄 (GetForegroundWindow)
    FRW->>FRW: 全屏置顶并启动 DirectComposition 渲染
    FRW->>FRW: 播放开箱入场、信封掉落与姓名卡动画
    FRW->>FRW: ResultAudioPlayer 播放抽卡音效与淡入 BGM
    FRW->>FRW: 到达 Ready 状态，启用 WH_MOUSE_LL 钩子吞点击，显示提示胶囊

    alt 开启暂留且点击底部横条「点击此处暂留结果」
        User->>FRW: 点击底部提示横条
        FRW->>FRW: 判定命中横条区域，设置 keepResult = true
    else 点击其他区域或按 Esc
        User->>FRW: 点击屏幕任意其他位置或按 Esc
        FRW->>FRW: 判定未命中横条区域，设置 keepResult = false
    end

    FRW->>FRW: 拦截点击，触发渐隐退场动画 (0.2s)
    FRW->>FRW: 停止音频，卸载鼠标钩子，隐藏窗口
    FRW->>FRW: 恢复前台活动焦点至抽卡前窗口 (SetForegroundWindow)
    FRW->>Core: [Post] @Frw.WindowClosed (FrwClosedPayload: Students, KeepResult)
    deactivate FRW

    activate Core
    Core->>FCB: [Get] @Fcb.ShowWindow (恢复悬浮球显示)
    Core->>Core: 检查配置与暂留意图 (ShowLastResult && KeepResult)
    alt 满足暂留条件 (ShowLastResult && KeepResult)
        Core->>LRW: [Get] @Lrw.ApplyProps + @Lrw.ShowWindow (LrwShowPayload)
        activate LRW
        LRW->>LRW: 绑定学生方块磁贴，自屏幕右侧滑入呈现 (0.3s)
        LRW->>LRW: 启动线性自动关闭进度条倒计时
        opt 倒计时结束或点击 ✕ / 底部关闭按钮
            LRW-->>LRW: 向右滑出隐藏
        end
        deactivate LRW
    else 不暂留或未开启暂留
        Core->>LRW: [Get] @Lrw.HideWindow
    end
    deactivate Core
```

---

### 2. 配置保存与跨窗口实时热更新流程图
```mermaid
sequenceDiagram
    autonumber
    actor User as 用户
    participant WebUI as WebUI 前端 (Vue 3)
    participant Bridge as NativeBridge (COM)
    participant Core as 核心调度 (BackendScheduler)
    participant FCB as 悬浮按钮 (FCB)
    participant FRW as 演播结果窗 (FRW)
    participant LRW as 结果暂留窗 (LRW)
    participant Disk as 物理磁盘 (config.yaml)

    User->>WebUI: 修改配置参数，点击“应用”
    WebUI->>Bridge: SaveConfigYaml(yaml)
    activate Bridge
    Bridge->>Core: BackendScheduler.SaveAndApplyConfig(yaml)
    Core->>Core: 反序列化校验并同步当前班级名单文件
    Core->>Disk: YamlConfigService.SaveConfig 写入 ~/.blue random/config.yaml
    Core->>Core: 更新内存缓存 ConfigCache.Current = Normalize(newConfig)

    par 跨窗口实时热更新广播
        Core->>FCB: [Get] @Fcb.ApplyProps (FloatingButtonConfig)
        Core->>FCB: [Get] @Fcb.ApplyDefaultPickCount (DefaultCount)
        FCB->>FCB: Dispatcher 切回 UI 线程刷新尺寸/透明度/边框/图标/默认人数
    and
        Core->>FRW: [Get] @Frw.ApplyProps (PickResultDialogConfig)
    and
        Core->>LRW: [Get] @Lrw.ApplyProps (PickResultDialogConfig)
        LRW->>LRW: LrwVisualTheme 重新混色并更新底色与不透明度（不主动弹出）
    end

    Core-->>Bridge: 返回保存成功状态
    Bridge-->>WebUI: 返回 true
    deactivate Bridge
```

---

### 3. 悬浮按钮位置记忆与拖拽持久化流程图
```mermaid
flowchart TD
    Start["用户鼠标/触控按住悬浮球"] --> MoveCheck{"移动距离 > 4 像素?"}
    MoveCheck -- 否 --> Click["判定为单次点击，展开/收拢人数选择器"]
    MoveCheck -- 是 --> Drag["鼠标走原生 DragMove / 触控按 DIP 增量平滑移动"]
    Drag --> Release["用户松开指针"]
    Release --> Clamp["ClampPosition 计算工作区边界 (允许透明区越界以紧贴边缘)"]
    Clamp --> SendIpc["Fcb 发送 [Post] @Fcb.PositionChanged(X, Y)"]
    SendIpc --> UpdateCache["BackendScheduler 更新 ConfigCache 内存中 Position 坐标"]
    UpdateCache --> Debounce["600ms 防抖落盘器"]
    Debounce --> SaveDisk["写入磁盘 ~/.blue random/config.yaml 并推送 @Cp.FetchConfig"]
```

---

### 4. 系统托盘菜单控制流程图
```mermaid
flowchart TD
    TrayClick["用户在系统任务栏托盘点击图标"] --> ActionCheck{"交互动作判定"}
    
    ActionCheck -- "左键点击/双击 或 右键点击'配置面板'" --> ShowCP["发送 [Get] @Cp.ShowConfigPanel"]
    ShowCP --> CpHandle["Cp 确保 WebView2 就绪，置顶激活前台并获焦"]

    ActionCheck -- "右键点击'显示悬浮球'" --> ShowFCB["发送 [Get] @Fcb.ShowWindow"]
    ShowFCB --> FcbHandle["Fcb 窗体执行 Show() 并刷新 Topmost"]

    ActionCheck -- "右键点击'重启应用'" --> RestartApp["启动新进程实例，释放托盘并退出"]

    ActionCheck -- "右键点击'退出程序'" --> DestroyAll["发送 [Destroy] @Ipc.Destroy 并释放托盘退出进程"]
```

---

## 五、 载荷数据结构定义

### 1. `FcbPositionPayload`
```csharp
public class FcbPositionPayload
{
    public int MonitorNumber { get; set; } // 显示器序号（预留扩展字段）
    public int PositionX { get; set; }     // 悬浮球窗口左上角 X 坐标
    public int PositionY { get; set; }     // 悬浮球窗口左上角 Y 坐标
}
```

### 2. `FrwShowPayload`
```csharp
public class FrwShowPayload
{
    public int Amount { get; set; }                         // 实际抽取人数
    public List<CalledStudent> CalledStudents { get; set; } // 抽卡命中的学生列表 (含姓名、信封品阶)
}
```

### 3. `FrwClosedPayload`
```csharp
public class FrwClosedPayload
{
    public List<CalledStudent> Students { get; set; } // 本轮抽取命中的学生列表
    public bool KeepResult { get; set; }              // 用户是否主动点击底部横条暂留结果
}
```

### 4. `LrwShowPayload`
```csharp
public class LrwShowPayload
{
    public List<CalledStudent> CalledStudents { get; set; } // 上一轮抽取命中的学生列表
    public DateTime DrawnAt { get; set; }                   // 抽取完成的时间戳 (用于界面展示如 "14:30 抽取")
}
```

---

## 六、 开发者集成与线程安全约束

1. **执行上下文与线程切换**:
   - `IpcBroker.Send` 将消息非阻塞写入 `Channel<IpcMessage>`，由 `IpcBroker` 后台单读取异步分发任务依次调用各订阅者委托。
   - **核心戒律**: 若消费者的逻辑包含 UI 修改（如 `Window.Show()`、修改控件属性、启动 Storyboard），**必须**通过目标窗口自有的 `Dispatcher.Invoke` / `Dispatcher.BeginInvoke`（WPF 窗口）或 `_uiQueue` + `PostMessageW(WM_APP_EXEC)`（`DCompResultWindow`）将操作封送至目标窗口所属的 STA 线程执行，严禁跨线程直接读写非冻结的 UI 元素。

2. **载荷不可变性**:
   - 严禁在 IPC 载荷中传输未冻结的 WPF 元素（如 `Brush`、`UIElement`、`Window` 句柄）。
   - 跨模块只传递纯数据 DTO（如 `string`、`int`、`Payload` 实体），图形对象（如图标、位图）仅能传递已经显式调用 `Freeze()` 冻结后的只读实例。