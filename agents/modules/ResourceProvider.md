# ResourceProvider 模块说明文档

## 概述
`ResourceProvider` 模块位于 `Main/Modules/ResourceProvider`，是 Blue Random 的静态资产全内存缓存托管中心与用户自定义多媒体素材管理引擎。本模块提供零磁盘落盘、零 IO 延迟的 WebUI 前端产物托管、WPF 原生位图与信封素材预加载、GDI+ 私有动态字体表注入（`UI.ttf` 南西新圆体）、用户自定义图标/音频存储去重（`CustomsStore`）以及平滑图标回退解析器（`CustomIconLoader`）。

---

## 功能清单

### 1. 阶段 -1 极速硬加载 (`ResourceCache.EarlyInit`)
- **1. 标题**: `ResourceCache.EarlyInit`
- **2. 用途**: 在任何原生 WPF 窗体（包括启动器 Splash 窗口）实例化之前同步执行。立即将内置 `UI.ttf` 私有加载至当前进程字体表，并预解码应用主图标与 Logo，杜绝窗体出现瞬间的系统默认宋体/黑体形变与白块闪烁。
- **3. 参数**:
  - `baseDir` (`string`): 应用程序基准运行目录（`AppDomain.CurrentDomain.BaseDirectory`）。
  - **返回值** (`void`)。
- **4. 注意事项**:
  - 必须在 `App.OnStartup` 的第一行优先触发。
  - 优先从本地磁盘 `Resources/fonts/UI.ttf` 加载；若在单文件打包模式下找不到物理文件，则无感切换到从程序集清单资源读取字节流并通过 Win32 API `AddFontMemResourceEx` 注入 GDI 内存字体句柄。

---

### 2. 全内存资源预热 (`ResourceCache.Preload`)
- **1. 标题**: `ResourceCache.Preload`
- **2. 用途**: 在启动流水线 Stage 3 中并发预载全部静态资源：将 Vite 构建的 WebUI 静态文件（HTML/CSS/JS/Woff2）灌入内存映射字典、预解码并冻结信封图片（蓝/金/彩）与角色立绘、预缓存抽取音频流。
- **3. 参数**:
  - `baseDir` (`string`): 应用程序运行基目录。
  - **返回值** (`void`)。
- **4. 注意事项**:
  - 自动清理历史上遗留在磁盘 `~/.blue random/webui` 的临时提取目录，保障用户磁盘 100% 洁净，实现零磁盘静态污染。
  - 所有加载的 WPF `BitmapImage` 均强制调用 `Freeze()`，不仅大幅提速渲染，而且允许跨 STA 线程（例如 FCB、FRW 独立 UI 线程）自由共享只读。

---

### 3. 内存提供 WebUI 静态产物 (`ResourceCache.GetWebUiAsset`)
- **1. 标题**: `ResourceCache.GetWebUiAsset`
- **2. 用途**: 响应 WebView2 `WebResourceRequested` 拦截器发起的虚拟域请求（`https://appassets/*`），自内存字典直接返回目标静态文件的二进制字节流。
- **3. 参数**:
  - `relativePath` (`string`): 请求的相对路径（如 `"index.html"`、`"assets/index-xxx.js"`）。
  - **返回值** (`byte[]?`): 资源的原始二进制字节数组；若资源不存在则返回 `null`（供宿主构造 HTTP 404）。
- **4. 注意事项**:
  - 自动规范化路径分隔符，空路径或根路径自动映射为 `"index.html"`。
  - 完全由内存提供响应，杜绝本地 HTTP 端口占用冲突、防火墙拦截与杀软误报。

---

### 4. 获取图片与音频流 (`ResourceCache.GetImage` / `GetEnvelopeImage` / `GetAudioBytes`)
- **1. 标题**: `ResourceCache.GetImage`、`ResourceCache.GetEnvelopeImage` 与 `ResourceCache.GetAudioBytes`
- **2. 用途**: 
  - `GetImage`: 按文件名检索预载并已冻结的 WPF `ImageSource`。
  - `GetEnvelopeImage`: 按信封品阶（`"rainbow"` / `"gold"` / `"blue"`）获取对应的信封纹理位图。
  - `GetAudioBytes`: 获取按键或抽取提示音的原始 WAV/MP3 二进制流。
- **3. 参数**:
  - 传入对应名称或颜色标识，**返回值**为只读冻结的 `ImageSource?` 或 `byte[]?`。
- **4. 注意事项**:
  - 获取失败时提供平滑的默认图标或空流后备，防止上层 UI 抛出空引用异常。

---

### 5. 用户自定义资源仓储 (`CustomsStore`)
- **1. 标题**: `CustomsStore`
- **2. 用途**: 管理用户上传的本地个性化素材（自定义悬浮球图标、抽卡音效、BGM 音频）。持久化存储在 `~/.blue random/customs/{id}.yaml`。
- **3. 参数**:
  - `SaveCustom(string purpose, string fileName, string base64)`: 存储自定义素材并写入元数据。
  - `LoadCustom(string id)`: 按 ID 检索素材实体。
  - `DeleteCustom(string id)`: 删除素材文件。
  - `ConsolidateAndBindLatestResources()`: 智能去重与垃圾清理，对同一种用途仅保留最新上传的一份物理文件，物理删除旧的历史文件。
- **4. 注意事项**:
  - 素材在落盘前会自动剥离 DataURL 前缀，以纯 Base64 或二进制组织。
  - 结合 `ConsolidateAndBindLatestResources` 防止用户反复上传大容量 BGM 音频时磁盘无限积攒垃圾文件。

---

### 6. 自定义图标回退加载器 (`CustomIconLoader`)
- **1. 标题**: `CustomIconLoader`
- **2. 用途**: 封装悬浮按钮图标的多级自适应加载。优先根据配置的自定义图标 ID 从 `CustomsStore` 提取并解码为冻结的 `BitmapImage`；若失败或未配置，则依次平滑回退至内置 `BlueRandom.png` 与 `app.ico`。
- **3. 参数**:
  - `customIconId` (`string?`): 自定义图标唯一标识符。
  - **返回值** (`ImageSource?`): 已完成解码、可跨线程共享的安全图标位图。
- **4. 注意事项**:
  - 解码过程包含完整的 `try-catch` 防护，若用户上传的图像文件损坏或格式不兼容，自动打印警告日志并优雅回退为内置图标，绝不阻塞应用启动或悬浮球呈现。
