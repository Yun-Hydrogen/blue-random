// ==============================================================================
// 文件: ConfigPanelWindow.xaml.cs
// 命名空间: BlueRandom.Views.ConfigPanel
// 作用: WebUI 设置面板宿主窗体。
//       承载 WebView2 控件，委托 ConfigPanelBrowserHost 管理浏览器生命周期与通信，
//       并实现窗体隐藏常驻秒开机制与 IPC 窗口级调度。
// ==============================================================================

using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;

namespace BlueRandom.Views.ConfigPanel;

/// <summary>
/// WebUI 配置面板窗体宿主。
/// </summary>
public partial class ConfigPanelWindow : Window
{
    private readonly ConfigPanelBrowserHost _browserHost;

    /// <summary>
    /// 初始化配置面板窗体实例，挂载托盘与程序图标，并初始化浏览器宿主。
    /// </summary>
    public ConfigPanelWindow()
    {
        InitializeComponent();
        var icon = ResourceCache.GetImage("app.ico");
        if (icon != null) Icon = icon;
        _browserHost = new ConfigPanelBrowserHost(WebViewControl, new NativeBridge(this));
        IpcBroker.Subscribe(IpcModule.Cp, OnIpcMessageReceived);
    }

    /// <summary>
    /// 确保 WebView2 运行时环境已完成初始化与前端页面预载（供窗口管理器后台预热调用）。
    /// </summary>
    /// <returns>异步任务 Task。</returns>
    public Task EnsureReadyAsync() => _browserHost.EnsureReadyAsync();

    /// <summary>
    /// 向前端推送更新包下载进度 JSON。
    /// </summary>
    internal Task PushUpdateProgressAsync(string progressJson) => _browserHost.PushUpdateProgressAsync(progressJson);

    /// <summary>
    /// 窗体关闭拦截钩子。
    /// 取消实际销毁操作，改为直接隐藏窗体（Hide），以保持 WebView2 渲染上下文实现秒开体验。
    /// </summary>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        // 隐藏而非销毁，保持 WebView2 状态以秒开
        e.Cancel = true;
        Hide();
    }

    /// <summary>
    /// 响应发往 CP 模块的 IPC 消息。
    /// 调度 ShowConfigPanel 激活前台、FetchConfig/OverwriteConfig 向前端注入最新 YAML，以及 Destroy 彻底销毁。
    /// </summary>
    /// <param name="msg">IPC 消息实体。</param>
    private void OnIpcMessageReceived(IpcMessage msg)
    {
        Dispatcher.Invoke(async () =>
        {
            switch (msg.Command)
            {
                case "ShowConfigPanel":
                    Show();
                    if (!_browserHost.IsReady)
                    {
                        await _browserHost.EnsureReadyAsync();
                    }
                    if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                    Activate();
                    Topmost = true;
                    Topmost = false;
                    Focus();
                    break;

                case "FetchConfig":
                case "OverwriteConfig":
                    if (msg.Payload is string yaml)
                    {
                        await _browserHost.PushConfigUpdatedAsync(yaml);
                    }
                    break;

                case "Destroy":
                    Closing -= OnWindowClosing;
                    Close();
                    break;
            }
        });
    }
}
