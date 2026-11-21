// ============================================================================
// 文件名称: LastResultTriggerTests.cs
// 项目名称: BlueRandom.Tests
// 文件说明: 结果暂留浮窗（LRW）触发逻辑、主动暂留与配置保存防误唤起测试
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using BlueRandom.Modules.ApiBridge;
using BlueRandom.Modules.ConfigProvider;
using BlueRandom.Modules.RandomProvider;
using Xunit;

namespace BlueRandom.Tests;

public class LastResultTriggerTests
{
    [Fact]
    public void SaveAndApplyConfig_DoesNotSendShowWindowToLrw()
    {
        BackendScheduler.Initialize();
        var original = ConfigCache.Current;
        var receivedCommands = new List<string>();
        var sync = new object();

        Action<IpcMessage> handler = msg =>
        {
            lock (sync) { receivedCommands.Add(msg.Command); }
        };

        IpcBroker.Subscribe(IpcModule.Lrw, handler);
        try
        {
            var config = ConfigCache.Current;
            config.PickResultDialog.ShowLastResult = true;
            string yaml = YamlConfigService.ToYamlWithComments(config);

            bool ok = BackendScheduler.SaveAndApplyConfig(yaml);
            Assert.True(ok);

            Thread.Sleep(100);
            lock (sync)
            {
                Assert.DoesNotContain("ShowWindow", receivedCommands);
                Assert.Contains("ApplyProps", receivedCommands);
            }
        }
        finally
        {
            ConfigCache.Current = original;
        }
    }

    [Fact]
    public void WindowClosed_WithKeepResultFalse_DoesNotShowWindow()
    {
        BackendScheduler.Initialize();
        var original = ConfigCache.Current;
        var receivedCommands = new List<string>();
        var sync = new object();

        Action<IpcMessage> handler = msg =>
        {
            lock (sync) { receivedCommands.Add(msg.Command); }
        };

        IpcBroker.Subscribe(IpcModule.Lrw, handler);
        try
        {
            ConfigCache.Current.PickResultDialog.ShowLastResult = true;

            var payload = new FrwClosedPayload
            {
                Students = new List<CalledStudent>
                {
                    new() { Name = "砂狼白子", LetterColor = "rainbow" }
                },
                KeepResult = false
            };

            IpcBroker.Send(IpcMessage.Create(IpcAction.Post, IpcModule.Frw, "WindowClosed", payload));

            Thread.Sleep(100);
            lock (sync)
            {
                Assert.DoesNotContain("ShowWindow", receivedCommands);
                Assert.Contains("HideWindow", receivedCommands);
            }
        }
        finally
        {
            ConfigCache.Current = original;
        }
    }

    [Fact]
    public void WindowClosed_WithKeepResultTrue_SendsShowWindowWhenEnabled()
    {
        BackendScheduler.Initialize();
        var original = ConfigCache.Current;
        var receivedCommands = new List<string>();
        var sync = new object();

        Action<IpcMessage> handler = msg =>
        {
            lock (sync) { receivedCommands.Add(msg.Command); }
        };

        IpcBroker.Subscribe(IpcModule.Lrw, handler);
        try
        {
            ConfigCache.Current.PickResultDialog.ShowLastResult = true;

            var payload = new FrwClosedPayload
            {
                Students = new List<CalledStudent>
                {
                    new() { Name = "早濑优香", LetterColor = "gold" }
                },
                KeepResult = true
            };

            IpcBroker.Send(IpcMessage.Create(IpcAction.Post, IpcModule.Frw, "WindowClosed", payload));

            Thread.Sleep(100);
            lock (sync)
            {
                Assert.Contains("ShowWindow", receivedCommands);
            }
        }
        finally
        {
            ConfigCache.Current = original;
        }
    }
}
