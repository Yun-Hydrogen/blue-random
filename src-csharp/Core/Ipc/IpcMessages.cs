using System;
using System.Collections.Generic;
using BlueRandom.Core.Config;

namespace BlueRandom.Core.Ipc;

/// <summary>
/// IPC 动作语义
/// </summary>
public enum IpcAction
{
    Init,
    Dest,
    Get,
    Post
}

/// <summary>
/// IPC 目标/来源模块
/// </summary>
public enum IpcModule
{
    Ipc,
    Fcb,
    Frw,
    Cp
}

/// <summary>
/// 通用 IPC 消息载体
/// </summary>
public class IpcMessage
{
    public IpcAction Action { get; set; }
    public IpcModule Target { get; set; }
    public string Command { get; set; } = string.Empty;
    public object? Payload { get; set; }

    public static IpcMessage Create(IpcAction action, IpcModule target, string command, object? payload = null)
    {
        return new IpcMessage
        {
            Action = action,
            Target = target,
            Command = command,
            Payload = payload
        };
    }

    public override string ToString() => $"[{Action.ToString().ToUpperInvariant()}] @{Target}.{Command}";
}

// ============================================================================
//  具体命令 Payload 强类型包装
// ============================================================================

public class FcbInitPayload
{
    public FloatingButtonConfig Props { get; set; } = new();
    public int MonitorNumber { get; set; }
    public int? PositionX { get; set; }
    public int? PositionY { get; set; }
}

public class FcbPositionPayload
{
    public int MonitorNumber { get; set; }
    public int PositionX { get; set; }
    public int PositionY { get; set; }
}

public class FrwShowPayload
{
    public int Amount { get; set; }
    public List<CalledStudent> CalledStudents { get; set; } = new();
}
