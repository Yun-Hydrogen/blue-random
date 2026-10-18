// ==============================================================================
// 文件: IpcMessages.cs
// 命名空间: BlueRandom.Core.Ipc
// 作用: 定义应用进程内跨线程/跨窗体（FCB、FRW、CP、IPC Broker）通信的标准契约，
//       包括动作枚举（IpcAction）、目标模块（IpcModule）、通用消息载体（IpcMessage）
//       以及各类强类型 Payload 载荷类。
// ==============================================================================

using System;
using System.Collections.Generic;
using BlueRandom.Core.Config;

namespace BlueRandom.Core.Ipc;

/// <summary>
/// IPC 动作语义类型。
/// 对应通信协议中的操作谓词：初始化（Init）、销毁清理（Dest）、拉取查询（Get）、推送变异（Post）。
/// </summary>
public enum IpcAction
{
    Init,
    Dest,
    Get,
    Post
}

/// <summary>
/// IPC 通信拓扑中的模块标识。
/// </summary>
public enum IpcModule
{
    Ipc,
    Fcb,
    Frw,
    Cp,
    /// <summary>结果暂留浮窗（Last Result Window）。</summary>
    Lrw
}

/// <summary>
/// 通用 IPC 消息实体载体。
/// 封装动作类型、目标模块、指令名称以及任意上下文载荷数据。
/// </summary>
public class IpcMessage
{
    /// <summary>动作谓词。</summary>
    public IpcAction Action { get; set; }

    /// <summary>接收目标模块。</summary>
    public IpcModule Target { get; set; }

    /// <summary>指令方法名称（例如 show、hide、reload 等）。</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>可选载荷对象（可承载强类型 DTO 或基础数据）。</summary>
    public object? Payload { get; set; }

    /// <summary>
    /// 便捷工厂方法，构造标准化 IPC 消息实例。
    /// </summary>
    /// <param name="action">动作谓词。</param>
    /// <param name="target">目标模块。</param>
    /// <param name="command">指令名称。</param>
    /// <param name="payload">可选附加载荷。</param>
    /// <returns>已装配的 IpcMessage 实例。</returns>
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

    /// <summary>
    /// 格式化为可读的日志诊断字符串，例如 "[POST] @Fcb.reload"。
    /// </summary>
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

/// <summary>
/// 结果暂留浮窗展示载荷。
/// </summary>
public class LrwShowPayload
{
    public List<CalledStudent> CalledStudents { get; set; } = new();
    public DateTime DrawnAt { get; set; }
}
