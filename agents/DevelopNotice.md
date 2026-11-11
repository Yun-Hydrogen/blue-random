# 项目开发规范

本文件为开发约定速查。命名条目统一按 **类别 — 规范命名 — 示例** 组织。

---

## 一、C# 命名

| 类别 | 规范命名 | 示例 |
|---|---|---|
| 类 / 结构 / 枚举 | PascalCase | `IpcBroker`、`IpcModule`、`LrwShowPayload` |
| 公开方法 / 属性 | PascalCase | `GetAppInfoJson()`、`IsPickerOpen` |
| 私有方法 | PascalCase | `HandleFcbPositionChanged()` |
| 私有实例 / 静态字段 | `_camelCase`（不用 `s_`） | `_lastResults`、`_wndProc`、`_debounceSaveTimer` |
| 局部变量 / 参数 | camelCase | `targetCount`、`allowDuplicate` |
| 常量（业务 / 布局 / 动画） | PascalCase | `SlideDistance`、`CardWidth`、`PanelMs` |
| 常量（Win32 / 外部 API 字面量） | UPPER_SNAKE | `WM_TIMER`、`WS_POPUP`、`IDC_ARROW`、`SWP_NOSIZE` |
| 布尔字段 | `_is` / `_has` / `_can` 前缀 | `_isDrawing`、`_hasDragged`、`_canClose` |
| 模块缩写 | 只首字母大写 | `Fcb`、`Frw`、`Cp`、`Lrw`、`Ui`（非 `FCB` / `UIAccess`） |
| 异步方法 | `Async` 后缀 | `CheckUpdateAsync()`、`FadeOutAndCloseAsync()` |
| P/Invoke 方法 | 保留原生名，不做 Pascal 化 | `Shell_NotifyIcon`、`timeBeginPeriod` |
| XAML `x:Name` | PascalCase | `SlideTransform`、`ProgressBarFill` |

---

## 二、IPC 命名

| 类别 | 规范命名 | 示例 |
|---|---|---|
| 命令字符串 | PascalCase，动词开头 | `"ShowWindow"`、`"ApplyProps"`、`"FetchConfig"` |
| 动作枚举 | PascalCase，不截断单词 | `IpcAction.Destroy`（非 `Dest`） |
| 模块枚举 | 缩写首字母大写 | `IpcModule.Fcb` / `Frw` / `Cp` / `Lrw` |
| 线程名字符串 | `模块_UI_Thread` | `"Fcb_UI_Thread"`、`"Cp_UI_Thread"` |

---

## 三、前端命名（Vue 3 / JavaScript）

| 类别 | 规范命名 | 示例 |
|---|---|---|
| 函数 / 方法 / 变量 | camelCase | `handleApply()`、`sliderLeft` |
| 组件变量 | PascalCase | `TabLogs`、`RizuiMd` |
| 模块级常量 | UPPER_SNAKE | `POLL_INTERVAL`、`MAX_DISPLAY` |
| CSS 类名 | kebab-case | `.chip-tooltip-arrow`、`.cfg-hint` |
| Vue 事件 | kebab-case；`update:` 后 camelCase | `chip-hover`、`update:studentList` |
| props | camelCase，用完整词不用缩写 | `appInfo`、`studentList` |
| 内联样式对象键 | camelCase | `{ borderColor, fontSize }` |

---

## 四、数据契约命名

| 类别 | 规范命名 | 示例 |
|---|---|---|
| `config.yaml` 键（`[YamlMember]`） | lowerCamelCase | `pickCountDialog.defaultCount`、`uiAccessEnabled` |
| 前端 JSON 字段（`[JsonPropertyName]`） | lowerCamelCase | `classId`、`studentList`、`letterColor` |
| 返回 JSON 的桥接方法 | `Get*Json` 后缀 | `GetAppInfoJson()`、`GetClassesJson()` |
| 返回 YAML 的桥接方法 | `*Yaml` 后缀 | `GetConfigYaml()`、`SaveConfigYaml()` |
| 资源文件名 | snake_case（沿用现有资产） | `button_click.wav`、`Letter_Blue.png` |

---

## 五、不可随意改名（外部契约，改名需同步验证）

- **`public/` 下资源文件名** —— `ResourceCache` 按字面量拼路径，改名即找不到文件。
- **`config.yaml` 的 YAML 键** —— 已写入用户磁盘，改名会导致历史配置读不出。
- **前端 JSON 字段名** —— `nativeBridgeAdapter.js` 直接消费。
- **`NativeBridge` 公开方法名** —— WebView2 通过 `hostObjects.nativeBridge` 反射调用，改名必须同步 `src/renderer/bridge/nativeBridgeAdapter.js`。
- **IPC 命令字符串** —— 发送方与接收方必须成对修改（均为进程内，无外部兼容负担）。
- **CSS 类名** —— 模板与 `<style>` 两处必须同步。

---

## 六、自动化约束

`src-csharp/.editorconfig` 已声明私有字段、常量、局部变量/参数的命名规则（级别 `suggestion`，不阻塞构建）。Win32 常量会被常量规则标注为建议项，属预期行为。
