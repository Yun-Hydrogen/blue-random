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
/// WebView2 宿主交互桥接层 (NativeBridge)
/// 通过 AddHostObjectToScript 注入供前端 JavaScript 直接调用
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

    public NativeBridge(Window hostWindow)
    {
        _hostWindow = hostWindow;
    }

    // ========================================================================
    //  1. 配置交互
    // ========================================================================

    public string GetConfigYaml()
    {
        return YamlConfigService.ToYamlWithComments(ConfigCache.Current);
    }

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
        }
        return ok;
    }

    // ========================================================================
    //  2. 班级管理直连 (data/classes/ 增删查改)
    // ========================================================================

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

    public bool RenameClass(string classId, string newName)
    {
        return ClassManagerService.RenameClass(classId, newName);
    }

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

    public string LoadClassJson(string classId)
    {
        var cls = ClassManagerService.LoadClass(classId);
        return cls != null ? JsonSerializer.Serialize(cls, JsonOptions) : "null";
    }

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

    public string SaveCustomResource(string fileName, string mimeType, string purpose, string base64, double duration)
    {
        var item = CustomsStore.SaveCustom(fileName, mimeType, purpose, base64, duration);
        return item != null ? JsonSerializer.Serialize(item, JsonOptions) : "null";
    }

    public string GetCustomResourceJson(string customId)
    {
        var item = CustomsStore.LoadCustom(customId);
        return item != null ? JsonSerializer.Serialize(item, JsonOptions) : "null";
    }

    public bool DeleteCustomResource(string customId)
    {
        return CustomsStore.DeleteCustom(customId);
    }

    public string GetCustomDataUrl(string customId)
    {
        return CustomsStore.GetDataUrl(customId) ?? string.Empty;
    }

    // ========================================================================
    //  4. 系统与文件操作
    // ========================================================================

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

    public async Task<string> CheckUpdateJson()
    {
        var result = await UpdateService.CheckUpdateAsync();
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    public bool CreateStartupTask(string taskName, string exePath, bool asAdmin)
    {
        return WindowsSystemService.CreateStartupTask(taskName, exePath, asAdmin);
    }

    // ========================================================================
    //  5. 安全管理 (.SHA256)
    // ========================================================================

    public bool IsSecurityEnabled() => SecurityService.IsEnabled();
    public bool VerifyPassword(string pwd) => SecurityService.VerifyPassword(pwd);
    public bool SetPassword(string pwd) => SecurityService.SetPassword(pwd);
    public bool DisablePassword(string pwd) => SecurityService.DisablePassword(pwd);
    public bool ResetAllConfig() => ResetConfig();

    // ========================================================================
    //  6. 窗口与日志
    // ========================================================================

    public void CloseWindow()
    {
        _hostWindow.Dispatcher.Invoke(() => _hostWindow.Hide());
    }

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

    public void Restart()
    {
        string exePath = Environment.ProcessPath ?? "";
        if (!string.IsNullOrEmpty(exePath))
        {
            Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true });
        }
        Environment.Exit(0);
    }

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

    public string GetFloatingPositionJson()
    {
        var pos = ConfigCache.Current.FloatingButton.Position;
        return JsonSerializer.Serialize(new
        {
            x = pos.X ?? 0,
            y = pos.Y ?? 0
        }, JsonOptions);
    }

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

    public void Log(string level, string message)
    {
        if (level?.ToUpperInvariant() == "ERROR")
            AppLogger.Error("WebUI", message);
        else if (level?.ToUpperInvariant() == "WARN")
            AppLogger.Warn("WebUI", message);
        else
            AppLogger.Info("WebUI", message);
    }
}
