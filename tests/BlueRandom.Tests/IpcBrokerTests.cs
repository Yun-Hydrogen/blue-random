// ============================================================================
// 文件名称: IpcBrokerTests.cs
// 项目名称: BlueRandom.Tests
// 文件说明: 进程内异步消息总线 (IpcBroker) 的单元测试
// 核心职责: 验证跨模块发布/订阅 (Pub/Sub) 模式的消息分发与动作谓词过滤
// ============================================================================

using System;
using System.Threading;
using BlueRandom.Core.Ipc;
using Xunit;

namespace BlueRandom.Tests;

/// <summary>
/// 进程内消息总线单元测试集
/// </summary>
public class IpcBrokerTests
{
    /// <summary>
    /// 验证订阅者能够正确接收发往指定目标模块的命令与负载消息
    /// </summary>
    [Fact]
    public void IpcBroker_PubSub_DeliversMessage()
    {
        string receivedCommand = "";
        object? receivedPayload = null;
        var signal = new ManualResetEventSlim(false);

        IpcBroker.Subscribe(IpcModule.Fcb, msg =>
        {
            receivedCommand = msg.Command;
            receivedPayload = msg.Payload;
            signal.Set();
        });

        IpcBroker.Send(IpcMessage.Create(IpcAction.Get, IpcModule.Fcb, "ShowWindow", "test-payload"));

        bool arrived = signal.Wait(1000);
        Assert.True(arrived);
        Assert.Equal("ShowWindow", receivedCommand);
        Assert.Equal("test-payload", receivedPayload);
    }

    /// <summary>
    /// 验证消息谓词动作过滤机制（如仅处理 Post 动作），防止无用消息干扰业务状态机
    /// </summary>
    [Fact]
    public void IpcBroker_ActionFiltering_Works()
    {
        int count = 0;
        var signal = new ManualResetEventSlim(false);
        IpcBroker.Subscribe(IpcModule.Cp, msg =>
        {
            if (msg.Action == IpcAction.Post)
            {
                Interlocked.Increment(ref count);
                signal.Set();
            }
        });

        IpcBroker.Send(IpcMessage.Create(IpcAction.Get, IpcModule.Cp, "FetchConfig"));
        IpcBroker.Send(IpcMessage.Create(IpcAction.Post, IpcModule.Cp, "ConfigSaved"));

        Thread.Sleep(50);
        bool arrived = signal.Wait(2000);
        Assert.True(arrived);
        Assert.Equal(1, count);
    }
}

