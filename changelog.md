## 未发布

**架构重构:**

1. 项目结构扁平化：`src-csharp` → `Main`，`src/renderer` → `WebUI/ConfigPanel`，`public` → `Resources`，`tests` → `Tests`，新增 `Scripts/`。
2. `Main` 按职责拆分为 `Views/`（仅布局与渲染）与 `Modules/`（Initializer、WebUIBridger、RandomProvider、ConfigProvider、ResourceProvider、ApiBridge、WindowsManager、Common）。
3. 前端依赖与 `node_modules` 收归 `WebUI`，根 `package.json` 仅保留版本、版本代号与脚本；Vite 产物输出至 `WebUI/dist`。
4. 视图业务逻辑下沉到模块：配置面板 WebView2 宿主、悬浮按钮控制器、结果窗音频播放器、结果暂留浮窗数据映射与自动关闭控制器、自定义图标加载。
5. 新增内嵌资源清单回归测试，防止资源目录调整后运行期静默丢图。

**[Version]** 26.10.04 

**[codename]** Midori

## 更新日志


**bug修复:** 

1. 更新js-yaml版本以修复js-yaml在特定版本的漏洞与性能问题。
2. 修复了资源文件更新时，会持续在资源目录积累的问题。

**功能优化:**

1. 使用DirectComposition方案优化结果浮窗的性能
2. 优化前端资源分包加载,提升配置面板加载速度。

**新增功能:**

1. 新增「结果暂留浮窗」：在"结果浮窗"Tab 开启后，抽取结束会在屏幕右上角保留上一轮抽取结果；支持自定义底色/边框/不透明度与自动关闭进度条（0 为不自动关闭），可手动关闭，下一次抽取时自动隐藏并刷新；浮窗从屏幕右缘滑入/滑出。
2. 支持在「悬浮按钮 → 行为」中设置默认抽取人数（1-10 人），保存后立即同步到人数选择器。

**移除功能:**

1. 移除没什么用的阿罗娜&普拉娜两小只。
2. 移除对ClassIsland广播的支持。