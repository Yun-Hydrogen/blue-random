using System;
using System.Threading;
using BlueRandom.Core.Ipc;
using Xunit;

namespace BlueRandom.Tests;

public class IpcBrokerTests
{
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

