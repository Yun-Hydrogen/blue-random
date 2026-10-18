// ==============================================================================
// 文件: NativeBridge.cs
// 命名空间: BlueRandom.UI.CP
// 作用: WebView2 宿主与前端 JavaScript 之间的 COM 跨进程/跨语言互操作桥接层。
//       通过 CoreWebView2.AddHostObjectToScript 注入至前端 window.chrome.webview.hostObjects.nativeBridge，
//       为 WebUI 提供 YAML 配置读写、班级 CRUD、素材持久化、文件选取、提权自启及日志流同步。
// ==============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using BlueRandom.Core;
using BlueRandom.Core.Config;
using BlueRandom.Core.Ipc;
using BlueRandom.Core.Logging;
using BlueRandom.Core.Resources;
using BlueRandom.Core.Scheduler;
using BlueRandom.Services;
using Microsoft.Win32;

namespace BlueRandom.UI.CP;

/// <summary>
/// WebView2 宿主交互桥接层 (NativeBridge)。
/// 通过 COM 可见性（ComVisible/AutoDual）暴露强类型 API 给前端 JavaScript 直接调用。
/// </summary>
[ClassInterface(ClassInterfaceType.AutoDual)]
[ComVisible(true)]
public class NativeBridge
{
    private readonly Window _hostWindow;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 初始化原生互操作桥接对象，绑定宿主窗体引用。
    /// </summary>
    /// <param name="hostWindow">承载 WebView2 的 WPF 窗体实例。</param>
    public NativeBridge(Window hostWindow)
    {
        _hostWindow = hostWindow;
    }

    // ========================================================================
    //  1. 配置交互
    // ========================================================================

    /// <summary>
    /// 获取当前生效的完整配置 YAML 文本（附带完整中文注释）。
    /// </summary>
    /// <returns>格式化 YAML 字符串。</returns>
    public string GetConfigYaml()
    {
        return YamlConfigService.ToYamlWithComments(ConfigCache.Current);
    }

    /// <summary>
    /// 校验并保存来自前端编辑器的 YAML 配置文本。
    /// 触发反序列化语法校验、持久化写盘与热重载广播。
    /// </summary>
    /// <param name="yaml">YAML 文本内容。</param>
    /// <returns>保存并应用成功返回 true，语法损坏或异常返回 false。</returns>
    public bool SaveConfigYaml(string yaml)
    {
        AppLogger.Info("NativeBridge", "收到前端保存配置请求");
        try
        {
            return BackendScheduler.SaveAndApplyConfig(yaml);
        }
        catch (Exception ex)
        {
            AppLogger.Error("NativeBridge", "保存配置异常", ex);
            return false;
        }
    }

    /// <summary>
    /// 恢复出厂默认配置并重置所有数据。
    /// </summary>
    /// <returns>重置成功返回 true。</returns>
    public bool ResetConfig()
    {
        IpcBroker.Send(IpcMessage.Create(IpcAction.Post, IpcModule.Cp, "ConfigReset"));
        bool ok = SecurityService.ResetAllConfig();
        if (ok)
        {
            var cfg = YamlConfigService.LoadConfig();
            ClassManagerService.EnsureInitialized(cfg);
            ConfigCache.Current = YamlConfigService.Normalize(cfg);
            IpcBroker.Send(IpcMessage.Create(IpcAction.Get, IpcModule.Fcb, "ApplyNewProp", ConfigCache.GetFCBProps()));
            IpcBroker.Send(IpcMessage.Create(IpcAction.Get, IpcModule.Frw, "ApplyNewProp", ConfigCache.GetFRWProps()));
            BackendScheduler.RefreshLastResultWindow();
        }
        return ok;
    }

    // ========================================================================
    //  2. 班级管理直连 (data/classes/ 增删查改)
    // ========================================================================

    /// <summary>
    /// 获取包含所有班级列表及当前激活班级 ID 的 JSON 序列化字符串。
    /// </summary>
    /// <returns>JSON 响应文本。</returns>
    public string ListClassesJson()
    {
        var classes = ClassManagerService.ListClasses();
        string activeId = ConfigCache.Current.ActiveClassId;
        if (string.IsNullOrEmpty(activeId) && classes.Count > 0)
        {
            activeId = classes[0].ClassId;
            ConfigCache.Current.ActiveClassId = activeId;
            ConfigCache.Current.StudentList = classes[0].StudentList;
            YamlConfigService.SaveConfig(ConfigCache.Current);
        }

        var result = new
        {
            ok = true,
            activeClassId = activeId,
            classes = classes
        };
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    /// <summary>
    /// 创建新班级，并在全局配置中将其设为当前激活班级。
    /// </summary>
    /// <param name="name">班级显示名称。</param>
    /// <returns>包含操作状态及班级完整实体的 JSON 字符串。</returns>
    public string CreateClass(string name)
    {
        var cls = ClassManagerService.CreateClass(name);
        ConfigCache.Current.ActiveClassId = cls.ClassId;
        ConfigCache.Current.StudentList = cls.StudentList;
        YamlConfigService.SaveConfig(ConfigCache.Current);

        var result = new
        {
            ok = true,
            @class = cls
        };
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    /// <summary>
    /// 重命名指定班级。
    /// </summary>
    /// <param name="classId">班级唯一标识符。</param>
    /// <param name="newName">新班级名称。</param>
    /// <returns>成功返回 true，否则返回 false。</returns>
    public bool RenameClass(string classId, string newName)
    {
        return ClassManagerService.RenameClass(classId, newName);
    }

    /// <summary>
    /// 删除指定班级。若删除的为当前激活班级，则自动降级切换至剩余首个班级或清空。
    /// </summary>
    /// <param name="classId">要删除的班级 ID。</param>
    /// <returns>删除成功返回 true，否则返回 false。</returns>
    public bool DeleteClass(string classId)
    {
        bool ok = ClassManagerService.DeleteClass(classId);
        if (ok && ConfigCache.Current.ActiveClassId == classId)
        {
            var remaining = ClassManagerService.ListClasses();
            if (remaining.Count > 0)
            {
                ConfigCache.Current.ActiveClassId = remaining[0].ClassId;
                ConfigCache.Current.StudentList = remaining[0].StudentList;
            }
            else
            {
                ConfigCache.Current.ActiveClassId = string.Empty;
                ConfigCache.Current.StudentList = new();
            }
            YamlConfigService.SaveConfig(ConfigCache.Current);
        }
        return ok;
    }

    /// <summary>
    /// 加载指定班级的完整配置 JSON（包含学生花名册与抽取权重）。
    /// </summary>
    /// <param name="classId">班级唯一 ID。</param>
    /// <returns>班级实体 JSON 字符串；若不存在则返回 "null"。</returns>
    public string LoadClassJson(string classId)
    {
        var cls = ClassManagerService.LoadClass(classId);
        return cls != null ? JsonSerializer.Serialize(cls, JsonOptions) : "null";
    }

    /// <summary>
    /// 反序列化并保存班级完整配置到对应独立的 class_*.json 文件中。
    /// </summary>
    /// <param name="json">前端传递的 ClassConfig JSON 字符串。</param>
    /// <returns>保存成功返回 true，失败返回 false。</returns>
    public bool SaveClassJson(string json)
    {
        try
        {
            var cls = JsonSerializer.Deserialize<ClassConfig>(json, JsonOptions);
            return cls != null && ClassManagerService.SaveClass(cls);
        }
        catch (Exception ex)
        {
            AppLogger.Error("NativeBridge", "保存班级 JSON 异常", ex);
            return false;
        }
    }

    // ========================================================================
    //  3. 自定义资源 (customs/)
    // ========================================================================

    /// <summary>
    /// 保存自定义资源（如音频、图片或图标）至本地 customs/ 目录与清单文件。
    /// </summary>
    /// <param name="fileName">原始文件名。</param>
    /// <param name="mimeType">MIME 类型。</param>
    /// <param name="purpose">资源用途标签（如 "sound", "avatar", "icon" 等）。</param>
    /// <param name="base64">文件的 Base64 编码数据。</param>
    /// <param name="duration">若为音视频资源，提供其播放时长（秒）。</param>
    /// <returns>保存后的 CustomResourceItem JSON 字符串；失败返回 "null"。</returns>
    public string SaveCustomResource(string fileName, string mimeType, string purpose, string base64, double duration)
    {
        var item = CustomsStore.SaveCustom(fileName, mimeType, purpose, base64, duration);
        return item != null ? JsonSerializer.Serialize(item, JsonOptions) : "null";
    }

    /// <summary>
    /// 获取指定自定义资源项的元数据 JSON。
    /// </summary>
    /// <param name="customId">资源唯一 ID。</param>
    /// <returns>CustomResourceItem JSON 字符串；不存在返回 "null"。</returns>
    public string GetCustomResourceJson(string customId)
    {
        var item = CustomsStore.LoadCustom(customId);
        return item != null ? JsonSerializer.Serialize(item, JsonOptions) : "null";
    }

    /// <summary>
    /// 从磁盘及清单中物理删除指定自定义资源。
    /// </summary>
    /// <param name="customId">资源唯一 ID。</param>
    /// <returns>成功返回 true，失败返回 false。</returns>
    public bool DeleteCustomResource(string customId)
    {
        return CustomsStore.DeleteCustom(customId);
    }

    /// <summary>
    /// 将自定义资源读取并打包转换为可直接在 HTML 渲染的 Base64 Data URL。
    /// </summary>
    /// <param name="customId">资源唯一 ID。</param>
    /// <returns>格式如 "data:image/png;base64,..." 的字符串；失败返回空串。</returns>
    public string GetCustomDataUrl(string customId)
    {
        return CustomsStore.GetDataUrl(customId) ?? string.Empty;
    }

    // ========================================================================
    //  4. 系统与文件操作
    // ========================================================================

    /// <summary>
    /// 调度宿主窗口线程打开系统原生文件选择对话框（OpenFileDialog）。
    /// </summary>
    /// <param name="filter">文件类型过滤规则（如 "音频文件 (*.wav;*.mp3)|*.wav;*.mp3"）。</param>
    /// <returns>用户选中的完整文件路径；若取消则返回空字符串。</returns>
    public string PickFile(string filter)
    {
        string selected = "";
        _hostWindow.Dispatcher.Invoke(() =>
        {
            var dlg = new OpenFileDialog
            {
                Filter = string.IsNullOrEmpty(filter) ? "所有文件 (*.*)|*.*" : filter
            };
            if (dlg.ShowDialog(_hostWindow) == true)
            {
                selected = dlg.FileName;
            }
        });
        return selected;
    }

    /// <summary>
    /// 在资源管理器中打开应用程序的用户数据存储目录 (%USERPROFILE%/.blue random/)。
    /// </summary>
    public void OpenConfigFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = YamlConfigService.GetUserDataDir(),
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Error("NativeBridge", "打开配置文件夹失败", ex);
        }
    }

    /// <summary>
    /// 调用系统默认浏览器安全打开指定的外部网络链接。
    /// </summary>
    /// <param name="url">目标绝对 URI 字符串。</param>
    public void OpenUrl(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("NativeBridge", $"打开外部链接失败: {url}", ex);
        }
    }

    /// <summary>
    /// 清理全内存资源缓存并重置 WebView2 临时渲染与会话文件。
    /// </summary>
    public void ClearCache()
    {
        try
        {
            ResourceCache.Preload(AppDomain.CurrentDomain.BaseDirectory);
            string sessionDir = Path.Combine(YamlConfigService.GetUserDataDir(), "EBWebView");
            if (Directory.Exists(sessionDir))
            {
                try { Directory.Delete(sessionDir, true); } catch { }
            }
            AppLogger.Info("NativeBridge", "ResCache 全内存与前端缓存重置成功");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("NativeBridge", $"清理缓存受阻: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取运行环境核心指标与系统提权状态的 JSON 元数据包。
    /// </summary>
    /// <returns>包含版本号、代号、管理员状态、UIAccess、配置文件路径等的 JSON 串。</returns>
    public string GetAppInfoJson()
    {
        var info = new
        {
            version = AppVersion.Version,
            codename = AppVersion.Codename,
            isAdmin = WindowsSystemService.IsProcessElevated(),
            isUiAccess = WindowsSystemService.IsProcessUiAccess(),
            uiAccessDllExists = WindowsSystemService.DoesUiAccessDllExist(),
            configPath = YamlConfigService.GetConfigFilePath(),
            configDir = YamlConfigService.GetUserDataDir(),
            exePath = Environment.ProcessPath ?? ""
        };
        return JsonSerializer.Serialize(info, JsonOptions);
    }

    /// <summary>
    /// 异步检查云端 GitHub Release 新版本更新。
    /// </summary>
    /// <returns>包含检查结果、最新版本、更新说明与下载地址的 JSON 字符串。</returns>
    public async Task<string> CheckUpdateJson()
    {
        var result = await UpdateService.CheckUpdateAsync();
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    /// <summary>
    /// 读取本地 changelog.md 更新日志内容（优先内嵌资源，次选物理文件）。
    /// </summary>
    /// <returns>changelog.md 完整 Markdown 文本。</returns>
    public string GetChangelog()
    {
        return AppVersion.GetChangelog();
    }

    /// <summary>
    /// 通过 schtasks 命令行服务创建或更新开机自启动计划任务。
    /// </summary>
    /// <param name="taskName">计划任务名称。</param>
    /// <param name="exePath">待启动的可执行文件绝对路径。</param>
    /// <param name="asAdmin">是否以最高特权级别 (HIGHEST) 运行以跨过 UAC 拦截。</param>
    /// <returns>任务创建成功返回 true，否则返回 false。</returns>
    public bool CreateStartupTask(string taskName, string exePath, bool asAdmin)
    {
        return WindowsSystemService.CreateStartupTask(taskName, exePath, asAdmin);
    }

    // ========================================================================
    //  5. 安全管理 (.SHA256)
    // ========================================================================

    /// <summary>
    /// 查询当前系统是否启用了密码保护。
    /// </summary>
    public bool IsSecurityEnabled() => SecurityService.IsEnabled();

    /// <summary>
    /// 校验用户输入的密码哈希是否匹配。
    /// </summary>
    /// <param name="pwd">明文密码。</param>
    public bool VerifyPassword(string pwd) => SecurityService.VerifyPassword(pwd);

    /// <summary>
    /// 设置并哈希持久化安全保护密码。
    /// </summary>
    /// <param name="pwd">新密码明文。</param>
    public bool SetPassword(string pwd) => SecurityService.SetPassword(pwd);

    /// <summary>
    /// 验证旧密码后注销安全保护。
    /// </summary>
    /// <param name="pwd">当前密码明文。</param>
    public bool DisablePassword(string pwd) => SecurityService.DisablePassword(pwd);

    /// <summary>
    /// 重置全量配置到系统出厂缺省状态。
    /// </summary>
    public bool ResetAllConfig() => ResetConfig();

    // ========================================================================
    //  6. 窗口与日志
    // ========================================================================

    /// <summary>
    /// 隐藏配置面板窗口（返回后台常驻或托盘，不销毁进程）。
    /// </summary>
    public void CloseWindow()
    {
        _hostWindow.Dispatcher.Invoke(() => _hostWindow.Hide());
    }

    /// <summary>
    /// 打开当前 WebView2 实例的 Chromium F12 开发者工具窗口。
    /// </summary>
    public void OpenDevTools()
    {
        _hostWindow.Dispatcher.Invoke(() =>
        {
            if (_hostWindow is ConfigPanelWindow cp)
            {
                cp.WebViewControl.CoreWebView2?.OpenDevToolsWindow();
            }
        });
    }

    /// <summary>
    /// 重启当前应用程序进程。
    /// </summary>
    public void Restart()
    {
        string exePath = Environment.ProcessPath ?? "";
        if (!string.IsNullOrEmpty(exePath))
        {
            Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true });
        }
        Environment.Exit(0);
    }

    /// <summary>
    /// 以管理员特权 (runas 动词) 重启启动自身进程，完成提权自愈。
    /// </summary>
    public void AdminElevate()
    {
        string exePath = Environment.ProcessPath ?? "";
        if (!string.IsNullOrEmpty(exePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Verb = "runas",
                UseShellExecute = true
            });
        }
        Environment.Exit(0);
    }

    /// <summary>
    /// 获取当前悬浮球屏幕物理坐标位置的 JSON 描述。
    /// </summary>
    public string GetFloatingPositionJson()
    {
        var pos = ConfigCache.Current.FloatingButton.Position;
        return JsonSerializer.Serialize(new
        {
            x = pos.X ?? 0,
            y = pos.Y ?? 0
        }, JsonOptions);
    }

    /// <summary>
    /// 读取本地运行日志文件，解析为具备时间、等级、哈希键的标准化结构体数组。
    /// </summary>
    /// <param name="maxLines">截取的最大日志行数。</param>
    /// <returns>日志条目对象的 JSON 序列化字符串。</returns>
    public string GetLogsJson(int maxLines)
    {
        try
        {
            string logFile = Path.Combine(YamlConfigService.GetUserDataDir(), "log.txt");
            if (!File.Exists(logFile)) return "[]";

            var lines = File.ReadAllLines(logFile);
            var list = new System.Collections.Generic.List<object>();
            int count = Math.Min(lines.Length, Math.Max(1, maxLines));
            int start = Math.Max(0, lines.Length - count);

            // 保持正序遍历：老日志在前，最新日志在最底部
            for (int i = start; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;

                string level = "info";
                string time = "";
                string text = line;

                if (line.StartsWith("["))
                {
                    int firstClose = line.IndexOf(']');
                    if (firstClose > 1)
                    {
                        string bracketContent = line.Substring(1, firstClose - 1);
                        if (bracketContent.Equals("INIT", StringComparison.OrdinalIgnoreCase))
                        {
                            time = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff");
                            level = "info";
                            text = line;
                        }
                        else
                        {
                            time = bracketContent;
                            if (TimeOnly.TryParse(time, out var to))
                            {
                                time = $"{DateTime.Today:yyyy-MM-dd}T{to:HH\\:mm\\:ss.fff}";
                            }

                            int secondOpen = line.IndexOf('[', firstClose);
                            if (secondOpen > 0)
                            {
                                int secondClose = line.IndexOf(']', secondOpen);
                                if (secondClose > secondOpen)
                                {
                                    string rawLevel = line.Substring(secondOpen + 1, secondClose - secondOpen - 1).ToLowerInvariant();
                                    if (rawLevel == "warn" || rawLevel == "error" || rawLevel == "info")
                                    {
                                        level = rawLevel;
                                    }
                                    text = line.Substring(secondClose + 1).Trim();
                                }
                            }
                        }
                    }
                }

                list.Add(new
                {
                    id = $"{i}_{line.GetHashCode():x8}",
                    time = time,
                    level = level,
                    text = text
                });
            }

            return JsonSerializer.Serialize(list, JsonOptions);
        }
        catch (Exception ex)
        {
            AppLogger.Error("NativeBridge", "读取日志异常", ex);
            return "[]";
        }
    }

    /// <summary>
    /// 接收并转录来自 Web 前端控制台输出的日志至本地宿主统一日志系统中。
    /// </summary>
    /// <param name="level">日志级别（"info" / "warn" / "error"）。</param>
    /// <param name="message">日志正文内容。</param>
    public void Log(string level, string message)
    {
        if (level?.ToUpperInvariant() == "ERROR")
            AppLogger.Error("WebUI", message);
        else if (level?.ToUpperInvariant() == "WARN")
            AppLogger.Warn("WebUI", message);
        else
            AppLogger.Info("WebUI", message);
    }

    // ========================================================================
    //  8. 抽卡结果浮窗 (PickResult) 专用互操作
    // ========================================================================

    /// <summary>
    /// 获取当前抽卡结果浮窗的个性化配置（包含颜色、透明度、立绘开关与自定义音效/BGM 的 Data URL）。
    /// </summary>
    /// <returns>JSON 序列化后的配置对象。</returns>
    public string GetPickResultConfigJson()
    {
        var props = ConfigCache.GetFRWProps();
        string gachaSoundDataUrl = "";
        if (!string.IsNullOrEmpty(props.GachaSoundCustomId))
        {
            var custom = CustomsStore.LoadCustom(props.GachaSoundCustomId);
            if (custom != null && !string.IsNullOrEmpty(custom.Base64))
            {
                string mime = custom.FileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? "audio/wav" : "audio/mpeg";
                gachaSoundDataUrl = $"data:{mime};base64,{custom.Base64}";
            }
        }
        string bgmDataUrl = "";
        if (props.PlayMusic && !string.IsNullOrEmpty(props.BgmCustomId))
        {
            var custom = CustomsStore.LoadCustom(props.BgmCustomId);
            if (custom != null && !string.IsNullOrEmpty(custom.Base64))
            {
                string mime = custom.FileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? "audio/wav" : "audio/mpeg";
                bgmDataUrl = $"data:{mime};base64,{custom.Base64}";
            }
        }

        var dto = new
        {
            defaultPlayGachaSound = props.DefaultPlayGachaSound,
            soundVolume = props.SoundVolume,
            gachaSoundDataUrl = gachaSoundDataUrl,
            playMusic = props.PlayMusic,
            musicVolume = props.MusicVolume,
            bgmStartTime = props.BgmStartTime,
            bgmFadeDuration = props.BgmFadeDuration,
            bgmDataUrl = bgmDataUrl,
            panelOpacity = props.PanelOpacity,
            panelBgColor = props.PanelBgColor,
            panelBorderColor = props.PanelBorderColor
        };
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    /// <summary>
    /// 获取上一轮或当前缓存在浮窗宿主中的抽取学生名单。
    /// </summary>
    /// <returns>JSON 序列化学生列表。</returns>
    public string GetLastResultsJson()
    {
        return JsonSerializer.Serialize(BackendScheduler.LastResults, JsonOptions);
    }

    /// <summary>
    /// 关闭抽卡结果浮窗，隐去窗口并上报 WindowClosed 事件。
    /// </summary>
    public void ClosePickResult()
    {
        _hostWindow.Dispatcher.Invoke(() =>
        {
            if (_hostWindow is BlueRandom.UI.FRW.ResultWindow frw)
            {
                frw.Dismiss();
            }
            else
            {
                _hostWindow.Hide();
                IpcBroker.Send(IpcMessage.Create(IpcAction.Post, IpcModule.Frw, "WindowClosed"));
            }
        });
    }
}
