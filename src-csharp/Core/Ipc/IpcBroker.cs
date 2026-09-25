using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BlueRandom.Core.Logging;

namespace BlueRandom.Core.Ipc;

/// <summary>
/// 核心通信总线 (@IPC Broker)
/// 基于 .NET 内置高性能无锁队列 (System.Threading.Channels) 实现跨线程调度
/// </summary>
public static class IpcBroker
{
    private static readonly Channel<IpcMessage> _channel = Channel.CreateUnbounded<IpcMessage>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    private static readonly ConcurrentDictionary<IpcModule, List<Action<IpcMessage>>> _subscribers = new();
    private static CancellationTokenSource? _cts;
    private static Task? _dispatchTask;
    private static bool _isInitialized = false;

    /// <summary>
    /// 初始化 IPC 通信总线并启动后台消息分发循环
    /// </summary>
    public static void Initialize()
    {
        if (_isInitialized) return;
        _isInitialized = true;
        _cts = new CancellationTokenSource();

        _dispatchTask = Task.Run(async () =>
        {
            AppLogger.Info("IPC", "IPC 通信总线已启动分发循环");
            var reader = _channel.Reader;
            try
            {
                while (await reader.WaitToReadAsync(_cts.Token))
                {
                    while (reader.TryRead(out var msg))
                    {
                        DispatchMessage(msg);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
            catch (Exception ex)
            {
                AppLogger.Error("IPC", "消息分发异常", ex);
            }
            AppLogger.Info("IPC", "IPC 通信总线已停止");
        }, _cts.Token);
    }

    /// <summary>
    /// 发布消息到通信总线
    /// </summary>
    public static void Send(IpcMessage message)
    {
        if (!_isInitialized) Initialize();
        _channel.Writer.TryWrite(message);
    }

    /// <summary>
    /// 注册模块消息监听器
    /// </summary>
    public static void Subscribe(IpcModule target, Action<IpcMessage> handler)
    {
        _subscribers.AddOrUpdate(target,
            _ => new List<Action<IpcMessage>> { handler },
            (_, list) =>
            {
                lock (list) { list.Add(handler); }
                return list;
            });
    }

    /// <summary>
    /// 注销监听器
    /// </summary>
    public static void Unsubscribe(IpcModule target, Action<IpcMessage> handler)
    {
        if (_subscribers.TryGetValue(target, out var list))
        {
            lock (list) { list.Remove(handler); }
        }
    }

    /// <summary>
    /// 分发消息到注册的监听者
    /// </summary>
    private static void DispatchMessage(IpcMessage msg)
    {
        if (_subscribers.TryGetValue(msg.Target, out var list))
        {
            Action<IpcMessage>[] handlers;
            lock (list)
            {
                handlers = list.ToArray();
            }

            foreach (var handler in handlers)
            {
                try
                {
                    handler(msg);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("IPC", $"执行消息处理器失败: {msg}", ex);
                }
            }
        }
        else
        {
            AppLogger.Warn("IPC", $"未找到目标模块监听器: {msg}");
        }
    }

    /// <summary>
    /// 安全终止通信总线
    /// </summary>
    public static void Destroy()
    {
        _cts?.Cancel();
        _channel.Writer.TryComplete();
    }
}
