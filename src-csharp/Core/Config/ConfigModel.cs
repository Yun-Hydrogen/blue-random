using System.Collections.Generic;
using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace BlueRandom.Core.Config;

/// <summary>
/// 学生数据条目
/// </summary>
public class StudentItem
{
    [JsonPropertyName("name")]
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("weight")]
    [YamlMember(Alias = "weight")]
    public double Weight { get; set; } = 1.0;

    /// <summary>
    /// 信封颜色级别：rainbow（彩/三星）、gold（金/二星）、blue（蓝/一星）
    /// </summary>
    [JsonPropertyName("letterColor")]
    [YamlMember(Alias = "letterColor")]
    public string LetterColor { get; set; } = "rainbow";
}

/// <summary>
/// 悬浮按钮屏幕坐标
/// </summary>
public class PositionConfig
{
    [YamlMember(Alias = "x")]
    public int? X { get; set; }

    [YamlMember(Alias = "y")]
    public int? Y { get; set; }
}

/// <summary>
/// 悬浮按钮外观与行为配置 (@FCBProps)
/// </summary>
public class FloatingButtonConfig
{
    [YamlMember(Alias = "sizePercent")]
    public int SizePercent { get; set; } = 100;

    [YamlMember(Alias = "transparencyPercent")]
    public int TransparencyPercent { get; set; } = 20;

    [YamlMember(Alias = "alwaysOnTop")]
    public bool AlwaysOnTop { get; set; } = true;

    [YamlMember(Alias = "showInTaskbar")]
    public bool ShowInTaskbar { get; set; } = false;

    [YamlMember(Alias = "position")]
    public PositionConfig Position { get; set; } = new();

    [YamlMember(Alias = "customIconId")]
    public string CustomIconId { get; set; } = string.Empty;

    [YamlMember(Alias = "iconSize")]
    public int IconSize { get; set; } = 48;

    [YamlMember(Alias = "borderColor")]
    public string BorderColor { get; set; } = "#ffffff";
}

/// <summary>
/// 人数选择弹窗配置
/// </summary>
public class PickCountDialogConfig
{
    [YamlMember(Alias = "defaultCount")]
    public int DefaultCount { get; set; } = 1;
}

/// <summary>
/// 抽卡结果弹窗配置 (@FRWProps)
/// </summary>
public class PickResultDialogConfig
{
    [YamlMember(Alias = "defaultPlayGachaSound")]
    public bool DefaultPlayGachaSound { get; set; } = true;

    [YamlMember(Alias = "gachaSoundCustomId")]
    public string GachaSoundCustomId { get; set; } = string.Empty;

    [YamlMember(Alias = "soundVolume")]
    public int SoundVolume { get; set; } = 80;

    [YamlMember(Alias = "playMusic")]
    public bool PlayMusic { get; set; } = false;

    [YamlMember(Alias = "bgmCustomId")]
    public string BgmCustomId { get; set; } = string.Empty;

    [YamlMember(Alias = "musicVolume")]
    public int MusicVolume { get; set; } = 60;

    [YamlMember(Alias = "bgmStartTime")]
    public double BgmStartTime { get; set; } = 0.0;

    [YamlMember(Alias = "bgmFadeDuration")]
    public double BgmFadeDuration { get; set; } = 1.5;

    [YamlMember(Alias = "panelOpacity")]
    public double PanelOpacity { get; set; } = 0.9;

    [YamlMember(Alias = "panelBgColor")]
    public string PanelBgColor { get; set; } = "#ffffff";

    [YamlMember(Alias = "panelBorderColor")]
    public string PanelBorderColor { get; set; } = "#66ccff";

    [YamlMember(Alias = "showDeco")]
    public bool ShowDeco { get; set; } = true;
}

/// <summary>
/// 高级设置（权限与开机启动）
/// </summary>
public class AdminConfig
{
    [YamlMember(Alias = "adminAutoStartAdmin")]
    public bool AdminAutoStartAdmin { get; set; } = true;

    [YamlMember(Alias = "adminAutoStartPath")]
    public string AdminAutoStartPath { get; set; } = string.Empty;

    [YamlMember(Alias = "adminAutoStartTaskName")]
    public string AdminAutoStartTaskName { get; set; } = "Blue Random (Admin)";

    [YamlMember(Alias = "requireAdminOnLaunch")]
    public bool RequireAdminOnLaunch { get; set; } = false;

    [YamlMember(Alias = "uiAccessEnabled")]
    public bool UiAccessEnabled { get; set; } = false;
}

/// <summary>
/// 根配置模型（对应 config.yaml）
/// </summary>
public class RootConfig
{
    /// <summary>
    /// 配置协议版本代号（由 package.json 中的 codename 统一控制）
    /// </summary>
    [JsonPropertyName("protocolVersion")]
    [YamlMember(Alias = "protocolVersion")]
    public string ProtocolVersion { get; set; } = AppVersion.Codename;

    [YamlMember(Alias = "studentList")]
    public List<StudentItem> StudentList { get; set; } = new();

    [YamlMember(Alias = "allowRepeatDraw")]
    public bool AllowRepeatDraw { get; set; } = true;

    [YamlMember(Alias = "agreedEula")]
    public bool AgreedEula { get; set; } = false;

    [YamlMember(Alias = "activeClassId")]
    public string ActiveClassId { get; set; } = string.Empty;

    [YamlMember(Alias = "floatingButton")]
    public FloatingButtonConfig FloatingButton { get; set; } = new();

    [YamlMember(Alias = "pickCountDialog")]
    public PickCountDialogConfig PickCountDialog { get; set; } = new();

    [YamlMember(Alias = "pickResultDialog")]
    public PickResultDialogConfig PickResultDialog { get; set; } = new();

    [YamlMember(Alias = "admin")]
    public AdminConfig Admin { get; set; } = new();
}

/// <summary>
/// 抽出的单个学生实体
/// </summary>
public class CalledStudent
{
    public string Name { get; set; } = string.Empty;
    public string LetterColor { get; set; } = "rainbow"; // rainbow / gold / blue
}
